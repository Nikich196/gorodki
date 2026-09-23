using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Config;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Territory;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Gorodki.Api.Features.Territory;

/// <summary>Чтение земли по тайлам для карты: версии тайлов, куски, угасание «при чтении» (PLAN.md, §3.3, §7.3).</summary>
public sealed class TerritoryReader(AppDbContext db, GameConfigStore configs, TimeProvider time)
{
    public async Task<TerritoryResponse> ReadAsync(
        League league, IReadOnlyList<(TileKey Tile, long? KnownVersion)> requested, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var rules = (await configs.GetCurrentAsync(cancellationToken)).Rules.Territory.ToRules();
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

        var parcels = changed.Count == 0
            ? []
            : await db.Parcels.AsNoTracking()
                .Where(p => p.League == league && p.TileX >= minX && p.TileX <= maxX && p.TileY >= minY && p.TileY <= maxY)
                .Join(db.Users, p => p.OwnerId, u => u.Id, (p, u) => new { Parcel = p, u.ColorIndex })
                .ToListAsync(cancellationToken);

        var result = changed
            .Select(c => new TileTerritory(
                c.Tile.X,
                c.Tile.Y,
                c.Version,
                parcels
                    .Where(p => p.Parcel.TileX == c.Tile.X && p.Parcel.TileY == c.Tile.Y)
                    .OrderBy(p => p.Parcel.Id)
                    .Select(p => ToView(p.Parcel, p.ColorIndex, rules, now))
                    .OfType<ParcelView>()
                    .ToList()))
            .ToList();
        return new TerritoryResponse(league, result, unchanged);
    }

    /// <summary>Кусок для карты с учётом угасания; null — земля потеряна и «призрак» уже исчез.</summary>
    private static ParcelView? ToView(ParcelEntity parcel, short colorIndex, TerritoryRules rules, DateTimeOffset now)
    {
        var state = new ParcelState
        {
            OwnerId = parcel.OwnerId,
            Level = parcel.Level,
            LastVisitAt = parcel.LastVisitAt,
            LastLevelUpAt = parcel.LastLevelUpAt,
        };
        var level = Decay.EffectiveLevel(state, now, rules);
        var ghost = level == 0 && Decay.IsGhost(state, now, rules);
        return level == 0 && !ghost ? null : new ParcelView(
        parcel.Id,
        parcel.OwnerId,
        colorIndex,
        (short)level,
        ghost,
        parcel.LastVisitAt.ToUnixTimeMilliseconds(),
        parcel.ShieldUntil?.ToUnixTimeMilliseconds(),
        parcel.SiegeUntil?.ToUnixTimeMilliseconds(),
        LatLon(parcel.Geometry.ExteriorRing),
        [.. parcel.Geometry.InteriorRings.Select(LatLon)]);
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
