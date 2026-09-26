using System.Collections.Concurrent;
using Gorodki.Domain.Fog;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Osm;
using NetTopologySuite.Algorithm.Locate;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Simplify;

namespace Gorodki.OsmPipeline;

/// <summary>Административный район из OSM: id отношения, название и контур (UTM, сетка).</summary>
public sealed record DistrictInput(long OsmId, string Name, Geometry Geometry);

/// <summary>Всё, что нужно сборке, — уже в UTM 34N на сетке 0,1 м.</summary>
public sealed class OsmInput
{
    /// <summary>Объекты всех тем вперемешку (линии, площади, точки); повторы одного объекта убраны.</summary>
    public required IReadOnlyList<OsmFeature> Features { get; init; }

    public required Geometry City { get; init; }

    public string CityName { get; init; } = "Брест";

    public long? CityOsmId { get; init; }

    /// <summary>Линия госграницы в рамке конвейера (граница многоугольника страны) — для погранполосы.</summary>
    public Geometry? BorderLine { get; init; }

    public IReadOnlyList<DistrictInput> Districts { get; init; } = [];
}

/// <summary>
/// Сборка набора (docs/architecture/osm-pipeline.md, «Поток данных», шаг 4): маски по видам, «достижимое», районы, Арена
/// и кварталы. Каждое наложение — через <see cref="GeoOps"/> на сетке 0,1 м; маски и «достижимое» строятся по тайлам UTM
/// 1×1 км, каждый тайл отдельно, — память не растёт с городом, а результат не зависит от порядка тайлов.
/// </summary>
public static class SetBuilder
{
    /// <summary>Виды масок, которые вычитаются из «достижимого» (вопрос 6.2, Б: все, кроме мемориалов).</summary>
    public static readonly IReadOnlySet<MaskKind> SubtractedFromReachable = new HashSet<MaskKind>
    {
        MaskKind.Water, MaskKind.Rail, MaskKind.MajorRoad, MaskKind.Military, MaskKind.Cemetery, MaskKind.Border,
    };

