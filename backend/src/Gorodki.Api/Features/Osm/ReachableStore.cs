using System.Collections.Concurrent;
using Gorodki.Api.Features.Config;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Fog;
using Gorodki.Domain.Osm;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Osm;

/// <summary>Район набора и его «достижимые» клетки: знаменатель «%» района.</summary>
public sealed record DistrictReach(
    long Id, DistrictKind Kind, string Key, string Name, bool Proposal, double AreaWithoutMasks, ReachableArea Reachable);

/// <summary>«Достижимое» набора: весь город и районы (город, Ленинский и Московский, Арена, кварталы).</summary>
public sealed record OsmReach(int SetVersion, ReachableArea City, IReadOnlyList<DistrictReach> Districts)
{
    /// <summary>
    /// Доли открытого в слое игрока: «% Бреста» (район вида «город») и по каждому району —
    /// <c>popcount(explored &amp; reachable) / popcount(reachable)</c> (PLAN.md, §7.3).
    /// </summary>
    public OsmShares SharesOf(IReadOnlyDictionary<FogTileKey, FogTileBits> explored) => new(
        SetVersion,
        City.ShareOf(explored),
        [.. Districts.Where(d => d.Kind != DistrictKind.City).Select(d => new DistrictShare(d.Key, d.Kind, d.Name, d.Proposal, d.Reachable.ShareOf(explored)))]);
}

/// <summary>Доля района.</summary>
public sealed record DistrictShare(string Key, DistrictKind Kind, string Name, bool Proposal, ExploredShare Share);

/// <summary>«% Бреста» и районов по набору <paramref name="SetVersion"/> (версию стоит отдавать рядом с процентом).</summary>
public sealed record OsmShares(int SetVersion, ExploredShare City, IReadOnlyList<DistrictShare> Districts);

/// <summary>Кэш «достижимого»: набор после загрузки не меняется. Весь город — ~100 тайлов по 8 КБ.</summary>
public sealed class ReachableCache
{
    internal ConcurrentDictionary<int, OsmReach> Sets { get; } = new();
}

/// <summary>
/// Чтение «достижимого» действующего набора (osm-pipeline.md, «Как считается % Бреста»). Опора для «% Бреста и районов» в
/// <c>GET /fog/summary</c> (задача E9, docs/guides/egor-server.md): процент считается при чтении, пословно (AND и popcount).
/// </summary>
public sealed class ReachableStore(AppDbContext db, GameConfigStore configs, ReachableCache cache)
{
    /// <summary>Набор, действующий сейчас (из текущей версии игрового конфига); <c>null</c> — набора нет, процента нет.</summary>
    public async Task<int?> CurrentSetVersionAsync(CancellationToken cancellationToken) =>
        (await configs.GetCurrentAsync(cancellationToken)).Rules.Osm.SetVersion;

    /// <summary>«Достижимое» набора: город и районы. <c>null</c>, если набора с таким номером в базе нет.</summary>
    public async Task<OsmReach?> LoadAsync(int setVersion, CancellationToken cancellationToken)
    {
        if (cache.Sets.TryGetValue(setVersion, out var cached))
        {
            return cached;
        }

        if (!await db.OsmSets.AnyAsync(s => s.Version == setVersion, cancellationToken))
        {
            return null;
        }

        var city = await db.ReachableTiles.AsNoTracking()
            .Where(t => t.SetVersion == setVersion)
            .Select(t => new { t.TileX, t.TileY, t.Bits })
            .ToListAsync(cancellationToken);
        var districts = await db.Districts.AsNoTracking()
            .Where(d => d.SetVersion == setVersion)
            .OrderBy(d => d.Kind).ThenBy(d => d.Key)
            .Select(d => new { d.Id, d.Kind, d.Key, d.Name, d.Proposal, d.AreaWithoutMasks })
            .ToListAsync(cancellationToken);
        var ids = districts.Select(d => d.Id).ToList();
        var tiles = await db.DistrictTiles.AsNoTracking()
            .Where(t => ids.Contains(t.DistrictId))
            .Select(t => new { t.DistrictId, t.TileX, t.TileY, t.Bits })
            .ToListAsync(cancellationToken);

        var reach = new OsmReach(
            setVersion,
            new ReachableArea(city.Select(t => KeyValuePair.Create(new FogTileKey(t.TileX, t.TileY), FogTileCodec.Decompress(t.Bits)))),
            [.. districts.Select(d => new DistrictReach(
                d.Id,
                d.Kind,
                d.Key,
                d.Name,
                d.Proposal,
                d.AreaWithoutMasks,
                new ReachableArea(tiles.Where(t => t.DistrictId == d.Id).Select(t => KeyValuePair.Create(new FogTileKey(t.TileX, t.TileY), FogTileCodec.Decompress(t.Bits))))))]);
        return cache.Sets.GetOrAdd(setVersion, reach);
    }
}
