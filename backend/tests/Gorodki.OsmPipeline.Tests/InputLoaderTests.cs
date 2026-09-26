using System.Security.Cryptography;
using System.Text;
using static Gorodki.OsmPipeline.Tests.Synthetic;

namespace Gorodki.OsmPipeline.Tests;

/// <summary>
/// Сверка «все площади собраны» (osm-pipeline.md, «Проверка») на поддельных файлах extract во временной папке, без
/// osmium. Файлы — как их пишет osmium 1.19.1 / libosmium 2.23.1: «восьмёрка» <c>landuse=military</c> выходит из
/// <c>export</c> только линией, в журнале — строка без id, код выхода 0 (проверено 26.09).
/// </summary>
public sealed class InputLoaderTests : IDisposable
{
    private const string AreaError = "Geometry error: Could not build area geometry\n";

    /// <summary>«Восьмёрка»: диагонали кольца пересекаются — многоугольника нет, только линия.</summary>
    private const string BowTieOpl = "w100 v1 dV c0 t i0 u Tlanduse=military Nn1,n2,n3,n4,n1";

    private static readonly string BowTieLine =
        GeoJsonLine("way", 100, "landuse=military", "LineString", LonLat((500, 500), (600, 500), (500, 600), (600, 600), (500, 500)));

    private readonly string _work = Directory.CreateTempSubdirectory("gorodki-osm-input-").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private void Write(string file, params string[] lines) => File.WriteAllLines(Path.Combine(_work, file), lines);

    private void WriteText(string file, string text) => File.WriteAllText(Path.Combine(_work, file), text);

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Ring(double x1, double y1, double x2, double y2) => LonLat((x1, y1), (x2, y1), (x2, y2), (x1, y2), (x1, y1));

    /// <summary>Папка, как после extract: темы пустые, кроме площадей; границы города (r1) и страны (r2); source.json.</summary>
    private void WorkFolder(string[] areas, string[] ways, string areaErrors)
    {
        foreach (var theme in new[] { "lines", "areas", "buildings", "memorials" })
        {
            Write($"{theme}.geojsonseq");
            Write($"{theme}.relations.opl");
            WriteText($"{theme}.errors.txt", "");
        }

        Write("areas.geojsonseq", areas);
        Write(Extract.WaysFile, ways);
        WriteText("areas.errors.txt", areaErrors);
        Write(
            "borders.geojsonseq",
            GeoJsonLine("relation", 1, "boundary=administrative", "Polygon", $"[{Ring(100, 100, 1_900, 1_900)}]"),
            GeoJsonLine("relation", 2, "boundary=administrative", "Polygon", $"[{Ring(-500, -500, 2_500, 2_500)}]"));
        WriteText("source.json", "{}");
    }

    /// <summary>Кладбище w101 собрано (osmium выдаёт замкнутый путь и линией, и многоугольником), военный объект w100 — нет.</summary>
    private void BowTieFolder() => WorkFolder(
        [
            BowTieLine,
            GeoJsonLine("way", 101, "landuse=cemetery", "LineString", Ring(800, 800, 900, 900)),
            GeoJsonLine("way", 101, "landuse=cemetery", "MultiPolygon", $"[[{Ring(800, 800, 900, 900)}]]"),
        ],
        [BowTieOpl, "w101 v1 dV c0 t i0 u Tlanduse=cemetery Nn11,n12,n13,n14,n11"],
        AreaError);

    [Fact]
    public void Closed_way_of_a_mask_exported_only_as_a_line_is_a_problem()
    {
        BowTieFolder();
        var (problems, notes) = (new List<string>(), new List<string>());

        InputLoader.CheckClosedWays(
            File.ReadLines(Path.Combine(_work, Extract.WaysFile)), GeoJsonSeq.ReadFile(Path.Combine(_work, "areas.geojsonseq")).ToList(), Params(), problems, notes);

        var problem = Assert.Single(problems);
        Assert.Contains("w100", problem, StringComparison.Ordinal);
        Assert.Contains("маска military", problem, StringComparison.Ordinal);
        Assert.Empty(notes);
    }

