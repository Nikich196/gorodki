using Gorodki.Domain.Osm;
using static Gorodki.OsmPipeline.Tests.Synthetic;

namespace Gorodki.OsmPipeline.Tests;

/// <summary>Правила тегов (решения 25.09: вопросы 1.1, 1.2, 2, 6.2) — без геометрии.</summary>
public sealed class ThemesTests
{
    private static readonly Themes Rules = new(Params());

    [Theory]
    [InlineData("highway=footway")]
    [InlineData("highway=residential")]
    [InlineData("highway=service")] // вопрос 2: service и cycleway остаются в списке
    [InlineData("highway=cycleway")]
    [InlineData("highway=primary;bridge=yes")] // мосты входят
    [InlineData("highway=footway;tunnel=yes")] // подземные переходы входят
    [InlineData("highway=trunk;sidewalk=both")]
    [InlineData("highway=trunk;sidewalk=separate")] // тротуар нарисован отдельно — обычный проспект
    [InlineData("highway=trunk_link;foot=designated")]
    [InlineData("highway=service;access=private;foot=yes")]
    public void Pedestrian_paths(string tags) => Assert.True(Rules.IsPedestrianLine(Way(tags, (0, 0), (10, 0))));

    [Theory]
    [InlineData("highway=motorway")]
    [InlineData("highway=trunk")] // без тега тротуара — в маске, не в пешеходных путях (слабое место варианта А)
    [InlineData("highway=trunk;sidewalk=no")]
    [InlineData("highway=trunk;sidewalk=none")]
    [InlineData("highway=footway;foot=no")]
    [InlineData("highway=service;access=private")]
    [InlineData("highway=track;access=no")]
    [InlineData("highway=construction")]
    [InlineData("highway=proposed")]
    [InlineData("railway=rail")]
    public void Not_pedestrian_paths(string tags) => Assert.False(Rules.IsPedestrianLine(Way(tags, (0, 0), (10, 0))));

    [Theory]
    [InlineData("highway=trunk", true)]
    [InlineData("highway=trunk;sidewalk=both", false)]
    [InlineData("highway=trunk;foot=yes", false)]
    [InlineData("highway=trunk;sidewalk=yes;foot=no", true)] // пешеходам запрещено — значит, доступа нет
    [InlineData("highway=trunk_link", true)]
    [InlineData("highway=motorway", true)]
    [InlineData("highway=motorway_link;sidewalk=both", true)] // motorway — всегда
    [InlineData("highway=motorway;tunnel=yes", false)]
    [InlineData("highway=trunk;tunnel=building_passage", false)]
    [InlineData("highway=trunk;tunnel=no", true)]
    [InlineData("highway=primary", false)]
    public void Major_roads(string tags, bool masked) => Assert.Equal(masked, Rules.IsMajorRoad(Way(tags, (0, 0), (10, 0))));

    [Fact]
    public void Major_road_and_pedestrian_path_are_one_rule_seen_from_two_sides()
    {
        // Вопрос 1.2: что в маске, того нет в пешеходных путях, и наоборот — для каждого варианта тегов trunk.
        foreach (var tags in new[] { "highway=trunk", "highway=trunk;sidewalk=left", "highway=trunk;foot=no", "highway=trunk;access=private", "highway=trunk_link;sidewalk=separate" })
        {
            var way = Way(tags, (0, 0), (10, 0));
            Assert.NotEqual(Rules.IsMajorRoad(way), Rules.IsPedestrianLine(way));
        }
    }

    [Fact]
    public void Trunk_listed_as_having_a_sidewalk_is_a_path_and_not_a_mask()
    {
        var way = Way("highway=trunk", (0, 0), (10, 0));
        var rules = new Themes(Params(p => p.SidewalkWayIds.Add(way.Id)));

        Assert.True(rules.IsPedestrianLine(way));
        Assert.False(rules.IsMajorRoad(way));
    }