    public static OsmSetData Build(OsmInput input, PipelineParams parameters, TileRange frame)
    {
        var themes = new Themes(parameters);
        var notes = new List<string>();
        var groups = Classify(input, parameters, themes, notes);

        // Арена — до тайлов: от неё зависит маска «вне поля», если захват ограничен Ареной.
        ArenaBuilder.Result? arena = null;
        if (parameters.Arena is { } recipe)
        {
            arena = ArenaBuilder.Build(recipe, input.Features, input.City);
            notes.AddRange(arena.Notes);
        }

        if (parameters.BorderStripMeters is null)
        {
            notes.Add("Погранполоса выключена: ширины из официальных правил нет (вопрос 1.3).");
        }

        if (parameters.Memorials.Objects.Count == 0)
        {
            notes.Add("Мемориалов нет: список ещё не утверждён Никитой (вопрос 6.6).");
        }

        var playArea = parameters.PlayZone switch
        {
            PlayZone.Anywhere => null,
            PlayZone.City => input.City,
            PlayZone.Arena => arena?.Arena ?? throw new InvalidOperationException("Зона игры — Арена, а рецепта Арены нет."),
            _ => throw new InvalidOperationException($"Неизвестная зона игры {parameters.PlayZone}."),
        };

        // Рамки считаются заранее: NTS кэширует их лениво, а тайлы идут параллельно.
        _ = input.City.EnvelopeInternal;
        _ = playArea?.EnvelopeInternal;
        var borderLine = parameters.BorderStripMeters is not null ? input.BorderLine : null;
        _ = borderLine?.EnvelopeInternal;

        var tiles = new ConcurrentBag<TileResult>();
        Parallel.ForEach(frame.Tiles(), tile => tiles.Add(BuildTile(tile, input.City, playArea, borderLine, groups, parameters)));
        var byTile = tiles.ToDictionary(t => t.Tile);

        var masks = byTile.Values
            .SelectMany(t => t.Masks)
            .OrderBy(m => m.Kind).ThenBy(m => m.Tile).ThenBy(m => m.Geometry.EnvelopeInternal.MinX).ThenBy(m => m.Geometry.EnvelopeInternal.MinY)
            .ThenBy(m => m.Geometry.Area)
            .ToList();
        var land = byTile.Values
            .SelectMany(t => t.Land)
            .OrderBy(m => m.Tile).ThenBy(m => m.Geometry.EnvelopeInternal.MinX).ThenBy(m => m.Geometry.EnvelopeInternal.MinY)
            .ThenBy(m => m.Geometry.Area)
            .ToList();
        var reachableVectors = byTile.Values.Where(t => !t.Reachable.IsEmpty).ToDictionary(t => t.Tile, t => t.Reachable);
        var reachable = ReachableRaster.Rasterize(reachableVectors, input.City.EnvelopeInternal);

        var districts = new List<DistrictData>
        {
            District("city", DistrictKind.City, input.CityName, input.CityOsmId, proposal: false, input.City, input.City, reachable, byTile),
        };
        foreach (var district in input.Districts.OrderBy(d => d.OsmId))
        {
            districts.Add(District(
                $"district:{district.OsmId}", DistrictKind.District, district.Name, district.OsmId, proposal: false, district.Geometry, input.City, reachable, byTile));
        }

        if (arena is not null)
        {
            districts.Add(District("arena", DistrictKind.Arena, "Арена БрГТУ", null, arena.Proposal, arena.Arena, input.City, reachable, byTile));
            for (var i = 0; i < arena.Quarters.Count; i++)
            {
                var quarter = arena.Quarters[i];
                districts.Add(District($"quarter:{i + 1}", DistrictKind.Quarter, quarter.Name, null, arena.Proposal, quarter.Shape, input.City, reachable, byTile));
            }
        }

        return new OsmSetData
        {
            Frame = frame,
            PlayZone = parameters.PlayZone,
            Masks = masks,
            Land = land,
            Reachable = reachable,
            Districts = districts,
            Notes = notes,
        };
    }

    // ── Отбор объектов по темам ──────────────────────────────────────────────

    internal sealed class Groups
    {
        public required STRtree<Geometry> PedestrianLines { get; init; }

        public required STRtree<Geometry> PedestrianAreas { get; init; }

        public required STRtree<Geometry> MajorRoads { get; init; }

        public required STRtree<Geometry> RailLines { get; init; }

        public required IReadOnlyDictionary<MaskKind, STRtree<Geometry>> AreaMasks { get; init; }

        public required STRtree<Geometry> Memorials { get; init; }

        public required STRtree<Geometry> LowValueLand { get; init; }
    }

