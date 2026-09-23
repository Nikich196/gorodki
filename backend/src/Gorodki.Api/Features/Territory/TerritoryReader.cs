using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Config;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Territory;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Gorodki.Api.Features.Territory;

/// <summary>Кто смотрит карту.</summary>
/// <param name="UserId">Игрок; свои захваты он видит сразу.</param>
/// <param name="Immediate">Видит всё без задержки: демо-аккаунт на показе и администратор (PLAN.md, §3.16).</param>
public sealed record TerritoryViewer(Guid? UserId, bool Immediate);

/// <summary>
/// Чтение земли по тайлам для карты: версии тайлов, куски, угасание «при чтении» (PLAN.md, §3.3, §7.3) и публичная
/// проекция с задержкой (§3.16): чужой захват виден остальным только через 20 минут — до этого на его месте земля такая,
/// какой была до него (по журналу захватов). Иначе карта показывала бы, где конкретный человек находится прямо сейчас.
/// </summary>
public sealed class TerritoryReader(AppDbContext db, GameConfigStore configs, TimeProvider time, ILogger<TerritoryReader> logger)
{
    /// <summary>Задержка публичной проекции по умолчанию (<c>privacy.publicEventDelayMinutes</c> в игровом конфиге, §3.16).</summary>
    public static readonly TimeSpan PublicDelay = TimeSpan.FromMinutes(Gorodki.Domain.Config.GameConfig.Default.Privacy.PublicEventDelayMinutes);

    public async Task<TerritoryResponse> ReadAsync(
        League league,
        IReadOnlyList<(TileKey Tile, long? KnownVersion)> requested,
        TerritoryViewer viewer,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var config = (await configs.GetCurrentAsync(cancellationToken)).Rules;
        var rules = config.Territory.ToRules();
        var delay = TimeSpan.FromMinutes(config.Privacy.PublicEventDelayMinutes);
        int minX = requested.Min(t => t.Tile.X), maxX = requested.Max(t => t.Tile.X);
        int minY = requested.Min(t => t.Tile.Y), maxY = requested.Max(t => t.Tile.Y);
        var versions = (await db.TileVersions.AsNoTracking()
                .Where(v => v.League == league && v.TileX >= minX && v.TileX <= maxX && v.TileY >= minY && v.TileY <= maxY)
                .ToListAsync(cancellationToken))
            .ToDictionary(v => new TileKey(v.TileX, v.TileY), v => v.Version);

        var changed = new List<(TileKey Tile, long Version)>();
        var unchanged = new List<TileRef>();
        foreach (var (tile, known) in requested)
        {
            var version = versions.GetValueOrDefault(tile);
            if (known == version)
            {
                unchanged.Add(new TileRef(tile.X, tile.Y));
            }
            else
            {
                changed.Add((tile, version));
            }
        }

        if (changed.Count == 0)
        {
            return new TerritoryResponse(league, [], unchanged);
        }

        var parcels = await db.Parcels.AsNoTracking()
            .Where(p => p.League == league && p.TileX >= minX && p.TileX <= maxX && p.TileY >= minY && p.TileY <= maxY)
            .ToListAsync(cancellationToken);
        var hidden = viewer.Immediate ? [] : await HiddenCapturesAsync(league, viewer.UserId, now - delay, minX, maxX, minY, maxY, cancellationToken);

        var tiles = new List<(TileKey Tile, long Version, List<(long Id, ParcelState State, Polygon Geometry)> Pieces, DateTimeOffset? RevealAt)>();
        foreach (var (tile, version) in changed)
        {
            var stored = parcels.Where(p => p.TileX == tile.X && p.TileY == tile.Y).ToList();
            var pending = hidden.Where(h => h.TileX == tile.X && h.TileY == tile.Y).ToList();
            if (pending.Count == 0)
            {
                tiles.Add((tile, version, stored.Select(p => (p.Id, CaptureProcessor.ToParcel(p).State, p.Geometry)).ToList(), null));
                continue;
            }

            // Версия 0: приложение пришлёт её обратно, она не совпадёт с настоящей — и тайл придёт заново, уже открытым.
            var revealAt = pending.Max(h => h.AppliedAt) + delay;
            tiles.Add((tile, 0, await ProjectAsync(tile, stored, pending, cancellationToken), revealAt));
        }

        var owners = tiles.SelectMany(t => t.Pieces.Select(p => p.State.OwnerId)).Distinct().ToList();
        var colors = await db.Users.AsNoTracking()
            .Where(u => owners.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.ColorIndex, cancellationToken);

        var result = tiles
            .Select(t => new TileTerritory(
                t.Tile.X,
                t.Tile.Y,
                t.Version,
                t.Pieces
                    .Where(p => colors.ContainsKey(p.State.OwnerId)) // из журнала мог вернуться кусок уже удалённого аккаунта
                    .OrderBy(p => p.Id)
                    .Select(p => ToView(p.Id, p.State, p.Geometry, colors[p.State.OwnerId], viewer, rules, now))
                    .OfType<ParcelView>()
                    .ToList(),
                t.RevealAt?.ToUnixTimeMilliseconds()))
            .ToList();
        return new TerritoryResponse(league, result, unchanged);
    }

