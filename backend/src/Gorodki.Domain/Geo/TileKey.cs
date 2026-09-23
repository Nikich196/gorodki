using NetTopologySuite.Geometries;

namespace Gorodki.Domain.Geo;

/// <summary>
/// Тайл — квадрат UTM 1×1 км. Единый ключ для хранения участков, блокировок, кэша и групп реального времени
/// (PLAN.md, §7.3). Каждый участок хранится кусками: один кусок — внутри одного тайла.
/// </summary>
/// <param name="X">Номер километра по оси «восток» (⌊E / 1000⌋).</param>
/// <param name="Y">Номер километра по оси «север» (⌊N / 1000⌋).</param>
public readonly record struct TileKey(int X, int Y) : IComparable<TileKey>
{
    public const double SizeMeters = 1000.0;

    public static TileKey Of(double easting, double northing) =>
        new((int)Math.Floor(easting / SizeMeters), (int)Math.Floor(northing / SizeMeters));

    /// <summary>
    /// Число для блокировки в PostgreSQL (<c>pg_advisory_xact_lock</c>).
    /// В Бресте X ≈ 680–700, Y ≈ 5770–5780, так что число помещается в int.
    /// </summary>
    public int LockKey => X * 10_000 + Y;

    /// <summary>Квадрат тайла. Углы — целые километры, поэтому точно лежат на сетке 0,1 м.</summary>
    public Polygon ToPolygon()
    {
        var minX = X * SizeMeters;
        var minY = Y * SizeMeters;
        var maxX = minX + SizeMeters;
        var maxY = minY + SizeMeters;
        return GeoOps.Factory.CreatePolygon(
        [
            new Coordinate(minX, minY),
            new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY),
            new Coordinate(minX, maxY),
            new Coordinate(minX, minY),
        ]);
    }

    /// <summary>
    /// Тайлы, которые задевает прямоугольник, в порядке возрастания.
    /// Один и тот же порядок везде — так две транзакции не заблокируют друг друга крест-накрест.
    /// </summary>
    public static IReadOnlyList<TileKey> Covering(Envelope envelope)
    {
        var first = Of(envelope.MinX, envelope.MinY);
        var last = Of(envelope.MaxX, envelope.MaxY);
        var tiles = new List<TileKey>();
        for (var x = first.X; x <= last.X; x++)
        {
            for (var y = first.Y; y <= last.Y; y++)
            {
                tiles.Add(new TileKey(x, y));
            }
        }

        return tiles;
    }

    public int CompareTo(TileKey other) => X != other.X ? X.CompareTo(other.X) : Y.CompareTo(other.Y);

    public override string ToString() => $"{X}:{Y}";
}
