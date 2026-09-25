using Gorodki.Domain.Fog;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Osm;
using NetTopologySuite.Algorithm.Locate;
using NetTopologySuite.Geometries;
using static Gorodki.OsmPipeline.Tests.Synthetic;

namespace Gorodki.OsmPipeline.Tests;

/// <summary>
/// Сборка на синтетике (osm-pipeline.md, «Проверка»): маски по видам, «достижимое» на сетке тумана, районы. Ожидания —
/// из решений 25.09 и из геометрии, а не подобраны под результат.
/// </summary>
public sealed class SetBuilderTests
{
    private static OsmSetData Build(PipelineParams parameters, params OsmFeature[] features) =>
        SetBuilder.Build(Input(features), parameters, Tiles);

    private static OsmSetData Build(params OsmFeature[] features) => Build(Params(), features);

    private static Geometry Masks(OsmSetData data, MaskKind kind) =>
        GeoOps.UnionAll(data.Masks.Where(m => m.Kind == kind).Select(m => (Geometry)m.Geometry));

    /// <summary>Достижима ли клетка тумана, в которой лежит точка (метры от угла рамки).</summary>
    private static bool Reachable(OsmSetData data, double x, double y)
    {
        var (latitude, longitude) = Utm34.Inverse(X0 + x, Y0 + y);
        var cell = FogGrid.Cell(latitude, longitude);
        return data.Reachable.TryGetValue(cell.Tile, out var bits) && bits.IsSet(cell.BitIndex);
    }

    [Fact]
    public void Footway_across_a_river_has_a_gap_where_the_water_is()
    {
        var data = Build(
            Way("highway=footway", (300, 1000), (1700, 1000)),
            Area("waterway=riverbank", Box(900, 0, 1100, 2000)));

        Assert.True(Reachable(data, 500, 1000));
        Assert.True(Reachable(data, 1500, 1000));
        Assert.False(Reachable(data, 1000, 1000)); // середина реки
        Assert.False(Reachable(data, 910, 1010));
        Assert.Equal(200 * 1800, Masks(data, MaskKind.Water).Area, 1); // вода — в границах города
        Assert.Empty(SetVerifier.Verify(data));
    }

    [Fact]
    public void Closed_path_around_a_pond_is_a_ring_not_a_disk()
    {
        // osmium отдаёт замкнутую дорожку и линией, и многоугольником: многоугольник в «достижимое» не идёт.
        var circle = (Polygon)GeoOps.Buffer(At(1000, 1000), 100, quadrantSegments: 16);
        var ring = new OsmFeature("way", 77, Tags("highway=footway"), GeoOps.Factory.CreateLineString(circle.Shell.Coordinates));
        var sameAsArea = new OsmFeature("way", 77, Tags("highway=footway"), circle);

        var data = Build(ring, sameAsArea);

        Assert.True(Reachable(data, 1000, 1100)); // на дорожке
        Assert.True(Reachable(data, 1000, 1120)); // 20 м от неё — в полосе 25 м
        Assert.False(Reachable(data, 1000, 1050)); // 50 м внутрь — пруд
        Assert.False(Reachable(data, 1000, 1000));
    }

    [Fact]
    public void Rail_mask_is_exactly_twice_the_half_width_and_ends_at_the_city_border()
    {
        var data = Build(Way("railway=rail", (0, 1000), (2000, 1000)));
        var rail = Masks(data, MaskKind.Rail);

        Assert.Equal(20 * 1800, rail.Area, 1); // 2 × 10 м (вопрос 1.1) на 1,8 км города — концы вне города
        Assert.Equal(Y0 + 990, rail.EnvelopeInternal.MinY, 6);
        Assert.Equal(Y0 + 1010, rail.EnvelopeInternal.MaxY, 6);
        Assert.Equal(X0 + 100, rail.EnvelopeInternal.MinX, 6);
    }