    private sealed record HiddenCapture(Guid CaptureId, int TileX, int TileY, DateTimeOffset AppliedAt, long AppliedSeq);

    /// <summary>Чужие захваты, применённые позже <paramref name="since"/>, в этих тайлах (откаченные — не в счёт: их земли уже нет).</summary>
    private async Task<List<HiddenCapture>> HiddenCapturesAsync(
        League league, Guid? viewerId, DateTimeOffset since, int minX, int maxX, int minY, int maxY, CancellationToken cancellationToken)
    {
        var rows = await db.CaptureJournal.AsNoTracking()
            .Where(j => j.League == league && j.AppliedAt > since
                && j.TileX >= minX && j.TileX <= maxX && j.TileY >= minY && j.TileY <= maxY)
            .Join(
                db.Captures,
                j => j.CaptureId,
                c => c.Id,
                (j, c) => new { j.CaptureId, j.TileX, j.TileY, j.AppliedAt, c.UserId, c.AppliedSeq, c.RolledBackAt })
            .Where(r => r.UserId != viewerId && r.RolledBackAt == null)
            .ToListAsync(cancellationToken);
        return rows.Select(r => new HiddenCapture(r.CaptureId, r.TileX, r.TileY, r.AppliedAt, r.AppliedSeq ?? 0)).ToList();
    }

    /// <summary>
    /// Тайл таким, каким его видят остальные: недавние чужие захваты откатываются в памяти — от новых к старым, только там,
    /// где земля и сейчас такая, какой её оставил захват (свои более поздние изменения зрителя остаются).
    /// </summary>
    private async Task<List<(long Id, ParcelState State, Polygon Geometry)>> ProjectAsync(
        TileKey tile, List<ParcelEntity> stored, List<HiddenCapture> pending, CancellationToken cancellationToken)
    {
        try
        {
            var map = new TerritoryMap();
            map.Load(stored.Select(CaptureProcessor.ToParcel));
            foreach (var capture in pending.OrderByDescending(h => h.AppliedSeq))
            {
                var changes = (await CaptureJournal.LoadAsync(db, capture.CaptureId, cancellationToken)).Where(c => c.Tile == tile).ToList();
                map.Restore(changes);
            }

            // Неизменные куски сохраняют свои номера; пересобранные получают временные отрицательные.
            var projected = map.ParcelsIn(tile);
            var diff = ParcelDiff.Compute([.. stored.Select(p => (p.Id, CaptureProcessor.ToParcel(p)))], projected);
            var kept = stored.Where(p => diff.Kept.Contains(p.Id)).Select(p => (p.Id, CaptureProcessor.ToParcel(p).State, p.Geometry));
            var added = diff.Added.Select((p, i) => (-(long)(i + 1), p.State, p.Geometry));
            return [.. kept, .. added];
        }
        catch (Exception e) when (e is TerritoryEngineException or TopologyException)
        {
            // Лучше пустой тайл на 20 минут, чем показать, где человек сейчас.
            logger.LogError(e, "Публичная проекция тайла {Tile} не собралась — тайл отдан пустым до раскрытия", tile);
            return [];
        }
    }

    /// <summary>Кусок для карты с учётом угасания; null — земля потеряна и «призрак» уже исчез.</summary>
    private static ParcelView? ToView(
        long id, ParcelState state, Polygon geometry, short colorIndex, TerritoryViewer viewer, TerritoryRules rules, DateTimeOffset now)
    {
        var level = Decay.EffectiveLevel(state, now, rules);
        var ghost = level == 0 && Decay.IsGhost(state, now, rules);
        if (level == 0 && !ghost)
        {
            return null;
        }

        // Точное время чужого визита — это «был здесь в 18:42»; остальным хватает часа (угасание считается днями).
        var lastVisit = state.LastVisitAt.ToUnixTimeMilliseconds();
        if (!viewer.Immediate && state.OwnerId != viewer.UserId)
        {
            lastVisit -= lastVisit % 3_600_000;
        }

        return new ParcelView(
            id,
            state.OwnerId,
            colorIndex,
            (short)level,
            ghost,
            lastVisit,
            state.ShieldUntil?.ToUnixTimeMilliseconds(),
            state.SiegeUntil?.ToUnixTimeMilliseconds(),
            LatLon(geometry.ExteriorRing),
            [.. geometry.InteriorRings.Select(LatLon)]);
    }

    /// <summary>Кольцо из UTM 34N в широту и долготу; 7 знаков после запятой — около 1 см.</summary>
    private static IReadOnlyList<double> LatLon(LineString ring)
    {
        var result = new double[ring.NumPoints * 2];
        for (var i = 0; i < ring.NumPoints; i++)
        {
            var point = ring.GetCoordinateN(i);
            var (latitude, longitude) = Utm34.Inverse(point.X, point.Y);
            result[2 * i] = Math.Round(latitude, 7);
            result[(2 * i) + 1] = Math.Round(longitude, 7);
        }

        return result;
    }
}