    private static Groups Classify(OsmInput input, PipelineParams parameters, Themes themes, List<string> notes)
    {
        var pedestrianLines = new List<Geometry>();
        var pedestrianAreas = new List<Geometry>();
        var majorRoads = new List<Geometry>();
        var railLines = new List<Geometry>();
        var areaMasks = new Dictionary<MaskKind, List<Geometry>>();
        var lowValue = new List<Geometry>();
        var memorials = new List<Geometry>();
        var memorialRefs = new HashSet<string>(parameters.Memorials.Objects, StringComparer.Ordinal);
        var foundMemorials = new HashSet<string>(StringComparer.Ordinal);

        foreach (var feature in input.Features)
        {
            if (themes.IsPedestrianLine(feature))
            {
                pedestrianLines.Add(feature.Geometry);
            }

            if (themes.IsPedestrianArea(feature))
            {
                pedestrianAreas.Add(feature.Geometry);
            }

            if (themes.IsMajorRoad(feature))
            {
                majorRoads.Add(feature.Geometry);
            }

            if (themes.IsRailLine(feature))
            {
                railLines.Add(feature.Geometry);
            }

            if (themes.AreaMaskKind(feature) is { } kind)
            {
                if (!areaMasks.TryGetValue(kind, out var list))
                {
                    list = [];
                    areaMasks[kind] = list;
                }

                list.Add(feature.Geometry);
            }

            if (themes.IsLowValueLand(feature))
            {
                lowValue.Add(feature.Geometry);
            }

            if (memorialRefs.Contains(feature.Ref) && (feature.IsArea || feature.Geometry is Point))
            {
                foundMemorials.Add(feature.Ref);
                memorials.Add(feature.IsArea ? feature.Geometry : GeoOps.Buffer(feature.Geometry, parameters.Memorials.PointRadiusMeters, parameters.QuadrantSegments));
            }
        }

        foreach (var missing in memorialRefs.Except(foundMemorials).Order(StringComparer.Ordinal))
        {
            notes.Add($"Мемориал {missing} из списка не найден в выгрузке (площадью или точкой).");
        }

        return new Groups
        {
            PedestrianLines = Index(pedestrianLines),
            PedestrianAreas = Index(pedestrianAreas),
            MajorRoads = Index(majorRoads),
            RailLines = Index(railLines),
            AreaMasks = areaMasks.ToDictionary(kv => kv.Key, kv => Index(kv.Value)),
            Memorials = Index(memorials),
            LowValueLand = Index(lowValue),
        };
    }

    private static STRtree<Geometry> Index(List<Geometry> geometries)
    {
        var tree = new STRtree<Geometry>();
        foreach (var geometry in geometries)
        {
            tree.Insert(geometry.EnvelopeInternal, geometry);
        }

        tree.Build(); // после Build запросы только читают — их можно делать из разных потоков
        return tree;
    }

    // ── Один тайл ────────────────────────────────────────────────────────────

    internal sealed record TileResult(TileKey Tile, List<MaskPiece> Masks, List<LandPiece> Land, Geometry Reachable, Geometry CaptureMasks);

