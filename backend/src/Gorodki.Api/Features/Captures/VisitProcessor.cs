using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Fog;
using Gorodki.Api.Features.Realtime;
using Gorodki.Api.Features.Runs;
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
/// (другой номер), пропускается. Путь внутри приватных зон игрока (§3.16) не считается.
/// </remarks>
public sealed class VisitProcessor(
    AppDbContext db, RunJudgements judgements, GameConfigStore configs, RealtimeHints hints, TimeProvider time)
{
    /// <summary>
    /// Забеги, готовые к подсчёту визитов, — сначала закончившиеся раньше. Не раньше чем через 20 минут после конца
    /// забега (публичная задержка, §3.16): визит меняет землю и версию тайла, и сразу он выдал бы «игрок только что пробежал
    /// здесь». Забеги демо-аккаунта — сразу, как его захваты.
    /// </summary>
    public async Task<List<Guid>> RunsReadyAsync(int take, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var closedBefore = now - FogProcessor.ClosedRunGrace;
        var publicBefore = now - await DelayAsync(cancellationToken);
        return await db.Runs.AsNoTracking()
            .Where(r => r.VisitsProcessedAt == null
                && r.PointsPurgedAt == null
                && r.PrefixEndSeq >= 0
                && ((r.Status == RunStatus.Finished && r.LastSeq != null && r.PrefixEndSeq >= r.LastSeq)
                    || (r.Status != RunStatus.Active && r.EndedAt < closedBefore))
                && (r.EndedAt <= publicBefore || db.Users.Any(u => u.Id == r.UserId && u.Role == UserRole.Demo)))
            .OrderBy(r => r.EndedAt)
            .Select(r => r.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    private async Task<TimeSpan> DelayAsync(CancellationToken cancellationToken) =>
        TimeSpan.FromMinutes((await configs.GetCurrentAsync(cancellationToken)).Rules.Privacy.PublicEventDelayMinutes);

    /// <summary>
    /// Засчитывает визиты забега. Возвращает, сколько кусков освежено, или null — забег уже обработан или ещё рано
    /// (не прошла публичная задержка после его конца).
    /// </summary>
    public async Task<int?> ProcessRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null || run.VisitsProcessedAt is not null || run.PrefixEndSeq < 0 || run.PointsPurgedAt is not null)
        {
            return null;
        }

        var demo = await db.Users.AnyAsync(u => u.Id == run.UserId && u.Role == UserRole.Demo, cancellationToken);
        if (!demo && (run.EndedAt is not { } endedAt || endedAt > time.GetUtcNow() - await DelayAsync(cancellationToken)))
        {
            return null;
        }

        var (_, judgement) = await judgements.JudgeAsync(run, cancellationToken);
        var current = (await configs.GetCurrentAsync(cancellationToken)).Rules; // карта общая — правила земли на момент визита
        var rules = current.Territory.ToRules();
        var segments = JudgedPath.Segments(judgement.Points, judgement.Verdicts).ToList();
        var acceptedMeters = Math.Round(JudgedPath.Length(segments), 1); // пробег — весь путь, без обрезки
        var path = Visits.TrimmedPath(segments, current.Privacy.TrimMeters);

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
        var lockSpace = 100 + (int)run.League;
        foreach (var tile in candidates.Select(c => c.Tile).Distinct().Order())
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({lockSpace}, {tile.LockKey})", cancellationToken);
        }

        var ids = candidates.Select(c => c.Id).ToList();
        var fresh = await db.Parcels.Where(p => ids.Contains(p.Id) && p.OwnerId == run.UserId).ToListAsync(cancellationToken);
        var changedTiles = new HashSet<TileKey>();
        var visitedCount = 0;
        foreach (var parcel in fresh)
        {
            var at = candidates.Single(c => c.Id == parcel.Id).At;
            var state = CaptureProcessor.ToParcel(parcel).State;
            if (CaptureRules.Visit(state, at, rules) is not { } visited || visited == state)
            {
                continue; // земля уже угасла (вернуть можно только захватом) или визит ничего не меняет
            }

            parcel.Level = (short)visited.Level;
            parcel.LastVisitAt = visited.LastVisitAt;
            parcel.LastLevelUpAt = visited.LastLevelUpAt;
            changedTiles.Add(new TileKey(parcel.TileX, parcel.TileY));
            visitedCount++;
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