    [Fact]
    public void Known_broken_way_is_a_note_and_not_a_problem()
    {
        BowTieFolder();
        var (problems, notes) = (new List<string>(), new List<string>());
        var parameters = Params() with { KnownBrokenWays = [new KnownBrokenWay(100, "вне города, правка в OSM отправлена")] };

        InputLoader.CheckClosedWays(
            File.ReadLines(Path.Combine(_work, Extract.WaysFile)), GeoJsonSeq.ReadFile(Path.Combine(_work, "areas.geojsonseq")).ToList(), parameters, problems, notes);

        Assert.Empty(problems);
        var note = Assert.Single(notes);
        Assert.Contains("w100", note, StringComparison.Ordinal);
        Assert.Contains("правка в OSM отправлена", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Set_is_not_released_with_an_unassembled_mask_way_or_a_non_empty_area_log()
    {
        BowTieFolder();

        var loaded = InputLoader.Load(Params(), _work);

        Assert.Equal(1L, loaded.Input.CityOsmId); // папка читается целиком, границы — на месте
        Assert.Equal(2, loaded.Problems.Count);
        Assert.Contains(loaded.Problems, p => p.Contains("w100", StringComparison.Ordinal));
        Assert.Contains(loaded.Problems, p => p.Contains("areas.errors.txt не пуст", StringComparison.Ordinal));
        Assert.DoesNotContain(loaded.Problems, p => p.Contains("w101", StringComparison.Ordinal));
    }

    [Fact]
    public void Area_log_accepted_by_its_sha256_is_a_note_and_another_log_is_still_a_problem()
    {
        BowTieFolder();
        var known = Params() with { KnownBrokenWays = [new KnownBrokenWay(100, "разобрано")] };

        var accepted = InputLoader.Load(known with { KnownAreaErrors = new KnownErrorLog(Sha256(AreaError), "это w100") }, _work);
        var stale = InputLoader.Load(known with { KnownAreaErrors = new KnownErrorLog(Sha256("старый журнал\n"), "прошлый набор") }, _work);

        Assert.Empty(accepted.Problems);
        Assert.Contains(accepted.Notes, n => n.Contains("разобран: это w100", StringComparison.Ordinal));
        Assert.Contains(stale.Problems, p => p.Contains("areas.errors.txt не пуст", StringComparison.Ordinal));
    }

    [Fact]
    public void Work_folder_of_an_older_extract_without_the_ways_file_is_a_problem()
    {
        BowTieFolder();
        File.Delete(Path.Combine(_work, Extract.WaysFile));

        Assert.Contains(InputLoader.Load(Params(), _work).Problems, p => p.Contains(Extract.WaysFile, StringComparison.Ordinal));
    }

    [Fact]
    public void Only_closed_ways_with_area_tags_of_the_set_are_checked()
    {
        string[] ways =
        [
            "w102 v1 dV c0 t i0 u T Nn1,n2,n3,n1", // член отношения без тегов — его тянет tags-filter
            "w103 v1 dV c0 t i0 u Tlanduse=forest Nn1,n2,n3,n1", // земля ×0,5 — потеря консервативна
            "w104 v1 dV c0 t i0 u Tarea=no,natural=water Nn1,n2,n3,n1", // area=no — многоугольника и не ждём
            "w105 v1 dV c0 t i0 u Thighway=pedestrian Nn1,n2,n3,n1", // кольцо улицы, не площадь
            "w106 v1 dV c0 t i0 u Tarea=yes,highway=pedestrian Nn1,n2,n3,n1", // пешеходная площадь — примечание
            "w107 v1 dV c0 t i0 u Tnatural=water Nn1,n2,n3", // не замкнут
            "w108 v1 dV c0 t i0 u Tnatural=water Nn1,n2,n3,n1", // собран
            "w109 v1 dV c0 t i0 u Twaterway=riverbank Nn1,n2,n1", // из трёх ссылок многоугольника не бывает
        ];
        var exported = new List<OsmFeature> { new("way", 108, Tags("natural=water"), Box(0, 0, 10, 10)) };
        var (problems, notes) = (new List<string>(), new List<string>());

        InputLoader.CheckClosedWays(ways, exported, Params(), problems, notes);

        Assert.Empty(problems);
        Assert.Equal(2, notes.Count);
        Assert.Contains("w103 (земля ×0,5)", notes[0], StringComparison.Ordinal);
        Assert.Contains("w106 (пешеходная площадь)", notes[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Error_log_of_paths_is_not_read_and_of_buildings_is_only_a_note()
    {
        var (problems, notes) = (new List<string>(), new List<string>());

        // Самопересекающаяся замкнутая дорожка пишет ту же ошибку, а нужна ей только линия.
        InputLoader.CheckErrorLog("lines", AreaError, Params(), problems, notes);
        InputLoader.CheckErrorLog("areas", " \n", Params(), problems, notes);
        Assert.Empty(problems);
        Assert.Empty(notes);

        InputLoader.CheckErrorLog("buildings", AreaError, Params(), problems, notes);
        Assert.Empty(problems);
        Assert.Contains("buildings.errors.txt", Assert.Single(notes), StringComparison.Ordinal);
    }

    [Fact]
    public void Osmium_opl_way_line_gives_id_unescaped_tags_and_whether_it_is_closed()
    {
        // Строка — вывод osmium 1.19.1 для пути с «Brest lake, pond=50% @x» и кириллицей в тегах.
        var (id, tags, closed) = InputLoader.ParseOplWay(
            "w7 v1 dV c0 t i0 u Tname=Brest%20%lake%2c%%20%pond%3d%50%25%%20%%40%x,name:ru=Озеро%20%Брест,natural=water Nn1,n2,n3,n1");

        Assert.Equal(7L, id);
        Assert.True(closed);
        Assert.Equal("Brest lake, pond=50% @x", tags["name"]);
        Assert.Equal("Озеро Брест", tags["name:ru"]);
        Assert.Equal("water", tags["natural"]);
        Assert.False(InputLoader.ParseOplWay("w8 v1 dV c0 t i0 u Tnatural=water Nn1,n2,n3,n4").Closed);
        Assert.Empty(InputLoader.ParseOplWay("w9 v1 dV c0 t i0 u T N").Tags);
    }
}
