using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Fog;
using Gorodki.Domain.Time;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Leaderboards;

/// <summary>
/// Рейтинг «Кто открыл больше» (PLAN.md, §3.10: сезон и всё время, «Пешком / Вело / Всего», только числа). Места
/// переснимаются каждые 5 минут — задача Hangfire <c>exploration-snapshot</c> (docs/plan-deviations.md, №15: «всё открыто и
/// работает сразу»; раньше — раз в сутки после полуночи).
/// </summary>
/// <remarks>
/// <para>
/// <b>Граница публичности (§3.16).</b> Живой рейтинг выдавал бы «её гектары только что выросли — она только что бегала».
/// Поэтому туман игрока, который открывал его позже границы публичности (<see cref="TerritoryReader.PublicHorizon"/>, те же
/// 20 минут шагами по 5, что у карты), в проход не берётся: у него остаются места его прошлого среза. Новые гектары
/// появляются в рейтинге с той же задержкой, что захват на карте, а свои игрок видит сразу — в <c>GET /fog/summary</c>.
/// Демо-аккаунта это не касается: на показе его изменения видны сразу, как на карте (§11).
/// </para>
/// <para>
/// <b>Хранение.</b> Строки текущих суток заменяются каждым проходом, за прошлые сутки остаётся их последний проход; хранятся
/// <see cref="KeepDays"/> дней. Проход идёт под блокировкой суток (4) — второй экземпляр сервера дублей не даст, а очистка
/// истории исследований (<see cref="Fog.FogHistory"/>) не разминётся со срезом.
/// </para>
/// </remarks>
public sealed class LeaderboardSnapshots(
    AppDbContext db, GameConfigStore configs, TimeProvider time, ILogger<LeaderboardSnapshots> logger)
{
    /// <summary>Сколько дней хранить срезы прошлых суток.</summary>
    public const int KeepDays = 7;

    /// <summary>
    /// Переснимает места за текущие игровые сутки (задача по расписанию, каждые 5 минут). Возвращает число записанных строк.
    /// </summary>
    public async Task<int> RefreshAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var day = GameClock.GameDayOf(now);
        var delay = TimeSpan.FromMinutes((await configs.GetCurrentAsync(cancellationToken)).Rules.Privacy.PublicEventDelayMinutes);
        var horizon = TerritoryReader.PublicHorizon(now, delay);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(4, {day.DayNumber})", cancellationToken);

        // Туман и признак «открывал туман позже границы» — одним запросом, то есть одним снимком базы: туман забега, который
        // открывается в эту секунду, либо не виден запросу вовсе, либо виден вместе с отметкой забега — и игрок придержан.
        var tiles = await db.FogTiles.AsNoTracking()
            .Select(f => new
            {
                f.UserId,
                f.Layer,
                f.Season,
                f.TileX,
                f.TileY,
                f.CellCount,
                Fresh = db.Runs.Any(r => r.UserId == f.UserId && r.FogStampedAt > horizon)
                    && !db.Users.Any(u => u.Id == f.UserId && u.Role == UserRole.Demo),
            })
            .ToListAsync(cancellationToken);
        var areas = tiles
            .Where(t => !t.Fresh)
            .GroupBy(t => (t.UserId, t.Layer, t.Season))
            .Select(g => (g.Key.UserId, Layer: (LeaderboardLayer)g.Key.Layer, g.Key.Season,
                Area: g.Sum(t => t.CellCount * FogTileCodec.CellAreaSquareMeters(new FogTileKey(t.TileX, t.TileY)))))
            .ToList();
        // «Всего» — сумма «Пешком» и «Вело» (§3.10).
        areas.AddRange(areas
            .GroupBy(a => (a.UserId, a.Season))
            .Select(g => (g.Key.UserId, Layer: LeaderboardLayer.Total, g.Key.Season, Area: g.Sum(a => a.Area)))
            .ToList());
        var values = areas.Select(a => (a.UserId, a.Layer, a.Season, Value: Math.Round(a.Area, 1))).ToList();

        // Придержанные — местами своего последнего среза (сегодняшнего или раньше). Нет его (первый туман, история только что
        // очищена) — в рейтинг игрок попадёт, когда туман станет публичным.
        var fresh = tiles.Where(t => t.Fresh).Select(t => t.UserId).Distinct().ToList();
        var previous = await db.LeaderboardSnapshots.AsNoTracking()
            .Where(s => s.Board == LeaderboardBoard.Exploration && fresh.Contains(s.UserId))
            .Select(s => new { s.Day, s.UserId, s.Layer, s.Season, s.Value })
            .ToListAsync(cancellationToken);
        values.AddRange(previous
            .GroupBy(s => s.UserId)
            .SelectMany(g => g.Where(s => s.Day == g.Max(x => x.Day)))
            .Select(s => (s.UserId, s.Layer, s.Season, s.Value)));

        var rows = new List<LeaderboardSnapshotEntity>();
        foreach (var board in values.GroupBy(v => (v.Layer, v.Season)))
        {
            // Одинаковая площадь — одинаковое место: 1, 2, 2, 4.
            var ordered = board.Select(v => v.Value).OrderDescending().ToList();
            rows.AddRange(board.Select(v => new LeaderboardSnapshotEntity
            {
                Day = day,
                Board = LeaderboardBoard.Exploration,
                Layer = board.Key.Layer,
                Season = board.Key.Season,
                UserId = v.UserId,
                Value = v.Value,
                Rank = 1 + ordered.Count(o => o > v.Value),
            }));
        }

        // Замена: строки сегодняшних суток — заново, прошлые сутки остаются своим последним проходом.
        await db.LeaderboardSnapshots
            .Where(s => s.Board == LeaderboardBoard.Exploration && s.Day == day)
            .ExecuteDeleteAsync(cancellationToken);
        db.LeaderboardSnapshots.AddRange(rows);
        await db.SaveChangesAsync(cancellationToken);
        // Только свои срезы: у рейтинга территории своя чистка, а его итог сезона хранится дольше недели (TerritorySnapshots).
        await db.LeaderboardSnapshots
            .Where(s => s.Board == LeaderboardBoard.Exploration && s.Day < day.AddDays(-KeepDays))
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogDebug("Срез «Кто открыл больше» за {Day}: {Rows} строк, придержано игроков {Held}", day, rows.Count, fresh.Count);
        return rows.Count;
    }

    /// <summary>
    /// Убирает игрока из срезов «Исследования» — при очистке истории исследований (<see cref="Fog.FogHistory"/>): стёртое
    /// не должно считаться до следующего среза. Места остальных в тех же срезах пересчитываются, без дыры «1, 3, 4».
    /// Зовётся внутри транзакции вызывающего, под блокировками срезов. Возвращает число стёртых строк.
    /// </summary>
    /// <remarks>
    /// Места считает сама база, одним запросом по затронутым срезам: строки всех игроков в память не читаются, а общие
    /// блокировки срезов, которых ждут срез и чужие очистки, держатся недолго.
    /// </remarks>
    public async Task<int> RemovePlayerAsync(Guid userId, CancellationToken cancellationToken)
    {
        var mine = db.LeaderboardSnapshots.Where(s => s.UserId == userId && s.Board == LeaderboardBoard.Exploration);
        var boards = await mine.Select(s => new { s.Day, s.Layer, s.Season }).ToListAsync(cancellationToken);
        if (boards.Count == 0)
        {
            return 0;
        }

        await mine.ExecuteDeleteAsync(cancellationToken);
        var board = (short)LeaderboardBoard.Exploration;
        var days = boards.Select(b => b.Day).ToArray();
        var layers = boards.Select(b => (short)b.Layer).ToArray();
        var seasons = boards.Select(b => b.Season).ToArray();
        // rank() — то же правило, что у среза: одинаковое значение — одинаковое место (1, 2, 2, 4).
        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE app.leaderboard_snapshots s SET rank = r.place
            FROM (
                SELECT day, layer, season, user_id,
                       rank() OVER (PARTITION BY day, layer, season ORDER BY value DESC)::int AS place
                FROM app.leaderboard_snapshots
                WHERE board = {board} AND (day, layer, season) IN (SELECT * FROM unnest({days}, {layers}, {seasons}))
            ) r
            WHERE s.board = {board} AND s.day = r.day AND s.layer = r.layer AND s.season = r.season
                AND s.user_id = r.user_id AND s.rank <> r.place
            """,
            cancellationToken);
        return boards.Count;
    }
}
