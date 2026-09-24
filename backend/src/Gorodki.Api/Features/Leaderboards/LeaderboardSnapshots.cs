using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Fog;
using Gorodki.Domain.Time;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Leaderboards;

/// <summary>
/// Ежедневный срез рейтингов (PLAN.md, §3.5: «рейтинги — по ежедневному снимку»; §3.10: «кто открыл больше» — сезон
/// и всё время, «Пешком / Вело / Всего», только числа). Срез делается раз в игровые сутки по Минску — при первом часовом
/// проходе фонового обработчика после полуночи.
/// </summary>
/// <remarks>
/// Живой рейтинг выдавал бы «её гектары только что выросли — она только что бегала»; срез раз в сутки этого не говорит
/// (правило приватности — docs/architecture/territory-map.md). Свои гектары игрок видит сразу — в <c>GET /fog/summary</c>.
/// </remarks>
public sealed class LeaderboardSnapshots(AppDbContext db, TimeProvider time, ILogger<LeaderboardSnapshots> logger)
{
    /// <summary>Сколько дней хранить срезы.</summary>
    public const int KeepDays = 7;

    /// <summary>Делает срез за текущие игровые сутки, если его ещё нет. Возвращает число строк среза (0 — уже был).</summary>
    public async Task<int> TakeIfDueAsync(CancellationToken cancellationToken)
    {
        var day = GameClock.GameDayOf(time.GetUtcNow());
        if (await db.LeaderboardSnapshots.AnyAsync(s => s.Day == day && s.Board == LeaderboardBoard.Exploration, cancellationToken))
        {
            return 0;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(4, {day.DayNumber})", cancellationToken);
        if (await db.LeaderboardSnapshots.AnyAsync(s => s.Day == day && s.Board == LeaderboardBoard.Exploration, cancellationToken))
        {
            return 0; // второй экземпляр сервера успел раньше
        }

        var tiles = await db.FogTiles.AsNoTracking()
            .Select(f => new { f.UserId, f.Layer, f.Season, f.TileX, f.TileY, f.CellCount })
            .ToListAsync(cancellationToken);
        var areas = tiles
            .GroupBy(t => (t.UserId, t.Layer, t.Season))
            .Select(g => (g.Key.UserId, Layer: (LeaderboardLayer)g.Key.Layer, g.Key.Season,
                Area: g.Sum(t => t.CellCount * FogTileCodec.CellAreaSquareMeters(new FogTileKey(t.TileX, t.TileY)))))
            .ToList();
        // «Всего» — сумма «Пешком» и «Вело» (§3.10).
        areas.AddRange(areas
            .GroupBy(a => (a.UserId, a.Season))
            .Select(g => (g.Key.UserId, Layer: LeaderboardLayer.Total, g.Key.Season, Area: g.Sum(a => a.Area)))
            .ToList());

        var rows = new List<LeaderboardSnapshotEntity>();
        foreach (var board in areas.GroupBy(a => (a.Layer, a.Season)))
        {
            var boardRows = board.Select(a => new LeaderboardSnapshotEntity
            {
                Day = day,
                Board = LeaderboardBoard.Exploration,
                Layer = board.Key.Layer,
                Season = board.Key.Season,
                UserId = a.UserId,
                Value = Math.Round(a.Area, 1),
            }).ToList();
            AssignRanks(boardRows);
            rows.AddRange(boardRows);
        }

        db.LeaderboardSnapshots.AddRange(rows);
        await db.SaveChangesAsync(cancellationToken);
        await db.LeaderboardSnapshots.Where(s => s.Day < day.AddDays(-KeepDays)).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Срез рейтингов за {Day}: {Rows} строк", day, rows.Count);
        return rows.Count;
    }

    /// <summary>
    /// Убирает игрока из срезов «Исследования» — при очистке истории исследований (<see cref="Fog.FogHistory"/>): стёртое
    /// не должно считаться до следующего среза. Места остальных в тех же срезах пересчитываются, без дыры «1, 3, 4».
    /// Зовётся внутри транзакции вызывающего, под блокировками срезов. Возвращает число стёртых строк.
    /// </summary>
    public async Task<int> RemovePlayerAsync(Guid userId, CancellationToken cancellationToken)
    {
        var mine = db.LeaderboardSnapshots.Where(s => s.UserId == userId && s.Board == LeaderboardBoard.Exploration);
        var boards = (await mine.Select(s => new { s.Day, s.Layer, s.Season }).ToListAsync(cancellationToken))
            .Select(s => (s.Day, s.Layer, s.Season))
            .ToHashSet();
        if (boards.Count == 0)
        {
            return 0;
        }

        await mine.ExecuteDeleteAsync(cancellationToken);
        var days = boards.Select(b => b.Day).Distinct().ToList();
        var rest = await db.LeaderboardSnapshots
            .Where(s => s.Board == LeaderboardBoard.Exploration && days.Contains(s.Day))
            .ToListAsync(cancellationToken);
        foreach (var board in rest.GroupBy(s => (s.Day, s.Layer, s.Season)).Where(g => boards.Contains(g.Key)))
        {
            AssignRanks([.. board]);
        }

        await db.SaveChangesAsync(cancellationToken);
        return boards.Count;
    }

    /// <summary>Места в одном срезе: одинаковое значение — одинаковое место (1, 2, 2, 4).</summary>
    private static void AssignRanks(IReadOnlyCollection<LeaderboardSnapshotEntity> board)
    {
        foreach (var row in board)
        {
            row.Rank = 1 + board.Count(other => other.Value > row.Value);
        }
    }
}
