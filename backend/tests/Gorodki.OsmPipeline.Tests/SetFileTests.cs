using System.IO.Compression;
using System.Text;
using Gorodki.Domain.Osm;
using NetTopologySuite.Geometries;
using static Gorodki.OsmPipeline.Tests.Synthetic;

namespace Gorodki.OsmPipeline.Tests;

/// <summary>Файл набора: читается обратно без потерь, отпечаток — по содержимому, порча файла ловится.</summary>
public sealed class SetFileTests
{
    private static (OsmSetData Data, PipelineParams Parameters) Scene()
    {
        var parameters = Params(p => p.PlayZone = PlayZone.City);
        var input = new OsmInput
        {
            Features =
            [
                Way("highway=footway", (300, 1000), (1700, 1000)),
                Way("railway=rail", (0, 500), (2000, 500)),
                Area("natural=water", Box(900, 0, 1100, 2000)),
                Area("landuse=forest", Box(1500, 1500, 1800, 1800)),
            ],
            City = City(),
            Districts = [new DistrictInput(11, "Левый", Box(100, 100, 1000, 1900))],
        };
        return (SetBuilder.Build(input, parameters, Tiles), parameters);
    }

    [Fact]
    public void Set_round_trips_through_the_file_with_the_same_fingerprint()
    {
        var (data, parameters) = Scene();
        var path = Path.Combine(Path.GetTempPath(), $"osm-set-test-{Guid.NewGuid():N}.zip");
        try
        {
            var source = new SetSource { File = "belarus-latest.osm.pbf", Sha256 = new string('a', 64), ReplicationTimestamp = DateTimeOffset.UnixEpoch };
            OsmSetFile.Write(path, 7, data, parameters, source, DateTimeOffset.UtcNow);

            var read = OsmSetFile.Read(path);

            Assert.Equal(7, read.Version);
            Assert.Equal(OsmSetFile.Fingerprint(data, parameters.ToCanonicalJson()), read.Fingerprint);
            Assert.Equal(data.Masks.Count, read.Data.Masks.Count);
            Assert.All(data.Masks.Zip(read.Data.Masks), pair =>
            {
                Assert.Equal(pair.First.Kind, pair.Second.Kind);
                Assert.Equal(pair.First.Tile, pair.Second.Tile);
                Assert.True(pair.First.Geometry.EqualsExact(pair.Second.Geometry));
            });
            Assert.Equal(data.ReachableCells, read.Data.ReachableCells);
            Assert.Equal(data.Land.Count, read.Data.Land.Count);
            Assert.Equal(data.Districts.Select(d => (d.Key, d.CellCount)), read.Data.Districts.Select(d => (d.Key, d.CellCount)));
            Assert.Equal(PlayZone.City, read.Data.PlayZone);
            Assert.Equal(source.Sha256, read.Source.Sha256);
            Assert.Contains("OpenStreetMap", (string)read.Metadata["attribution"]!, StringComparison.Ordinal);
            using var zip = ZipFile.OpenRead(path);
            Assert.NotNull(zip.GetEntry("LICENSE.txt"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Changed_content_does_not_pass_the_fingerprint_check()
    {
        var (data, parameters) = Scene();
        var path = Path.Combine(Path.GetTempPath(), $"osm-set-test-{Guid.NewGuid():N}.zip");
        try
        {
            OsmSetFile.Write(path, 7, data, parameters, new SetSource(), DateTimeOffset.UtcNow);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("masks.jsonl")!;
                string text;
                using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
                {
                    text = reader.ReadToEnd();
                }

                entry.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("masks.jsonl").Open(), Encoding.UTF8);
                writer.Write(string.Join('\n', text.Split('\n').Skip(1))); // одна маска пропала
            }

            Assert.Throws<FormatException>(() => OsmSetFile.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Fingerprint_does_not_depend_on_the_build_time_but_on_every_piece()
    {
        var (data, parameters) = Scene();
        var fingerprint = OsmSetFile.Fingerprint(data, parameters.ToCanonicalJson());
        var withoutOnePiece = new OsmSetData
        {
            Frame = data.Frame,
            PlayZone = data.PlayZone,
            Masks = data.Masks.Skip(1).ToList(),
            Land = data.Land,
            Reachable = data.Reachable,
            Districts = data.Districts,
            Notes = data.Notes,
        };

        Assert.NotEqual(fingerprint, OsmSetFile.Fingerprint(withoutOnePiece, parameters.ToCanonicalJson()));
        Assert.NotEqual(fingerprint, OsmSetFile.Fingerprint(data, Params().ToCanonicalJson()));
    }

    [Fact]
    public void GeoJsonSeq_from_osmium_is_read_into_utm_on_the_grid()
    {
        // Как пишет osmium export -f geojsonseq -a type,id: разделитель RS, мультиполигон с дырой, точка, линия.
        var lines = new[]
        {
            GeoJsonLine("relation", 5, "natural=water;name=Озеро", "MultiPolygon",
                $"[[{LonLat((0, 0), (100, 0), (100, 100), (0, 100), (0, 0))},{LonLat((40, 40), (60, 40), (60, 60), (40, 60), (40, 40))}]]"),
            GeoJsonLine("way", 6, "highway=footway", "LineString", LonLat((0, 0), (0, 0), (50, 0))),
            GeoJsonLine("node", 7, "historic=memorial", "Point", LonLat((10, 10))[1..^1]),
            "",
        };

        var features = GeoJsonSeq.Read(lines).ToList();

        Assert.Equal(3, features.Count);
        var lake = features[0];
        Assert.Equal(("relation", 5L, "way/6"), (lake.Type, lake.Id, features[1].Ref));
        Assert.Equal("Озеро", lake.Tag("name"));
        Assert.True(lake.IsArea && lake.Geometry.IsValid && Gorodki.Domain.Geo.GeoOps.IsOnGrid(lake.Geometry));
        Assert.Equal(100 * 100 - 20 * 20, lake.Geometry.Area, 1);
        Assert.Equal(2, features[1].Geometry.NumPoints); // повтор точки убран
        Assert.Equal(50, features[1].Geometry.Length, 1);
        Assert.IsType<Point>(features[2].Geometry);
    }

    [Fact]
    public void Export_without_type_and_id_is_refused()
    {
        var line = "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[23.7,52.1]},\"properties\":{\"highway\":\"bus_stop\"}}";

        Assert.Throws<FormatException>(() => GeoJsonSeq.Read([line]).ToList());
    }

    [Theory]
    [InlineData("r123 v4 dV c5 t2026-01-01T00:00:00Z i6 uuser Ttype=multipolygon,natural=water Mw1@outer", 123, "multipolygon")]
    [InlineData("r9 v1 dV c1 t2026-01-01T00:00:00Z i1 u Tname=Мухавец,type=boundary Mw2@outer", 9, "boundary")]
    [InlineData("r10 v1 dV c1 t2026-01-01T00:00:00Z i1 u T Mn1@", 10, null)]
    public void Osmium_opl_relation_line_gives_id_and_type(string line, long id, string? type) =>
        Assert.Equal((id, type), InputLoader.ParseOplRelation(line));

    [Theory]
    [InlineData("natural=water", "wr/natural=water")]
    [InlineData("military=*", "wr/military")]
    public void Area_rules_become_osmium_filters(string rule, string filter) => Assert.Equal(filter, Extract.Filter(rule));
}