    private static TileResult BuildTile(
        TileKey tile, Geometry city, Geometry? playArea, Geometry? borderLine, Groups groups, PipelineParams parameters)
    {
        var square = tile.ToPolygon();
        var cityInTile = city.EnvelopeInternal.Intersects(square.EnvelopeInternal) ? GeoOps.Intersection(city, square) : GeoOps.EmptyPolygon();
        var byKind = new SortedDictionary<MaskKind, Geometry>();
        var land = new List<LandPiece>();
        Geometry reachable = GeoOps.EmptyPolygon();

        if (!cityInTile.IsEmpty)
        {
            foreach (var (kind, tree) in groups.AreaMasks)
            {
                byKind[kind] = AreasIn(tree, square);
            }

            var railBuffer = LinesBuffered(groups.RailLines, square, parameters.Rail.HalfWidthMeters, parameters.QuadrantSegments);
            byKind[MaskKind.Rail] = GeoOps.Union(byKind.GetValueOrDefault(MaskKind.Rail) ?? GeoOps.EmptyPolygon(), railBuffer);
            byKind[MaskKind.MajorRoad] = LinesBuffered(groups.MajorRoads, square, parameters.MajorRoads.HalfWidthMeters, parameters.QuadrantSegments);
            if (borderLine is not null && parameters.BorderStripMeters is { } strip)
            {
                byKind[MaskKind.Border] = LinesBuffered([borderLine], square, strip, parameters.QuadrantSegments);
            }

            byKind[MaskKind.Memorial] = AreasIn(groups.Memorials, square);

            // Все маски — в границах города (§3.11: «обрезка по границе Бреста»).
            foreach (var kind in byKind.Keys.ToList())
            {
                byKind[kind] = byKind[kind].IsEmpty ? byKind[kind] : GeoOps.Intersection(byKind[kind], cityInTile);
            }

            // «Достижимое»: полоса вокруг пешеходных путей и площадей ∩ город − маски (кроме мемориалов).
            var buffer = parameters.Pedestrian.BufferMeters;
            var paths = GeoOps.Union(
                LinesBuffered(groups.PedestrianLines, square, buffer, parameters.QuadrantSegments),
                AreasBuffered(groups.PedestrianAreas, square, buffer, parameters.QuadrantSegments));
            reachable = paths.IsEmpty ? paths : GeoOps.Intersection(paths, cityInTile);
            var subtracted = GeoOps.UnionAll(byKind.Where(kv => SubtractedFromReachable.Contains(kv.Key) && !kv.Value.IsEmpty).Select(kv => kv.Value));
            if (!reachable.IsEmpty && !subtracted.IsEmpty)
            {
                reachable = GeoOps.Difference(reachable, subtracted);
            }

            var lowValue = AreasIn(groups.LowValueLand, square);
            if (!lowValue.IsEmpty)
            {
                land.AddRange(GeoOps.Polygons(GeoOps.Intersection(lowValue, cityInTile)).Select(p => new LandPiece(LandKind.LowValue, tile, Normalized(p))));
            }
        }

        if (playArea is not null)
        {
            // «Вне игрового поля» — рамка минус зона игры; из «достижимого» не вычитается (% Бреста — по всему городу).
            byKind[MaskKind.Outside] = playArea.EnvelopeInternal.Intersects(square.EnvelopeInternal)
                ? GeoOps.Difference(square, playArea)
                : square;
        }

        var masks = new List<MaskPiece>();
        foreach (var (kind, geometry) in byKind)
        {
            var simplified = parameters.MaskSimplifyMeters > 0 && !geometry.IsEmpty
                ? GeoOps.Polygonal(GeoOps.Factory.CreateGeometry(GeoOps.Snap(TopologyPreservingSimplifier.Simplify(geometry, parameters.MaskSimplifyMeters))))
                : geometry;
            masks.AddRange(GeoOps.Polygons(simplified).Select(p => new MaskPiece(kind, tile, Normalized(p))));
        }

        var captureMasks = GeoOps.UnionAll(byKind.Where(kv => kv.Key != MaskKind.Outside && !kv.Value.IsEmpty).Select(kv => kv.Value));
        return new TileResult(tile, masks, land, reachable, captureMasks);
    }

    private static Polygon Normalized(Polygon polygon) => (Polygon)polygon.Normalized();

    /// <summary>Площади, задевающие тайл, — обрезанные тайлом и объединённые.</summary>
    private static Geometry AreasIn(STRtree<Geometry> tree, Polygon square)
    {
        var pieces = tree.Query(square.EnvelopeInternal)
            .Select(area => GeoOps.Intersection(area, square))
            .Where(piece => !piece.IsEmpty)
            .ToList();
        return pieces.Count == 0 ? GeoOps.EmptyPolygon() : GeoOps.UnionAll(pieces);
    }

    /// <summary>
    /// Полоса ширины 2·<paramref name="halfWidth"/> вокруг линий, обрезанная тайлом. Линии берутся только в рамке тайла с
    /// запасом halfWidth + 1 м: ближайшая к точке тайла точка линии лежит в этой рамке, поэтому полоса внутри тайла та же,
    /// что у целых линий.
    /// </summary>
    private static Geometry LinesBuffered(STRtree<Geometry> tree, Polygon square, double halfWidth, int quadrantSegments) =>
        LinesBuffered(tree.Query(Expanded(square, halfWidth + 1)), square, halfWidth, quadrantSegments);

    private static Geometry LinesBuffered(IEnumerable<Geometry> lines, Polygon square, double halfWidth, int quadrantSegments)
    {
        var window = GeoOps.Factory.ToGeometry(Expanded(square, halfWidth + 1));
        var clipped = lines
            .Where(line => line.EnvelopeInternal.Intersects(window.EnvelopeInternal))
            .Select(line => GeoOps.LineInside(line, window))
            .Where(line => !line.IsEmpty)
            .ToList();
        if (clipped.Count == 0)
        {
            return GeoOps.EmptyPolygon();
        }

        var buffered = GeoOps.Buffer(GeoOps.Factory.BuildGeometry(clipped), halfWidth, quadrantSegments);
        return buffered.IsEmpty ? buffered : GeoOps.Intersection(buffered, square);
    }