    [Fact]
    public void Rail_and_major_road_in_a_tunnel_have_no_mask()
    {
        var data = Build(
            Way("railway=rail;tunnel=yes", (0, 500), (2000, 500)),
            Way("highway=trunk;tunnel=yes", (0, 800), (2000, 800)),
            Way("highway=motorway;tunnel=yes", (0, 1200), (2000, 1200)));

        Assert.DoesNotContain(data.Masks, m => m.Kind is MaskKind.Rail or MaskKind.MajorRoad);
    }

    [Fact]
    public void Trunk_with_a_sidewalk_is_reachable_and_without_one_is_a_mask()
    {
        var data = Build(
            Way("highway=trunk;sidewalk=both", (100, 500), (1900, 500)),
            Way("highway=trunk", (100, 1500), (1900, 1500)));
        var major = Masks(data, MaskKind.MajorRoad);

        Assert.True(Reachable(data, 1000, 500));
        Assert.False(major.Intersects(At(1000, 500)));
        Assert.False(Reachable(data, 1000, 1500));
        Assert.True(major.Covers(At(1000, 1511.9)));
        Assert.False(major.Covers(At(1000, 1512.1))); // 12 м от оси (вопрос 1.2)
    }

    [Fact]
    public void Footway_crossing_a_major_road_is_cut_by_its_mask()
    {
        var data = Build(
            Way("highway=footway", (1000, 200), (1000, 1800)),
            Way("highway=trunk", (100, 1000), (1900, 1000)));

        Assert.True(Reachable(data, 1000, 800));
        Assert.False(Reachable(data, 1000, 1000)); // маски вычитаются из «достижимого» (вопрос 6.2, Б)
    }

    [Fact]
    public void Memorial_is_a_capture_mask_but_stays_reachable()
    {
        // Вопрос 6.2, Б: «достижимое» — минус все маски, кроме мемориалов; точечный памятник — круг 50 м (6.6).
        var monument = Node("historic=memorial", 1000, 1000);
        var data = Build(Params(p => p.Memorials.Add(monument.Ref)), monument, Way("highway=footway", (300, 1000), (1700, 1000)));

        Assert.Equal(Math.PI * 50 * 50, Masks(data, MaskKind.Memorial).Area, 0.02 * Math.PI * 50 * 50);
        Assert.True(Reachable(data, 1000, 1000));
    }

    [Fact]
    public void Multipolygon_with_an_island_masks_the_water_and_not_the_island()
    {
        var lake = (Polygon)GeoOps.Difference(Box(600, 600, 1400, 1400), Box(900, 900, 1100, 1100));
        var data = Build(
            Area("natural=water", lake, type: "relation"),
            Way("highway=path", (950, 1000), (1050, 1000))); // тропинка на острове

        Assert.Equal(800 * 800 - 200 * 200, Masks(data, MaskKind.Water).Area, 1);
        Assert.True(Reachable(data, 1000, 1000));
        Assert.False(Reachable(data, 700, 700));
    }

    [Fact]
    public void Border_strip_is_clipped_by_the_city_and_absent_without_a_width()
    {
        var line = GeoOps.Factory.CreateLineString([new Coordinate(X0 + 1000, Y0 - 500), new Coordinate(X0 + 1000, Y0 + 2500)]);
        var input = new OsmInput { Features = [], City = City(), BorderLine = line };

        var withWidth = SetBuilder.Build(input, Params(p => p.BorderStripMeters = 50), Tiles);
        var without = SetBuilder.Build(input, Params(), Tiles);

        var strip = Masks(withWidth, MaskKind.Border);
        Assert.Equal(100 * 1800, strip.Area, 1);
        Assert.True(City().Covers(strip));
        Assert.DoesNotContain(without.Masks, m => m.Kind == MaskKind.Border);
        Assert.Contains(without.Notes, n => n.Contains("Погранполоса выключена", StringComparison.Ordinal));
    }

