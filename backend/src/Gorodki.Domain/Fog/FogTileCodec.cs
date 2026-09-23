using System.IO.Compression;

namespace Gorodki.Domain.Fog;

/// <summary>Тайл тумана в хранилище: 8 КБ бит, сжатые Deflate (PLAN.md, §7.3). Почти пустой тайл занимает десятки байт.</summary>
public static class FogTileCodec
{
    public static byte[] Compress(FogTileBits bits)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(bits.ToBytes());
        }

        return output.ToArray();
    }

    /// <exception cref="FormatException">Данные повреждены: после распаковки не ровно 8 КБ.</exception>
    public static FogTileBits Decompress(byte[] compressed)
    {
        using var input = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress);
        var buffer = new byte[FogTileBits.ByteCount + 1];
        var read = 0;
        int chunk;
        while (read < buffer.Length && (chunk = input.Read(buffer, read, buffer.Length - read)) > 0)
        {
            read += chunk;
        }

        return read == FogTileBits.ByteCount
            ? FogTileBits.FromBytes(buffer.AsSpan(0, read))
            : throw new FormatException("Тайл тумана повреждён.");
    }

    /// <summary>Площадь одной клетки тайла, м²: сторона клетки на широте центра тайла.</summary>
    public static double CellAreaSquareMeters(FogTileKey tile)
    {
        var (latitude, _) = FogGrid.Center(new FogCell((tile.X << 8) + 128, (tile.Y << 8) + 128));
        var size = FogGrid.CellSizeMeters(latitude);
        return size * size;
    }
}