    /// <summary>Площади с запасом по краю (пешеходная площадь «целиком и с тем же запасом 25 м»).</summary>
    private static Geometry AreasBuffered(STRtree<Geometry> tree, Polygon square, double margin, int quadrantSegments)
    {
        var near = tree.Query(Expanded(square, margin + 1)).ToList();
        if (near.Count == 0)
        {
            return GeoOps.EmptyPolygon();
        }

        var buffered = GeoOps.Buffer(GeoOps.Factory.BuildGeometry(near), margin, quadrantSegments);
        return buffered.IsEmpty ? buffered : GeoOps.Intersection(buffered, square);
    }

    private static Envelope Expanded(Polygon square, double by)
    {
        var envelope = new Envelope(square.EnvelopeInternal);
        envelope.ExpandBy(by);
        return envelope;
    }

    // ── Районы ───────────────────────────────────────────────────────────────

    private static DistrictData District(
        string key,
        DistrictKind kind,
        string name,
        long? osmId,
        bool proposal,
        Geometry shape,
        Geometry city,
        SortedDictionary<FogTileKey, FogTileBits> reachable,
        IReadOnlyDictionary<TileKey, TileResult> tiles)
    {
        var inCity = kind == DistrictKind.City ? shape : GeoOps.Intersection(shape, city);
        var multi = inCity is MultiPolygon m ? m : GeoOps.Factory.CreateMultiPolygon(GeoOps.Polygons(inCity).ToArray());
        var normalized = (Geometry)multi.Normalized();

        // Площадь без масок захвата (кроме «вне поля»): знаменатель доли клана в квартале (§3.6).
        var area = 0.0;
        foreach (var tile in TileKey.Covering(normalized.EnvelopeInternal))
        {
            var piece = GeoOps.Intersection(normalized, tile.ToPolygon());
            if (piece.IsEmpty)
            {
                continue;
            }

            if (tiles.TryGetValue(tile, out var result) && !result.CaptureMasks.IsEmpty)
            {
                piece = GeoOps.Difference(piece, result.CaptureMasks);
            }

            area += piece.Area;
        }

        var bits = kind == DistrictKind.City
            ? ReachableRaster.Copy(reachable)
            : ReachableRaster.Within(reachable, normalized);
        return new DistrictData(key, kind, name, osmId, proposal, normalized, Math.Round(area, 1), bits);
    }
}

/// <summary>
/// «Достижимое» на сетке тумана (osm-pipeline.md, «Растр — по центрам клеток»): клетка G22 достижима, если её центр
/// (<see cref="FogGrid.Center"/>, переведённый <see cref="Utm34.Forward"/>) внутри вектора или на его границе — то же
/// правило «центр в круге, граница включительно», что у <see cref="FogLayer.RevealAround"/>.
/// </summary>
public static class ReachableRaster
{
    public static SortedDictionary<FogTileKey, FogTileBits> Rasterize(IReadOnlyDictionary<TileKey, Geometry> vectors, Envelope area)
    {
        foreach (var geometry in vectors.Values)
        {
            _ = geometry.EnvelopeInternal; // лениво кэшируемая рамка — до параллельной части
        }

        var result = new ConcurrentDictionary<FogTileKey, FogTileBits>();
        Parallel.ForEach(FogTilesOver(area), fogTile =>
        {
            var locators = new Dictionary<TileKey, IPointOnGeometryLocator?>();
            var bits = new FogTileBits();
            var any = false;
            for (var bit = 0; bit < 65_536; bit++)
            {
                var cell = new FogCell((fogTile.X << 8) + (bit & 255), (fogTile.Y << 8) + (bit >> 8));
                var (latitude, longitude) = FogGrid.Center(cell);
                var (easting, northing) = Utm34.Forward(latitude, longitude);
                var tile = TileKey.Of(easting, northing);
                if (!locators.TryGetValue(tile, out var locator))
                {
                    locator = vectors.TryGetValue(tile, out var vector) ? new IndexedPointInAreaLocator(vector) : null;
                    locators[tile] = locator;
                }

                if (locator is not null && locator.Locate(new Coordinate(easting, northing)) != Location.Exterior)
                {
                    bits.Set(cell.BitIndex);
                    any = true;
                }
            }

            if (any)
            {
                result[fogTile] = bits;
            }
        });
        return new SortedDictionary<FogTileKey, FogTileBits>(result);
    }