    [Theory]
    [InlineData("railway=rail", true)]
    [InlineData("railway=light_rail", true)]
    [InlineData("railway=narrow_gauge", true)]
    [InlineData("railway=rail;tunnel=yes", false)]
    [InlineData("railway=abandoned", false)] // заброшенные и разобранные — нет (вопрос 1.1)
    [InlineData("railway=disused", false)]
    [InlineData("railway=tram", false)]
    public void Rail_lines(string tags, bool masked) => Assert.Equal(masked, Rules.IsRailLine(Way(tags, (0, 0), (10, 0))));

    [Theory]
    [InlineData("natural=water", MaskKind.Water)]
    [InlineData("waterway=riverbank", MaskKind.Water)]
    [InlineData("landuse=reservoir", MaskKind.Water)]
    [InlineData("landuse=basin", MaskKind.Water)]
    [InlineData("landuse=railway", MaskKind.Rail)]
    [InlineData("landuse=military", MaskKind.Military)]
    [InlineData("military=barracks", MaskKind.Military)]
    [InlineData("landuse=cemetery", MaskKind.Cemetery)]
    [InlineData("amenity=grave_yard", MaskKind.Cemetery)]
    public void Area_masks(string tags, MaskKind kind) => Assert.Equal(kind, Rules.AreaMaskKind(Area(tags, Box(0, 0, 10, 10))));

    [Fact]
    public void Area_themes_take_only_polygons_and_path_themes_only_lines()
    {
        // osmium выдаёт замкнутую линию и линией, и многоугольником (без area=no): каждая тема берёт только свои типы.
        Assert.Null(Rules.AreaMaskKind(Way("natural=water", (0, 0), (10, 0), (10, 10), (0, 0))));
        Assert.False(Rules.IsPedestrianLine(Area("highway=footway", Box(0, 0, 10, 10))));
        Assert.False(Rules.IsPedestrianArea(Area("highway=footway", Box(0, 0, 10, 10))));
        Assert.False(Rules.IsPedestrianArea(Area("highway=pedestrian", Box(0, 0, 10, 10)))); // без area=yes — кольцо улицы
        Assert.True(Rules.IsPedestrianArea(Area("highway=pedestrian;area=yes", Box(0, 0, 10, 10))));
        Assert.True(Rules.IsPedestrianArea(Area("highway=pedestrian", Box(0, 0, 10, 10), type: "relation")));
        Assert.True(Rules.IsPedestrianArea(Area("place=square", Box(0, 0, 10, 10))));
    }

    [Theory]
    [InlineData("landuse=farmland", true)]
    [InlineData("landuse=forest", true)]
    [InlineData("landuse=industrial", true)]
    [InlineData("natural=wood", true)]
    [InlineData("landuse=residential", false)]
    public void Low_value_land(string tags, bool low) => Assert.Equal(low, Rules.IsLowValueLand(Area(tags, Box(0, 0, 10, 10))));

    [Fact]
    public void Committed_parameters_are_the_decisions_of_25_09()
    {
        var parameters = PipelineParams.FromJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "osm-pipeline.json")));

        Assert.Equal(10, parameters.Rail.HalfWidthMeters); // 1.1
        Assert.Equal(12, parameters.MajorRoads.HalfWidthMeters); // 1.2
        Assert.Null(parameters.BorderStripMeters); // 1.3: ширины нет — слоя нет
        Assert.Equal(25, parameters.Pedestrian.BufferMeters); // §3.10 = радиус тумана
        Assert.Contains("cycleway", parameters.Pedestrian.Highways); // 2
        Assert.Contains("service", parameters.Pedestrian.Highways);
        Assert.Equal(50, parameters.Memorials.PointRadiusMeters); // 6.6
        Assert.Empty(parameters.Memorials.Objects); // список утверждает Никита
        Assert.Empty(parameters.MajorRoads.SidewalkWayIds);
        Assert.Equal([3626404, 3626405], parameters.DistrictRelationIds); // 6.3: Ленинский и Московский
        Assert.Equal(0, parameters.MaskSimplifyMeters);
        Assert.Empty(parameters.KnownBrokenRelations); // исключений из «все площади собраны» нет
        Assert.Empty(parameters.KnownBrokenWays);
        Assert.Null(parameters.KnownAreaErrors);
    }
}
