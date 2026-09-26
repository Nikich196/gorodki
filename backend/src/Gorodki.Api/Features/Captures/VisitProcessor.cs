using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Fog;
using Gorodki.Api.Features.Realtime;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Features.Scoring;
using Gorodki.Api.Features.Seasons;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Runs;
using Gorodki.Domain.Territory;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Gorodki.Api.Features.Captures;

/// <summary>
/// Визиты (PLAN.md, §3.3): «визит — ≥50 м следа внутри участка или повторный захват; +1 уровень не чаще раза в 20 ч;
/// не считаются первые и последние 200 м забега». Без визитов угасание заставляло бы перезахватывать свою же землю.
/// Один раз за забег — когда он завершён и все точки на месте (как туман). Путь — только принятый судьёй отрезков.
/// </summary>
/// <remarks>
/// Расчёт — без блокировок; запись — короткая транзакция под блокировками тайлов, куски перечитываются и визит
/// применяется к их свежему состоянию (визит — функция состояния и времени). Кусок, который с тех пор пересобрал захват
/// (другой номер), пропускается. Путь внутри приватных зон игрока (§3.16) не считается. Пока в тайлах, по которым прошёл
/// путь, скрыт захват, в журнале которого есть земля игрока, визиты ждут его раскрытия — иначе скрытую петлю выдал бы шов.
/// </remarks>
public sealed class VisitProcessor(
    AppDbContext db, RunJudgements judgements, GameConfigStore configs, SeasonStore seasons, RealtimeHints hints, TimeProvider time)
{
    /// <summary>
    /// Забеги, готовые к подсчёту визитов, — сначала закончившиеся раньше. Не раньше, чем конец забега станет публичным
    /// (<see cref="TerritoryReader.PublicHorizon"/>: «сейчас − 20 минут» вниз до 5 минут, §3.16): визит меняет землю и
    /// версию тайла, и раньше он выдал бы «игрок только что пробежал здесь». Конец — по часам сервера: у телефона с
    /// отстающими часами он иначе «в прошлом» уже в момент завершения. Забеги демо-аккаунта — сразу, как его захваты.
    /// Забег, визиты которого не посчитались из-за ошибки, ждёт своей паузы (<see cref="PostponeAsync"/>), а забег, по
    /// тайлам которого ещё скрыт захват земли игрока, — раскрытия этого захвата (<see cref="ProcessRunAsync"/>).
    /// </summary>
    public async Task<List<Guid>> RunsReadyAsync(int take, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var closedBefore = now - FogProcessor.ClosedRunGrace;
        var horizon = TerritoryReader.PublicHorizon(now, await DelayAsync(cancellationToken));
        return await db.Runs.AsNoTracking()
            .Where(r => r.VisitsProcessedAt == null
                && r.PointsPurgedAt == null
                && r.PrefixEndSeq >= 0
                && (r.VisitsRetryAt == null || r.VisitsRetryAt <= now)
                && ((r.Status == RunStatus.Finished && r.LastSeq != null && r.PrefixEndSeq >= r.LastSeq)
                    || (r.Status != RunStatus.Active && r.EndedAt < closedBefore))
                && (r.EndedAt!.Value.AddSeconds(-r.ClockSkewMs / 1000.0) <= horizon
                    || db.Users.Any(u => u.Id == r.UserId && u.Role == UserRole.Demo)))
            .OrderBy(r => r.EndedAt)
            .Select(r => r.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Визиты забега не посчитались из-за ошибки: забег откладывается на <see cref="CaptureWorker.RetryDelay"/>, чтобы
    /// не стоять первым в очереди и не обрывать проход.
    /// </summary>
    public async Task PostponeAsync(Guid runId, CancellationToken cancellationToken)
    {
        var failures = await db.Runs.Where(r => r.Id == runId).Select(r => r.VisitsFailures).SingleAsync(cancellationToken) + 1;
        var retryAt = time.GetUtcNow() + CaptureWorker.RetryDelay(failures);
        await db.Runs
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(
                set => set.SetProperty(r => r.VisitsFailures, failures).SetProperty(r => r.VisitsRetryAt, retryAt),
                cancellationToken);
    }

    private async Task<TimeSpan> DelayAsync(CancellationToken cancellationToken) =>
        TimeSpan.FromMinutes((await configs.GetCurrentAsync(cancellationToken)).Rules.Privacy.PublicEventDelayMinutes);

    /// <summary>Публичен ли уже конец забега: его конец по часам сервера — не позже границы публичности.</summary>
    private static bool EndIsPublic(DateTimeOffset endedAt, long clockSkewMs, DateTimeOffset horizon) =>
        endedAt.AddSeconds(-clockSkewMs / 1000.0) <= horizon;

    /// <summary>
    /// Когда применён самый поздний ещё скрытый от всех захват (<see cref="TerritoryReader.HiddenJournal"/>) в этих
    /// тайлах, в журнале которого — до или после — есть земля игрока; null — такого нет.
    /// </summary>
    /// <remarks>
    /// Пока такой захват скрыт, визиты забега не засчитываются (docs/architecture/territory-map.md). Иначе они легли бы
    /// на куски после захвата: у жертвы — на треснувшую часть и остаток (у автора — на освежённую землю и остаток его
    /// куска), каждому своё время визита и свой порог 50 м. Публичная проекция откатывает захват по граням следа и
    /// остаток вне следа с возвращённой землёй не сливает — скрытая петля была бы видна по шву раньше 20 минут.
    /// Автора, чья земля в журнале, это касается так же. Петлю своего живого забега он обычно не ждёт: она публична не
    /// позже, чем конец забега (кроме петли, применённой после финиша, — до одного шага раскрытия). Ждут его забег из
    /// офлайна (захват применён позже конца) и его прежний забег по тем же тайлам, пока скрыт захват следующего.
    /// </remarks>
    private async Task<DateTimeOffset?> HiddenCaptureOfOwnLandAsync(
        RunEntity run, IReadOnlySet<TileKey> tiles, DateTimeOffset horizon, CancellationToken cancellationToken)
    {
        int minX = tiles.Min(t => t.X), maxX = tiles.Max(t => t.X), minY = tiles.Min(t => t.Y), maxY = tiles.Max(t => t.Y);
        var hidden = await TerritoryReader.HiddenJournal(db, run.League, horizon)
            .Where(j => j.TileX >= minX && j.TileX <= maxX && j.TileY >= minY && j.TileY <= maxY
                && db.CaptureJournalPieces.Any(p => p.CaptureId == j.CaptureId && p.TileX == j.TileX && p.TileY == j.TileY
                    && p.OwnerId == run.UserId))
            .Select(j => new { j.TileX, j.TileY, j.AppliedAt })
            .ToListAsync(cancellationToken);
        return hidden.Where(j => tiles.Contains(new TileKey(j.TileX, j.TileY))).Max(j => (DateTimeOffset?)j.AppliedAt);
    }

    /// <summary>
    /// Засчитывает визиты забега. Возвращает, сколько кусков освежено, или null — забег уже обработан или ещё рано
    /// (не прошла публичная задержка после его конца или в тайлах его пути ещё скрыт захват земли игрока).
    /// </summary>
    public async Task<int?> ProcessRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null || run.VisitsProcessedAt is not null || run.PrefixEndSeq < 0 || run.PointsPurgedAt is not null)
        {
            return null;
        }

        var delay = await DelayAsync(cancellationToken);
        var horizon = TerritoryReader.PublicHorizon(time.GetUtcNow(), delay);
        var demo = await db.Users.AnyAsync(u => u.Id == run.UserId && u.Role == UserRole.Demo, cancellationToken);
        if (!demo && (run.EndedAt is not { } endedAt || !EndIsPublic(endedAt, run.ClockSkewMs, horizon)))
        {
            return null;
        }

        var (_, judgement) = await judgements.JudgeAsync(run, cancellationToken);
        var current = (await configs.GetCurrentAsync(cancellationToken)).Rules; // карта общая — правила земли на момент визита
        var rules = current.Territory.ToRules();
        var segments = JudgedPath.Segments(judgement.Points, judgement.Verdicts).ToList();
        var acceptedMeters = Math.Round(JudgedPath.Length(segments), 1); // пробег — весь путь, без обрезки
        var path = Visits.TrimmedPath(segments, current.Privacy.TrimMeters);

        // Дистанция для очков (§3.5) — засчитанный путь без первых и последних 200 м (§3.16: «обрезка 200 м — для треков,
        // визитов и начислений»). Приватные зоны здесь не вычитаются: число километров не говорит, где бегали. Повтор
        // записанного забега (демо) дистанцию второй раз не даёт.
        var distanceMeters = run.Source == RunSource.Live ? path.Sum(s => s.From.Distance(s.To)) : 0;
        var startedAt = run.StartedAt.AddMilliseconds(-run.ClockSkewMs); // по часам сервера, как у сезонного тумана
        var season = (await seasons.CalendarAsync(cancellationToken)).At(startedAt)?.Number;

        // Свои куски в тайлах, которые задевает путь: сколько пути прошло внутри каждого.
        var candidates = new List<(long Id, DateTimeOffset At, TileKey Tile)>();
        if (path.Count > 0)
        {
            var envelope = new Envelope();
            foreach (var step in path)
            {
                envelope.ExpandToInclude(step.From);
                envelope.ExpandToInclude(step.To);
            }

            var tiles = TileKey.Covering(envelope);
            int minX = tiles.Min(t => t.X), maxX = tiles.Max(t => t.X), minY = tiles.Min(t => t.Y), maxY = tiles.Max(t => t.Y);

            // Тайлы, по которым прошёл засчитанный путь, — не весь прямоугольник вокруг него: захват в тайле, куда игрок не
            // заходил, визиты не задерживает. Ждёт и забег без визитов: их может не быть как раз потому, что землю взяли, —
            // засчитай его сразу, и жертва по своему пути узнала бы, какой кусок тронут.
            var walked = path.SelectMany(s => TileKey.Covering(new Envelope(s.From, s.To))).ToHashSet();
            if (await HiddenCaptureOfOwnLandAsync(run, walked, horizon, cancellationToken) is { } appliedAt)
            {
                // Ещё рано, это не ошибка (счётчик ошибок не растёт): визиты ждут раскрытия захвата и лягут на публичные
                // куски, без шва. До раскрытия забег не берётся в очередь — и не судится заново каждые 5 секунд. Одно
                // ожидание — до раскрытия одного захвата: не позже чем через задержку и шаг раскрытия (≤ 25 минут) после
                // применения. Но ожидания сцепляются, пока в этих тайлах применяются новые захваты земли игрока (серия его
                // забегов по своему району, людное место): тогда ждёт вся серия плюс до 25 минут. Жёсткий предел — стирание
                // точек через 14 дней: забег уходит из очереди без визитов.
                var publicAt = TerritoryReader.PublicAt(appliedAt, delay);
                await db.Runs
                    .Where(r => r.Id == runId && r.VisitsProcessedAt == null)
                    .ExecuteUpdateAsync(set => set.SetProperty(r => r.VisitsRetryAt, publicAt), cancellationToken);
                return null;
            }

            var own = await db.Parcels.AsNoTracking()
                .Where(p => p.OwnerId == run.UserId && p.League == run.League
                    && p.TileX >= minX && p.TileX <= maxX && p.TileY >= minY && p.TileY <= maxY)
                .ToListAsync(cancellationToken);
            var zones = await db.PrivacyZones.AsNoTracking()
                .Where(z => z.UserId == run.UserId)
                .Select(z => new { z.Latitude, z.Longitude })
                .ToListAsync(cancellationToken);
            var excluded = PrivacyZones.Area(
                [.. zones.Select(z => Utm34.Forward(z.Latitude, z.Longitude)).Select(c => new Coordinate(c.Easting, c.Northing))],
                current.Privacy.ZoneRadiusMeters);
            var inside = Visits.Inside(path, own.Select(p => p.Geometry).ToList(), excluded);
            candidates = inside
                .Where(v => v.Value.Meters >= current.Territory.VisitMinMeters)
                .Select(v => (
                    own[v.Key].Id,
                    DateTimeOffset.FromUnixTimeMilliseconds(v.Value.LastTimeMs - run.ClockSkewMs), // время по часам сервера
                    new TileKey(own[v.Key].TileX, own[v.Key].TileY)))
                .ToList();
        }

        var now = time.GetUtcNow();
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '15s'", cancellationToken);

        // Блокировка игрока — до тайлов, как у захвата (порядок один — взаимной блокировки нет): суточный потолок дистанции
        // считается под ней, и два забега игрока, посчитанные одновременно, его не превысят.
        var userKey = run.UserId.ToString();
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(1, hashtext({userKey}))", cancellationToken);
        var lockSpace = 100 + (int)run.League;
        foreach (var tile in candidates.Select(c => c.Tile).Distinct().Order())
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({lockSpace}, {tile.LockKey})", cancellationToken);
        }

        // Визит раньше начала идущего сезона, а смена на него уже прошла (забег кончился перед полуночью — визиты считаются
        // после границы публичности): изменённый визитом кусок проходит мягкий сброс, как если бы визит засчитали до смены
        // (SeasonReset.Late). Иначе он поднял бы уровень уже сброшенной земли. Под блокировками тайлов — смена их тоже берёт.
        var resetSeasonStart = (await seasons.ResetSeasonAsync(now, cancellationToken))?.StartsAt;
        var ids = candidates.Select(c => c.Id).ToList();
        var fresh = await db.Parcels.Where(p => ids.Contains(p.Id) && p.OwnerId == run.UserId).ToListAsync(cancellationToken);
        var changedTiles = new HashSet<TileKey>();
        var visitedCount = 0;
        foreach (var parcel in fresh)
        {
            var at = candidates.Single(c => c.Id == parcel.Id).At;
            var state = CaptureProcessor.ToParcel(parcel).State;
            if (SeasonReset.Late(state, CaptureRules.Visit(state, at, rules), at, resetSeasonStart, rules) is not { } visited
                || visited == state)
            {
                continue; // земля уже угасла (вернуть можно только захватом) или визит ничего не меняет
            }

            parcel.Level = (short)visited.Level;
            parcel.LastVisitAt = visited.LastVisitAt;
            parcel.LastLevelUpAt = visited.LastLevelUpAt;
            parcel.TouchedAt = visited.TouchedAt;
            changedTiles.Add(new TileKey(parcel.TileX, parcel.TileY));
            visitedCount++;
        }

        if (distanceMeters > 0)
        {
            await ScoreBook.AddDistanceAsync(db, run, distanceMeters, startedAt, season, current.Scoring, now, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var tile in changedTiles)
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO app.tile_versions (league, tile_x, tile_y, version) VALUES ({(short)run.League}, {tile.X}, {tile.Y}, 1)
                ON CONFLICT (league, tile_x, tile_y) DO UPDATE SET version = app.tile_versions.version + 1
                """,
                cancellationToken);
        }

        var marked = await db.Runs
            .Where(r => r.Id == runId && r.VisitsProcessedAt == null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(r => r.VisitsProcessedAt, now)
                    .SetProperty(r => r.VisitedParcels, visitedCount)
                    .SetProperty(r => r.AcceptedMeters, acceptedMeters),
                cancellationToken);
        if (marked == 0)
        {
            await transaction.RollbackAsync(cancellationToken); // другой проход успел раньше
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        if (changedTiles.Count > 0)
        {
            hints.TilesChanged(run.League, changedTiles); // визит засчитан уже после публичной задержки
        }

        return visitedCount;
    }
}
