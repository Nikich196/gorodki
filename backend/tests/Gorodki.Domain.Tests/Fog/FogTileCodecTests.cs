using Gorodki.Domain.Fog;

namespace Gorodki.Domain.Tests.Fog;

public sealed class FogTileCodecTests
{
    [Fact]
    public void Compressed_tile_reads_back_exactly_and_is_small()
    {
        var layer = new FogLayer();
        layer.RevealPath(52.0976, 23.688, 52.0986, 23.690, 25, 200);
        var bits = layer.Tiles.Values.First();

        var compressed = FogTileCodec.Compress(bits);
        var restored = FogTileCodec.Decompress(compressed);

        Assert.Equal(bits.Words, restored.Words);
        Assert.True(compressed.Length < 1_000, $"{compressed.Length} байт"); // почти пустой тайл — сотни байт, не 8 КБ
    }

    [Fact]
    public void Damaged_tile_is_refused()
    {
        var compressed = FogTileCodec.Compress(new FogTileBits());

        Assert.ThrowsAny<Exception>(() => FogTileCodec.Decompress(compressed[..^2]));
    }

    [Fact]
    public void Cell_in_Brest_is_about_34_square_metres()
    {
        var tile = FogGrid.Cell(52.0976, 23.688).Tile;

        Assert.InRange(FogTileCodec.CellAreaSquareMeters(tile), 34, 35); // сторона ≈ 5,87 м
    }
}
