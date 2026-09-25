using System.Collections.Concurrent;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Osm;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Gorodki.Api.Features.Osm;

/// <summary>
/// Маски набора OSM по тайлам UTM — для шага A захвата (docs/architecture/osm-pipeline.md, «Маски в обработке захвата»).
/// </summary>
public interface IMaskStore
{
    /// <summary>
    /// Маски набора <paramref name="setVersion"/> во всех тайлах, которые задевает рамка (<see cref="TileKey.Covering"/>), —
    /// объединением через <see cref="GeoOps.UnionAll"/>; <c>null</c>, если масок там нет. Если захват ограничен зоной игры
    /// (вопрос 6.4), тайл вне рамки конвейера входит целиком как «вне игрового поля». Каждый вызов даёт новые объекты:
    /// геометрии NTS изменяемы.
    /// </summary>
    /// <exception cref="InvalidOperationException">Набора с таким номером в базе нет — конфиг ссылается на незагруженный набор.</exception>
    Task<Geometry?> CoveringAsync(Envelope envelope, int setVersion, CancellationToken cancellationToken);
}

/// <summary>
/// Кэш масок: набор после загрузки не меняется, поэтому (набор, тайл) хранится сколько угодно. Хранятся байты TWKB, а не
/// объекты NTS: общий изменяемый объект между потоками — лишний риск (<see cref="GeoOps.EmptyPolygon"/>).
/// </summary>
public sealed class MaskCache
{
    internal ConcurrentDictionary<int, OsmSetFrame> Frames { get; } = new();

    internal ConcurrentDictionary<(int Set, TileKey Tile), byte[][]> Tiles { get; } = new();
}

/// <summary>Рамка набора (тайлы UTM) и зона игры: за рамкой строк масок нет.</summary>
public sealed record OsmSetFrame(int MinX, int MinY, int MaxX, int MaxY, PlayZone PlayZone)
{
    public bool Contains(TileKey tile) => tile.X >= MinX && tile.X <= MaxX && tile.Y >= MinY && tile.Y <= MaxY;
}

public sealed class MaskStore(AppDbContext db, MaskCache cache) : IMaskStore
{
    public async Task<Geometry?> CoveringAsync(Envelope envelope, int setVersion, CancellationToken cancellationToken)
    {
        var frame = await FrameAsync(setVersion, cancellationToken);
        var tiles = TileKey.Covering(envelope);
        var missing = tiles.Where(t => frame.Contains(t) && !cache.Tiles.ContainsKey((setVersion, t))).ToList();
        if (missing.Count > 0)
        {
            var xs = missing.Select(t => t.X).ToArray();
            var ys = missing.Select(t => t.Y).ToArray();
            var rows = await db.Masks
                .FromSql($"SELECT * FROM app.masks WHERE set_version = {setVersion} AND (tile_x, tile_y) IN (SELECT * FROM unnest({xs}, {ys}))")
                .AsNoTracking()
                .Select(m => new { m.TileX, m.TileY, m.Geometry })
                .ToListAsync(cancellationToken);
            foreach (var tile in missing)
            {
                cache.Tiles[(setVersion, tile)] = rows
                    .Where(r => r.TileX == tile.X && r.TileY == tile.Y)
                    .Select(r => Twkb.Write(r.Geometry))
                    .ToArray();
            }
        }

        var parts = new List<Geometry>();
        foreach (var tile in tiles)
        {
            if (!frame.Contains(tile))
            {
                if (frame.PlayZone != PlayZone.Anywhere)
                {
                    parts.Add(tile.ToPolygon()); // вне рамки конвейера — целиком «вне игрового поля»
                }

                continue;
            }

            parts.AddRange(cache.Tiles[(setVersion, tile)].Select(Twkb.Read));
        }

        return parts.Count == 0 ? null : GeoOps.UnionAll(parts);
    }

    private async Task<OsmSetFrame> FrameAsync(int setVersion, CancellationToken cancellationToken)
    {
        if (cache.Frames.TryGetValue(setVersion, out var cached))
        {
            return cached;
        }

        var frame = await db.OsmSets.AsNoTracking()
            .Where(s => s.Version == setVersion)
            .Select(s => new OsmSetFrame(s.FrameMinX, s.FrameMinY, s.FrameMaxX, s.FrameMaxY, s.PlayZone))
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Набора масок {setVersion} нет в базе: игровой конфиг ссылается на незагруженный набор.");
        return cache.Frames.GetOrAdd(setVersion, frame);
    }
}