    /// <summary>Клетки <paramref name="reachable"/>, центр которых внутри <paramref name="shape"/> (граница включительно).</summary>
    public static SortedDictionary<FogTileKey, FogTileBits> Within(SortedDictionary<FogTileKey, FogTileBits> reachable, Geometry shape)
    {
        var locator = new IndexedPointInAreaLocator(shape);
        var envelope = shape.EnvelopeInternal;
        var result = new SortedDictionary<FogTileKey, FogTileBits>();
        foreach (var (key, bits) in reachable)
        {
            if (!UtmEnvelope(key).Intersects(envelope))
            {
                continue;
            }

            var inside = new FogTileBits();
            var any = false;
            for (var bit = 0; bit < 65_536; bit++)
            {
                if (!bits.IsSet(bit))
                {
                    continue;
                }

                var (latitude, longitude) = FogGrid.Center(new FogCell((key.X << 8) + (bit & 255), (key.Y << 8) + (bit >> 8)));
                var (easting, northing) = Utm34.Forward(latitude, longitude);
                if (envelope.Contains(easting, northing) && locator.Locate(new Coordinate(easting, northing)) != Location.Exterior)
                {
                    inside.Set(bit);
                    any = true;
                }
            }

            if (any)
            {
                result[key] = inside;
            }
        }

        return result;
    }

    /// <summary>Рамка тайла тумана в UTM — по углам, с запасом в клетку (края в UTM слегка кривые).</summary>
    public static Envelope UtmEnvelope(FogTileKey key)
    {
        var envelope = new Envelope();
        foreach (var (dx, dy) in new[] { (0, 0), (256, 0), (0, 256), (256, 256) })
        {
            var (latitude, longitude) = FogGrid.Center(new FogCell((key.X << 8) + dx, (key.Y << 8) + dy));
            var (easting, northing) = Utm34.Forward(latitude, longitude);
            envelope.ExpandToInclude(easting, northing);
        }

        envelope.ExpandBy(10);
        return envelope;
    }

    public static SortedDictionary<FogTileKey, FogTileBits> Copy(SortedDictionary<FogTileKey, FogTileBits> tiles) =>
        new(tiles.ToDictionary(kv => kv.Key, kv => FogTileBits.FromBytes(kv.Value.ToBytes())));

    /// <summary>Тайлы тумана z14, которые задевает рамка UTM.</summary>
    public static IEnumerable<FogTileKey> FogTilesOver(Envelope area)
    {
        var corners = new[]
        {
            Utm34.Inverse(area.MinX, area.MinY), Utm34.Inverse(area.MinX, area.MaxY),
            Utm34.Inverse(area.MaxX, area.MinY), Utm34.Inverse(area.MaxX, area.MaxY),
        };
        // Края рамки UTM в градусах — кривые, поэтому с запасом в тайл: пустые тайлы в результат не попадают.
        var first = FogGrid.Cell(corners.Max(c => c.Latitude), corners.Min(c => c.Longitude)).Tile; // север — меньший y
        var last = FogGrid.Cell(corners.Min(c => c.Latitude), corners.Max(c => c.Longitude)).Tile;
        for (var x = first.X - 1; x <= last.X + 1; x++)
        {
            for (var y = first.Y - 1; y <= last.Y + 1; y++)
            {
                yield return new FogTileKey(x, y);
            }
        }
    }
}
