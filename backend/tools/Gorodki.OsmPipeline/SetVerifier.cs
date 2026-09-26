using Gorodki.Domain.Fog;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Osm;
using NetTopologySuite.Algorithm.Locate;
using NetTopologySuite.Geometries;

namespace Gorodki.OsmPipeline;

/// <summary>
/// Инварианты набора (osm-pipeline.md, «Проверка»): хоть одно нарушение — набор не выпускается.
/// </summary>
public static class SetVerifier
{
    public static IReadOnlyList<string> Verify(OsmSetData data)
    {
        var problems = new List<string>();
        foreach (var piece in data.Masks)
        {
            CheckPiece($"маска {OsmCodes.Of(piece.Kind)} в тайле {piece.Tile}", piece.Tile, piece.Geometry, problems);
        }

        foreach (var piece in data.Land)
        {
            CheckPiece($"земля {piece.Kind} в тайле {piece.Tile}", piece.Tile, piece.Geometry, problems);
        }

        CheckReachableOutsideMasks(data, problems);
        CheckDistricts(data, problems);
        return problems;
    }

    /// <summary>Каждый кусок — правильный простой многоугольник на сетке внутри своего тайла, TWKB читается без потерь.</summary>
    private static void CheckPiece(string what, TileKey tile, Polygon polygon, List<string> problems)
    {
        if (polygon.IsEmpty || polygon.Area <= 0)
        {
            problems.Add($"{what}: пустой кусок.");
            return;
        }

        if (!polygon.IsValid)
        {
            problems.Add($"{what}: неправильная геометрия.");
        }

        if (!GeoOps.IsOnGrid(polygon))
        {
            problems.Add($"{what}: вершины не на сетке 0,1 м.");
        }

        if (!tile.ToPolygon().Covers(polygon))
        {
            problems.Add($"{what}: кусок выходит за свой тайл.");
        }

        if (!Twkb.Read(Twkb.Write(polygon)).EqualsExact(polygon))
        {
            problems.Add($"{what}: TWKB не читается обратно без потерь.");
        }
    }

    /// <summary>
    /// Нет достижимой клетки, центр которой внутри маски, вычитаемой из «достижимого». Допуск — сетка: snap-rounding
    /// сдвигает край не дальше полудиагонали клетки сетки (0,0707 м, ADR 0003), поэтому в счёт идут центры глубже 0,1 м.
    /// </summary>
    /// <remarks>
    /// Маски тайла — объединением, а не коллекцией кусков: маски разных видов накладываются (ж/д над водой на мосту,
    /// ж/д и «магистраль» на переезде), а <see cref="IndexedPointInAreaLocator"/> считает пересечения луча с кольцами —
    /// точка в двух наложенных многоугольниках вышла бы «снаружи», и в зонах наложения проверка была бы слепа.
    /// </remarks>
    private static void CheckReachableOutsideMasks(OsmSetData data, List<string> problems)
    {
        var masksByTile = data.Masks
            .Where(m => SetBuilder.SubtractedFromReachable.Contains(m.Kind))
            .GroupBy(m => m.Tile)
            .ToDictionary(g => g.Key, g => GeoOps.UnionAll(g.Select(m => (Geometry)m.Geometry)));
        var locators = masksByTile.ToDictionary(kv => kv.Key, kv => new IndexedPointInAreaLocator(kv.Value));
        var bad = 0;
        foreach (var (key, bits) in data.Reachable)
        {
            for (var bit = 0; bit < 65_536; bit++)
            {
                if (!bits.IsSet(bit))
                {
                    continue;
                }

                var (latitude, longitude) = FogGrid.Center(new FogCell((key.X << 8) + (bit & 255), (key.Y << 8) + (bit >> 8)));
                var (easting, northing) = Utm34.Forward(latitude, longitude);
                var tile = TileKey.Of(easting, northing);
                var center = new Coordinate(easting, northing);
                if (locators.TryGetValue(tile, out var locator)
                    && locator.Locate(center) == Location.Interior
                    && masksByTile[tile].Boundary.Distance(GeoOps.Factory.CreatePoint(center)) > 0.1)
                {
                    bad++;
                }
            }
        }

        if (bad > 0)
        {
            problems.Add($"«Достижимое»: {bad} клеток с центром внутри маски.");
        }
    }

    private static void CheckDistricts(OsmSetData data, List<string> problems)
    {
        var city = data.Districts.SingleOrDefault(d => d.Kind == DistrictKind.City);
        if (city is null)
        {
            problems.Add("Нет района «город».");
            return;
        }

        if (city.Tiles.Count != data.Reachable.Count
            || city.Tiles.Any(t => !data.Reachable.TryGetValue(t.Key, out var bits) || !bits.ToBytes().AsSpan().SequenceEqual(t.Value.ToBytes())))
        {
            problems.Add("Клетки района «город» не совпадают с «достижимым».");
        }

        foreach (var district in data.Districts)
        {
            foreach (var (key, bits) in district.Tiles)
            {
                if (!data.Reachable.TryGetValue(key, out var reachable) || bits.CountAnd(reachable) != bits.Count)
                {
                    problems.Add($"Район {district.Key}: клетки вне «достижимого» в тайле {key.X}:{key.Y}.");
                }
            }

            if (!district.Geometry.IsValid || !GeoOps.IsOnGrid(district.Geometry))
            {
                problems.Add($"Район {district.Key}: неправильная геометрия или вершины не на сетке.");
            }
        }

        var quarters = data.Districts.Where(d => d.Kind == DistrictKind.Quarter).ToList();
        var arena = data.Districts.SingleOrDefault(d => d.Kind == DistrictKind.Arena);
        if (arena is null)
        {
            return;
        }

        if (quarters.Count is < ArenaBuilder.MinQuarters or > ArenaBuilder.MaxQuarters)
        {
            problems.Add($"Кварталов Арены {quarters.Count}, а по плану 6–8 (§3.6).");
        }

        foreach (var quarter in quarters.Where(q => q.Geometry.IsEmpty))
        {
            problems.Add($"Квартал «{quarter.Name}» пуст.");
        }

        for (var i = 0; i < quarters.Count; i++)
        {
            for (var j = i + 1; j < quarters.Count; j++)
            {
                if (GeoOps.Intersection(quarters[i].Geometry, quarters[j].Geometry).Area > 1)
                {
                    problems.Add($"Кварталы «{quarters[i].Name}» и «{quarters[j].Name}» накладываются.");
                }
            }
        }
    }
}