    [Fact]
    public void Pieces_of_all_tiles_add_up_to_the_whole()
    {
        var diagonal = Way("railway=rail", (0, 0), (2000, 2000));
        var data = Build(diagonal);
        var whole = GeoOps.Intersection(GeoOps.Buffer(diagonal.Geometry, 10), City());
        var pieces = Masks(data, MaskKind.Rail);

        Assert.Equal(4, data.Masks.Count(m => m.Kind == MaskKind.Rail)); // по куску в каждом из 4 тайлов
        Assert.Equal(whole.Area, pieces.Area, 0.5); // snap-rounding: не больше 0,075 м × длину границы
        Assert.True(GeoOps.Difference(whole, pieces).Area < 0.5);
        Assert.True(GeoOps.Difference(pieces, whole).Area < 0.5);
    }

    [Fact]
    public void Raster_is_the_direct_test_of_cell_centres_against_the_vector()
    {
        var footway = Way("highway=footway", (150, 300), (1850, 1700));
        var data = Build(footway);
        var vector = GeoOps.Intersection(GeoOps.Buffer(footway.Geometry, 25), City());
        var locator = new IndexedPointInAreaLocator(vector);
        var boundary = vector.Boundary;

        var checkedCells = 0;
        foreach (var tile in ReachableRaster.FogTilesOver(City().EnvelopeInternal))
        {
            data.Reachable.TryGetValue(tile, out var bits);
            for (var bit = 0; bit < 65_536; bit += 7)
            {
                var (latitude, longitude) = FogGrid.Center(new FogCell((tile.X << 8) + (bit & 255), (tile.Y << 8) + (bit >> 8)));
                var (easting, northing) = Utm34.Forward(latitude, longitude);
                var center = new Coordinate(easting, northing);
                if (boundary.Distance(GeoOps.Factory.CreatePoint(center)) < 0.2)
                {
                    continue; // у самой границы тайловый и цельный буфер могут разойтись на сетку 0,1 м
                }

                Assert.Equal(locator.Locate(center) != Location.Exterior, bits?.IsSet(bit) ?? false);
                checkedCells++;
            }
        }

        Assert.True(checkedCells > 100_000);
    }

    [Fact]
    public void Raster_cells_are_the_cells_of_the_fog_grid()
    {
        // Крошечная площадь вокруг центра одной клетки G22 даёт ровно ту клетку, что открывает туман телефона и сервера.
        var (latitude, longitude) = Utm34.Inverse(X0 + 1000, Y0 + 1000);
        var cell = FogGrid.Cell(latitude, longitude);
        var (centerLat, centerLon) = FogGrid.Center(cell);
        var (e, n) = Utm34.Forward(centerLat, centerLon);
        var square = Rectangle(Math.Round(e - 1, 1), Math.Round(n - 1, 1), Math.Round(e + 1, 1), Math.Round(n + 1, 1));
        var fog = new FogLayer();
        fog.RevealAround(centerLat, centerLon, 1);

        var raster = ReachableRaster.Rasterize(new Dictionary<TileKey, Geometry> { [TileKey.Of(e, n)] = square }, square.EnvelopeInternal);

        var (tile, bits) = Assert.Single(raster);
        Assert.Equal(1, bits.Count);
        Assert.True(bits.IsSet(cell.BitIndex));
        Assert.Equal(cell.Tile, tile);
        Assert.Equal(fog.Tiles[tile].ToBytes(), bits.ToBytes());
    }

