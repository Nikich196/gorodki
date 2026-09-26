using Gorodki.Domain.Geo;
using Gorodki.Domain.Osm;
using static Gorodki.OsmPipeline.Tests.Synthetic;

namespace Gorodki.OsmPipeline.Tests;

/// <summary>
/// Арена по рецепту (вопрос 3): граница и кварталы — по улицам, радиус — ориентир. Синтетика: сетка улиц 400 м вокруг
/// «корпуса» в центре города, радиус 700 м — внутренние 6 кварталов почти целиком в круге, внешние — меньше чем наполовину.
/// </summary>
public sealed class ArenaBuilderTests
{
    private static readonly OsmFeature Campus = Area("building=university", Box(990, 990, 1010, 1010));

    private static List<OsmFeature> Streets() =>
    [
        Way("highway=primary;name=A", (400, 0), (400, 2000)),
        Way("highway=primary;name=B", (800, 0), (800, 2000)),
        Way("highway=primary;name=C", (1200, 0), (1200, 2000)),
        Way("highway=primary;name=D", (1600, 0), (1600, 2000)),
        Way("highway=primary;name=E", (0, 400), (2000, 400)),
        Way("highway=primary;name=F", (0, 1000), (2000, 1000)),
        Way("highway=primary;name=G", (0, 1600), (2000, 1600)),
        Way("highway=residential;name=Тихая", (0, 1300), (2000, 1300)), // не улица разреза
    ];

    private static QuarterLabel Label(string name, double x, double y)
    {
        var (latitude, longitude) = Utm34.Inverse(X0 + x, Y0 + y);
        return new QuarterLabel(name, latitude, longitude);
    }

    private static ArenaRecipe Recipe(params QuarterLabel[] labels) => new()
    {
        Anchors = [Campus.Ref],
        RadiusMeters = 700,
        CutStreets = ["A", "B", "C", "D", "E", "F", "G"],
        Quarters = labels.Length > 0 ? labels :
        [
            Label("Юго-запад", 600, 700), Label("Юг", 1000, 700), Label("Юго-восток", 1400, 700),
            Label("Северо-запад", 600, 1300), Label("Север", 1000, 1300), Label("Северо-восток", 1400, 1300),
        ],
    };

    [Fact]
    public void Arena_is_the_street_faces_mostly_within_the_radius_and_each_labelled_face_is_a_quarter()
    {
        var result = ArenaBuilder.Build(Recipe(), [Campus, .. Streets()], City());

        Assert.Equal(6, result.Quarters.Count);
        Assert.Equal(1200 * 1200, result.Arena.Area, 1); // от улицы A до D и от E до G
        Assert.All(result.Quarters, q => Assert.Equal(400 * 600, q.Shape.Area, 1));
        Assert.True(result.Proposal);
        Assert.Contains(result.Notes, n => n.Contains("предложение", StringComparison.Ordinal));
    }

    [Fact]
    public void Face_without_a_label_joins_the_neighbour_with_the_longest_shared_border()
    {
        // Улица H режет квартал «Северо-запад» надвое; метка — только в нижней половине.
        var streets = Streets();
        streets.Add(Way("highway=secondary;name=H", (400, 1450), (800, 1450)));
        var recipe = Recipe() with { CutStreets = [.. Recipe().CutStreets, "H"] };

        var result = ArenaBuilder.Build(recipe, [Campus, .. streets], City());

        Assert.Equal(400 * 600, result.Quarters.Single(q => q.Name == "Северо-запад").Shape.Area, 1);
        Assert.Equal(1200 * 1200, result.Arena.Area, 1);
    }

    [Fact]
    public void Recipe_errors_are_refused_not_guessed()
    {
        Assert.Throws<InvalidOperationException>(() => ArenaBuilder.Build(Recipe() with { Anchors = ["way/1"] }, [Campus, .. Streets()], City()));
        Assert.Throws<InvalidOperationException>(() => ArenaBuilder.Build(Recipe() with { CutStreets = ["Нет такой"] }, [Campus, .. Streets()], City()));
        Assert.Throws<InvalidOperationException>(() => ArenaBuilder.Build(Recipe(Label("За городом", 200, 200)), [Campus, .. Streets()], City()));
        Assert.Throws<InvalidOperationException>(() => ArenaBuilder.Build(Recipe(Label("Раз", 600, 700), Label("Два", 610, 710)), [Campus, .. Streets()], City()));
    }

    [Fact]
    public void Arena_and_quarters_are_districts_marked_as_a_proposal()
    {
        var parameters = Params(p => p.Arena = Recipe());
        var data = SetBuilder.Build(new OsmInput
        {
            Features = [Campus, .. Streets(), Way("highway=footway", (500, 700), (1500, 700))],
            City = City(),
        }, parameters, Tiles);

        var arena = data.Districts.Single(d => d.Kind == DistrictKind.Arena);
        var quarters = data.Districts.Where(d => d.Kind == DistrictKind.Quarter).ToList();
        Assert.True(arena.Proposal);
        Assert.All(quarters, q => Assert.True(q.Proposal));
        Assert.Equal(6, quarters.Count);
        Assert.InRange(quarters.Sum(q => q.CellCount), arena.CellCount, arena.CellCount + 40); // клетка на улице разреза — в двух кварталах
        Assert.True(data.Reachable.Values.Sum(b => b.Count) > arena.CellCount); // часть «достижимого» — вне Арены
        Assert.All(quarters, q => Assert.True(q.CellCount > 0)); // улицы разреза — сами пешеходные пути
        Assert.Empty(SetVerifier.Verify(data));
    }

    [Fact]
    public void Verifier_wants_six_to_eight_quarters()
    {
        var parameters = Params(p => p.Arena = Recipe(Label("Один", 600, 700), Label("Два", 1000, 700), Label("Три", 1400, 700),
            Label("Четыре", 600, 1300), Label("Пять", 1000, 1300)));
        var data = SetBuilder.Build(new OsmInput { Features = [Campus, .. Streets()], City = City() }, parameters, Tiles);

        Assert.Contains(SetVerifier.Verify(data), p => p.Contains("6–8", StringComparison.Ordinal));
    }
}
