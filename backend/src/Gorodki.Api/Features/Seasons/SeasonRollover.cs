using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Realtime;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Territory;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Seasons;

/// <summary>
/// Смена сезона (PLAN.md, §3.4; решено 25.09 в #30) — задача Hangfire по расписанию (<c>season-rollover</c>, каждый час
/// в :00 по Минску, то есть и ровно в полночь начала сезона). Идёт параллельно обработчику захватов, а меняет землю —
/// поэтому берёт те же блокировки тайлов, что захват, визиты, откат и удаление аккаунта (docs/architecture/jobs.md).
/// </summary>
/// <remarks>
/// <para>
/// <b>Мягкий сброс</b> (<see cref="SeasonReset.Soft"/>): уровни → 1, «последний визит» сдвигается, щиты, осада и окно снятия
/// уровней снимаются. <b>Чистая карта</b> (у сезона <see cref="SeasonEntity.CleanStart"/>, это Сезон 0): вся земля, зоны
/// «спорная» и журнал захватов стираются — земля полевых тестов преимущества не даёт. Очки → 0 делать не нужно: у новых
/// начислений новый номер сезона (<c>ScoreBook</c>), старые остаются историей.
/// </para>
/// <para>
/// <b>Журнал захватов сбрасывается той же формулой</b>, что и земля (земля «до» и «после» каждого захвата за 7 дней).
/// Иначе захват, ещё скрытый задержкой в момент сброса, стал бы виден раньше границы: публичная проекция откатывает его,
/// только если земля и сейчас такая, какой её оставил захват, — а сброс её поменял. Со сброшенным журналом проекция
/// показывает сброшенный мир без скрытого захвата, а откат нарушителя возвращает жертве сброшенную землю.
/// </para>
/// <para>
/// <b>Безопасность.</b> Блокировки всех тайлов с версией (вся земля и весь журнал лежат только в таких тайлах) — по лигам и
/// по порядку, как у удаления аккаунта, поэтому взаимной блокировки нет. Сброс земли, журнала и рост версий тайлов — один
/// SQL-оператор (общий снимок базы): захват в совсем новом тайле, который идёт в этот момент, попадает в сброс целиком
/// или не попадает вовсе. <b>Идемпотентность:</b> строка сезона берётся <c>FOR UPDATE</c>, отметка
/// <see cref="SeasonEntity.ResetAt"/> ставится в той же транзакции — повтор задачи (Hangfire, второй экземпляр сервера)
/// второй раз не сбросит. Версии тайлов растут — телефоны перечитают карту.
/// </para>
/// </remarks>
public sealed class SeasonRollover(
    AppDbContext db, SeasonStore seasons, GameConfigStore configs, RealtimeHints hints, TimeProvider time, ILogger<SeasonRollover> logger)
{
    /// <summary>
    /// Если идущий сезон начался, а смена на него ещё не выполнена, — выполняет её. Возвращает, у скольких тайлов выросла
    /// версия (0 — сезоны не начались, смена уже была или земли нет вовсе). Смена на пропущенный сезон не догоняется:
    /// выполняется только смена на идущий (мягкий сброс идущего сезона её заменяет).
    /// </summary>
    public async Task<int> RunIfDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if ((await seasons.CalendarAsync(cancellationToken)).At(now) is not { } season
            || await db.Seasons.AsNoTracking().AnyAsync(s => s.Number == season.Number && s.ResetAt != null, cancellationToken))
        {
            return 0;
        }

        var rules = (await configs.GetCurrentAsync(cancellationToken)).Rules.Territory.ToRules();
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Захват держит тайлы до 15 с (его statement_timeout): ждём дольше, чем он. Не дождались — Hangfire повторит.
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '30s'; SET LOCAL statement_timeout = '120s'", cancellationToken);

        // Второй экземпляр задачи ждёт здесь и дальше видит отметку. Без LINQ поверх: блокировка — в самом запросе.
        var row = (await db.Seasons
                .FromSql($"SELECT * FROM app.seasons WHERE number = {season.Number} FOR UPDATE")
                .AsNoTracking()
                .ToListAsync(cancellationToken))
            .Single();
        if (row.ResetAt is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return 0;
        }

        // Блокировки тайлов — по лигам и по порядку тайлов, как у удаления аккаунта и захвата.
        var known = await db.TileVersions.AsNoTracking().Select(v => new { v.League, v.TileX, v.TileY }).ToListAsync(cancellationToken);
        foreach (var tile in known.OrderBy(t => t.League).ThenBy(t => new TileKey(t.TileX, t.TileY)))
        {
            var lockSpace = 100 + (int)tile.League;
            var lockKey = new TileKey(tile.TileX, tile.TileY).LockKey;
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({lockSpace}, {lockKey})", cancellationToken);
        }

        var bumped = row.CleanStart
            ? await WipeAsync(cancellationToken)
            : await SoftResetAsync(season.StartsAt, rules.DecayInterval, cancellationToken);
        await db.Seasons
            .Where(s => s.Number == season.Number)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.ResetAt, now), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        foreach (var league in bumped.GroupBy(t => t.League))
        {
            hints.TilesChanged(league.Key, league.Select(t => t.Tile)); // смена сезона публична для всех сразу
        }

        logger.LogInformation(
            "Смена на сезон {Season}: {Kind}, версии {Tiles} тайлов выросли",
            season.Number,
            row.CleanStart ? "чистая карта" : "мягкий сброс",
            bumped.Count);
        return bumped.Count;
    }

    /// <summary>
    /// Мягкий сброс всей земли и всего журнала захватов (<see cref="SeasonReset.Soft"/> — та же формула) и рост версий всех
    /// тайлов — одним оператором.
    /// </summary>
    private Task<List<(League League, TileKey Tile)>> SoftResetAsync(
        DateTimeOffset seasonStart, TimeSpan decayInterval, CancellationToken cancellationToken) =>
        ChangedTilesAsync(
            db.Database.SqlQuery<string>(
                $"""
                WITH parcels AS (
                    UPDATE app.parcels SET
                        last_visit_at = GREATEST(last_visit_at, LEAST({seasonStart}, last_visit_at + ((level - 1) * {decayInterval}))),
                        level = 1, shield_until = NULL, siege_until = NULL, loss_window_since = NULL, loss_attackers = ARRAY[]::uuid[]
                    RETURNING 1
                ), journal AS (
                    UPDATE app.capture_journal_pieces SET
                        last_visit_at = GREATEST(last_visit_at, LEAST({seasonStart}, last_visit_at + ((level - 1) * {decayInterval}))),
                        level = 1, shield_until = NULL, siege_until = NULL, loss_window_since = NULL, loss_attackers = ARRAY[]::uuid[]
                    RETURNING 1
                ), versions AS (
                    UPDATE app.tile_versions SET version = version + 1 RETURNING league, tile_x, tile_y
                )
                SELECT concat_ws(':', league, tile_x, tile_y) AS "Value" FROM versions
                """),
            cancellationToken);

    /// <summary>Чистая карта: земля, зоны «спорная» и журнал захватов (куски журнала — каскадом) стираются, версии растут.</summary>
    private Task<List<(League League, TileKey Tile)>> WipeAsync(CancellationToken cancellationToken) =>
        ChangedTilesAsync(
            db.Database.SqlQuery<string>(
                $"""
                WITH parcels AS (
                    DELETE FROM app.parcels RETURNING 1
                ), zones AS (
                    DELETE FROM app.contested_zones RETURNING 1
                ), journal AS (
                    DELETE FROM app.capture_journal RETURNING 1
                ), versions AS (
                    UPDATE app.tile_versions SET version = version + 1 RETURNING league, tile_x, tile_y
                )
                SELECT concat_ws(':', league, tile_x, tile_y) AS "Value" FROM versions
                """),
            cancellationToken);

    private static async Task<List<(League League, TileKey Tile)>> ChangedTilesAsync(
        IQueryable<string> query, CancellationToken cancellationToken) =>
        [.. (await query.ToListAsync(cancellationToken))
            .Select(row => row.Split(':').Select(int.Parse).ToArray())
            .Select(parts => ((League)parts[0], new TileKey(parts[1], parts[2])))];
}