    [Fact]
    public void Walking_the_path_with_the_fog_radius_opens_almost_all_of_its_reachable_strip()
    {
        // Полоса «достижимого» — ровно та, что открывает прогулка по оси пути (буфер = радиус тумана, 25 м).
        var data = Build(Way("highway=footway", (300, 1000), (1700, 1000)));
        var (latA, lonA) = Utm34.Inverse(X0 + 300, Y0 + 1000);
        var (latB, lonB) = Utm34.Inverse(X0 + 1700, Y0 + 1000);
        var fog = new FogLayer();
        fog.RevealPath(latA, lonA, latB, lonB, 25, maxGap: 10_000);

        var share = new ReachableArea(data.Reachable).ShareOf(fog.Tiles);

        Assert.InRange(share.Percent, 97, 100); // край — поправка меркатора против UTM и округление полосы до клеток
    }

    [Fact]
    public void Outside_mask_is_the_frame_minus_the_city_when_capture_is_limited_to_the_city()
    {
        var data = Build(Params(p => p.PlayZone = PlayZone.City));
        var outside = Masks(data, MaskKind.Outside);

        Assert.Equal((2000 * 2000) - (1800 * 1800), outside.Area, 1);
        Assert.False(outside.Intersects(At(1000, 1000)));
        Assert.DoesNotContain(Build(Params()).Masks, m => m.Kind == MaskKind.Outside);
    }

    [Fact]
    public void Districts_have_reachable_cells_inside_them_only()
    {
        var left = new DistrictInput(11, "Левый", Box(100, 100, 1000, 1900));
        var right = new DistrictInput(12, "Правый", Box(1000, 100, 1900, 1900));
        var input = new OsmInput
        {
            Features = [Way("highway=footway", (200, 1000), (1800, 1000)), Area("natural=water", Box(1200, 100, 1300, 1900))],
            City = City(),
            Districts = [left, right],
        };

        var data = SetBuilder.Build(input, Params(), Tiles);

        var city = data.Districts.Single(d => d.Kind == DistrictKind.City);
        var districts = data.Districts.Where(d => d.Kind == DistrictKind.District).ToList();
        Assert.Equal(2, districts.Count);
        Assert.InRange(districts.Sum(d => d.CellCount), city.CellCount, city.CellCount + 60); // клетка на стыке — в обоих (граница включительно)
        Assert.Equal((900 * 1800) - (100 * 1800), districts.Single(d => d.OsmId == 12).AreaWithoutMasks, 1); // без реки
        Assert.Equal(900 * 1800, districts.Single(d => d.OsmId == 11).AreaWithoutMasks, 1);
        Assert.Empty(SetVerifier.Verify(data));
    }

    [Fact]
    public void Same_input_and_versions_give_the_same_fingerprint()
    {
        OsmFeature[] Scene() =>
        [
            Way("highway=footway", (300, 1000), (1700, 1000)),
            Way("railway=rail", (0, 0), (2000, 2000)),
            Area("natural=water", Box(900, 0, 1100, 2000)),
            Area("landuse=cemetery", Box(200, 1500, 400, 1700)),
        ];
        var parameters = Params(p => p.PlayZone = PlayZone.City);

        var first = OsmSetFile.Fingerprint(Build(parameters, Scene()), parameters.ToCanonicalJson());
        var second = OsmSetFile.Fingerprint(Build(parameters, [.. Scene().Reverse()]), parameters.ToCanonicalJson());

        Assert.Equal(first, second);
    }

    [Fact]
    public void Verifier_catches_a_reachable_cell_inside_a_mask()
    {
        var data = Build(Way("highway=footway", (300, 1000), (1700, 1000)));
        var broken = new OsmSetData
        {
            Frame = data.Frame,
            PlayZone = data.PlayZone,
            Masks = [.. GeoOps.Polygons(GeoOps.Intersection(Box(900, 900, 1100, 1100), new TileKey(684, 5774).ToPolygon()))
                .Select(p => new MaskPiece(MaskKind.Water, new TileKey(684, 5774), p))],
            Land = data.Land,
            Reachable = data.Reachable,
            Districts = data.Districts,
            Notes = data.Notes,
        };

        Assert.Contains(SetVerifier.Verify(broken), p => p.Contains("с центром внутри маски", StringComparison.Ordinal));
    }
}
