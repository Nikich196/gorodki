using System.Globalization;
using System.Text.Json;
using Gorodki.Domain.Fog;

namespace Gorodki.Domain.Tests.Fog;

/// <summary>
/// Растеризатор тумана на сервере против эталонов <c>contracts/fog.v1.json</c>, которые записала реализация на Swift
/// (<c>FogContractTests</c> в GameCore): каждое 64-битное слово каждого тайла должно совпасть.
/// </summary>
public sealed class FogContractTests
{
    private static readonly Contract Vectors = JsonSerializer.Deserialize<Contract>(
        File.ReadAllText(Path.Combine(RepositoryRoot(), "contracts", "fog.v1.json")),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    public static TheoryData<string> Scenarios => [.. Vectors.Scenarios.Select(s => s.Name)];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Server_reveals_exactly_the_cells_the_phone_reveals(string name)
    {
        var scenario = Vectors.Scenarios.Single(s => s.Name == name);
        var layer = new FogLayer();
        foreach (var operation in scenario.Operations)
        {
            if (operation.Around is [var latitude, var longitude])
            {
                layer.RevealAround(latitude, longitude, operation.Radius);
            }
            else if (operation.Path is [var latitudeA, var longitudeA, var latitudeB, var longitudeB])
            {
                layer.RevealPath(latitudeA, longitudeA, latitudeB, longitudeB, operation.Radius, operation.MaxGap ?? 100);
            }
        }

        Assert.Equal(scenario.Expected.CellCount, layer.CellCount);
        Assert.Equal(
            scenario.Expected.Tiles.Select(t => (t.X, t.Y)),
            layer.Tiles.Keys.Select(k => (k.X, k.Y)));
        foreach (var tile in scenario.Expected.Tiles)
        {
            var bits = layer.Tiles[new FogTileKey(tile.X, tile.Y)];
            var actual = bits.Words
                .Select((word, index) => (word, index))
                .Where(w => w.word != 0)
                .Select(w => new[] { w.index.ToString(CultureInfo.InvariantCulture), w.word.ToString("x", CultureInfo.InvariantCulture) });
            Assert.Equal(tile.Count, bits.Count);
            Assert.Equal(tile.Words.Select(w => string.Join(':', w)), actual.Select(w => string.Join(':', w)));
        }
    }

    [Fact]
    public void Tile_bits_survive_storage()
    {
        var layer = new FogLayer();
        layer.RevealAround(52.0976, 23.688, 25);
        var bits = layer.Tiles.Values.Single();

        var restored = FogTileBits.FromBytes(bits.ToBytes());

        Assert.Equal(bits.Words, restored.Words);
        Assert.Equal(0, restored.NewCount(bits));
        Assert.Equal(bits.Count, bits.NewCount(new FogTileBits()));
    }

    private sealed record Contract(int Version, List<Scenario> Scenarios);

    private sealed record Scenario(string Name, List<Operation> Operations, Expected Expected);

    private sealed record Operation(double[]? Around, double[]? Path, double Radius, double? MaxGap);

    private sealed record Expected(int CellCount, List<Tile> Tiles);

    private sealed record Tile(int X, int Y, int Count, List<string[]> Words);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "backend")) && Directory.Exists(Path.Combine(directory.FullName, "docs")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Не найден корень репозитория (папки backend и docs).");
    }
}
