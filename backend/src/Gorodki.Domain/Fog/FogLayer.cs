using System.Buffers.Binary;
using System.Numerics;
using Gorodki.Domain.Geo;

namespace Gorodki.Domain.Fog;

/// <summary>
/// Битовая карта тумана одного тайла: 256 × 256 клеток, бит 1 — клетка открыта. На сервере хранится в <c>fog_tiles.bits</c>
/// (PLAN.md, §7.3): 8 КБ до сжатия.
/// </summary>
public sealed class FogTileBits
{
    public const int WordCount = 256 * 256 / 64;

    public const int ByteCount = WordCount * 8;

    private readonly ulong[] _words = new ulong[WordCount];

    public IReadOnlyList<ulong> Words => _words;

    public bool IsSet(int bit) => (_words[bit >> 6] & (1UL << (bit & 63))) != 0;

    public void Set(int bit) => _words[bit >> 6] |= 1UL << (bit & 63);

    /// <summary>Сколько клеток открыто.</summary>
    public int Count => _words.Sum(BitOperations.PopCount);

    /// <summary>Сколько клеток открыто здесь и не было в <paramref name="old"/>: popcount(new &amp; ~old).</summary>
    public int NewCount(FogTileBits old)
    {
        var total = 0;
        for (var i = 0; i < WordCount; i++)
        {
            total += BitOperations.PopCount(_words[i] & ~old._words[i]);
        }

        return total;
    }

    /// <summary>Сколько клеток открыто и здесь, и в <paramref name="other"/>: popcount(this &amp; other).</summary>
    public int CountAnd(FogTileBits other)
    {
        var total = 0;
        for (var i = 0; i < WordCount; i++)
        {
            total += BitOperations.PopCount(_words[i] & other._words[i]);
        }

        return total;
    }

    public void UnionWith(FogTileBits other)
    {
        for (var i = 0; i < WordCount; i++)
        {
            _words[i] |= other._words[i];
        }
    }

    /// <summary>8 192 байта, слова little-endian — так тайл хранится (до сжатия).</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[ByteCount];
        for (var i = 0; i < WordCount; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * 8), _words[i]);
        }

        return bytes;
    }

    public static FogTileBits FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteCount)
        {
            throw new FormatException($"Тайл тумана — ровно {ByteCount} байт.");
        }

        var bits = new FogTileBits();
        for (var i = 0; i < WordCount; i++)
        {
            bits._words[i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(i * 8)..]);
        }

        return bits;
    }
}

/// <summary>
/// Слой «Исследования» одного игрока (PLAN.md, §3.10) — перенос <c>FogLayer</c> из GameCore строка в строку. Валидное движение
/// открывает круг вокруг каждой точки пути, между точками — сплошную полосу, если разрыв не больше порога лиги.
/// </summary>
public sealed class FogLayer
{
    private readonly SortedDictionary<FogTileKey, FogTileBits> _tiles = new();

    public IReadOnlyDictionary<FogTileKey, FogTileBits> Tiles => _tiles;

    public int CellCount => _tiles.Values.Sum(t => t.Count);

    /// <summary>Открывает круг радиусом <paramref name="radius"/> метров вокруг точки: клетка открыта, если её центр в круге.</summary>
    public void RevealAround(double latitude, double longitude, double radius)
    {
        var size = FogGrid.CellSizeMeters(latitude);
        var (px, py) = FogGrid.Pixel(latitude, longitude);
        var reach = (int)Math.Ceiling(radius / size) + 1;
        var cx = (int)Math.Floor(px);
        var cy = (int)Math.Floor(py);
        for (var dy = -reach; dy <= reach; dy++)
        {
            for (var dx = -reach; dx <= reach; dx++)
            {
                var x = cx + dx;
                var y = cy + dy;
                var ex = (x + 0.5 - px) * size;
                var ey = (y + 0.5 - py) * size;
                if ((ex * ex) + (ey * ey) <= radius * radius)
                {
                    Set(new FogCell(x, y));
                }
            }
        }
    }

    /// <summary>Открывает полосу вдоль пути между двумя точками (или только концы, если разрыв длиннее <paramref name="maxGap"/>).</summary>
    public void RevealPath(double latitudeA, double longitudeA, double latitudeB, double longitudeB, double radius, double maxGap)
    {
        var length = Geodesy.Distance(latitudeA, longitudeA, latitudeB, longitudeB);
        if (length > maxGap)
        {
            RevealAround(latitudeA, longitudeA, radius);
            RevealAround(latitudeB, longitudeB, radius);
            return;
        }

        // Шаг — половина клетки, чтобы круги сливались в сплошную полосу без «зубцов».
        var step = FogGrid.CellSizeMeters(latitudeA) / 2;
        var count = Math.Max(1, (int)Math.Ceiling(length / step));
        for (var i = 0; i <= count; i++)
        {
            var t = (double)i / count;
            RevealAround(latitudeA + ((latitudeB - latitudeA) * t), longitudeA + ((longitudeB - longitudeA) * t), radius);
        }
    }

    public bool IsRevealed(FogCell cell) => _tiles.TryGetValue(cell.Tile, out var bits) && bits.IsSet(cell.BitIndex);

    public void UnionWith(FogLayer other)
    {
        foreach (var (key, bits) in other._tiles)
        {
            TileAt(key).UnionWith(bits);
        }
    }

    /// <summary>Тайл для записи (создаётся пустым) — например, чтобы загрузить сохранённые биты.</summary>
    public FogTileBits TileAt(FogTileKey key)
    {
        if (!_tiles.TryGetValue(key, out var bits))
        {
            bits = new FogTileBits();
            _tiles[key] = bits;
        }

        return bits;
    }

    private void Set(FogCell cell) => TileAt(cell.Tile).Set(cell.BitIndex);
}
