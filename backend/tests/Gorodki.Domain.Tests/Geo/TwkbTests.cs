using Gorodki.Domain.Geo;
using NetTopologySuite.Geometries;

namespace Gorodki.Domain.Tests.Geo;

/// <summary>TWKB журнала захватов: без потерь на сетке 0,1 м и в несколько раз меньше WKB.</summary>
public sealed class TwkbTests
{
    // Координаты как в Бресте (UTM 34N): большие числа проверяют переменную длину и зигзаг.
    private static Polygon SquareWithHole(double x, double y) =>
        GeoOps.Factory.CreatePolygon(
            Ring((x, y), (x + 100.3, y), (x + 100.3, y + 80.7), (x, y + 80.7)),
            [Ring((x + 10.1, y + 10.2), (x + 10.1, y + 20.2), (x + 30.4, y + 20.2), (x + 30.4, y + 10.2))]);

    /// <summary>Кольцо на сетке: вершины — канонические числа сетки, как у движка (сумма 684 123,4 + 100,3 в double — не они).</summary>
    private static LinearRing Ring(params (double X, double Y)[] points)
    {
        var coordinates = points.Select(p => GeoOps.Snap(new Coordinate(p.X, p.Y))).ToList();
        return GeoOps.Factory.CreateLinearRing([.. coordinates, coordinates[0].Copy()]);
    }

    [Fact]
    public void Polygon_with_a_hole_round_trips_exactly()
    {
        var polygon = SquareWithHole(684_123.4, 5_775_987.6);

        var restored = Twkb.Read(Twkb.Write(polygon));

        Assert.True(restored.EqualsExact(polygon));
        Assert.Equal(Utm34.Srid, restored.SRID);
    }

    [Fact]
    public void Multipolygon_round_trips_exactly()
    {
        var multi = GeoOps.Factory.CreateMultiPolygon([SquareWithHole(684_000, 5_775_000), SquareWithHole(684_500.5, 5_775_300.1)]);

        var restored = Twkb.Read(Twkb.Write(multi));

        Assert.IsType<MultiPolygon>(restored);
        Assert.True(restored.EqualsExact(multi));
    }

    [Fact]
    public void Empty_geometries_round_trip()
    {
        Assert.True(Twkb.Read(Twkb.Write(GeoOps.EmptyPolygon())).IsEmpty);
        Assert.IsType<MultiPolygon>(Twkb.Read(Twkb.Write(GeoOps.Factory.CreateMultiPolygon())));
    }

    [Fact]
    public void Result_of_engine_operations_round_trips_exactly()
    {
        // Вершины из пересечений (snap-rounding на сетке) — самые «неровные» числа, какие пишет движок.
        var a = GeoOps.Factory.CreatePolygon(Ring((684_000, 5_775_000), (684_333.3, 5_775_010.1), (684_100.7, 5_775_290.9)));
        var b = SquareWithHole(684_050.5, 5_775_050.5);
        var union = GeoOps.Union(a, b);
        Assert.True(GeoOps.IsOnGrid(union));

        Assert.True(Twkb.Read(Twkb.Write(union)).EqualsExact(union));
    }

    [Fact]
    public void Is_several_times_smaller_than_wkb()
    {
        var polygon = SquareWithHole(684_123.4, 5_775_987.6);

        var twkb = Twkb.Write(polygon).Length;
        var wkb = polygon.ToBinary().Length;

        Assert.True(twkb * 3 < wkb, $"TWKB {twkb} байт, WKB {wkb} байт");
    }

    [Fact]
    public void Header_says_polygon_with_precision_one()
    {
        var bytes = Twkb.Write(SquareWithHole(0, 0));

        Assert.Equal(0x23, bytes[0]); // тип 3 (многоугольник), точность 1 в зигзаге (2) — в старших битах
        Assert.Equal(0x00, bytes[1]); // без рамки, размера и списка id
    }

    [Fact]
    public void Off_grid_vertices_are_refused()
    {
        var offGrid = GeoOps.Factory.CreatePolygon(
        [
            new Coordinate(0, 0), new Coordinate(10.05, 0), new Coordinate(10.05, 10), new Coordinate(0, 10), new Coordinate(0, 0),
        ]);

        Assert.Throws<ArgumentException>(() => Twkb.Write(offGrid));
    }

    [Fact]
    public void Truncated_data_is_refused()
    {
        var bytes = Twkb.Write(SquareWithHole(684_000, 5_775_000));

        Assert.Throws<FormatException>(() => Twkb.Read(bytes[..^3]));
    }

    /// <summary>
    /// Испорченные записи журнала, которые разбираются как TWKB, но многоугольника из них не собрать: NTS бросает
    /// ArgumentException, а публичная проекция ловила только FormatException — и отвечала 500 каждому, кто смотрит тайл.
    /// </summary>
    public static TheoryData<string, byte[]> Unbuildable => new()
    {
        // Кольцо из одной точки: оно уже «замкнуто», а NTS нужно хотя бы три.
        { "one-point ring", Encode(PolygonHeader, 1, 1, Z(100), Z(100)) },
        // Две одинаковые точки: тоже замкнуто, и тоже мало.
        { "ring of two equal points", Encode(PolygonHeader, 1, 2, Z(100), Z(100), Z(0), Z(0)) },
        // Дыра без оболочки: оболочка пустая, дыра — квадрат.
        { "hole without a shell", Encode(PolygonHeader, 2, 0, 5, Z(0), Z(0), Z(10), Z(0), Z(0), Z(10), Z(-10), Z(0), Z(0), Z(-10)) },
    };

    [Theory]
    [MemberData(nameof(Unbuildable))]
    public void Readable_bytes_that_make_no_polygon_are_refused_as_damaged_data(string damage, byte[] data)
    {
        var error = Assert.Throws<FormatException>(() => Twkb.Read(data));

        Assert.True(error.InnerException is ArgumentException, $"{damage}: причина от NTS остаётся внутри — для журнала сервера");
    }

    [Theory]
    [InlineData(10_000_000UL)] // вершин в кольце: список на 10 млн вершин — 80 МБ ещё до первой вершины
    [InlineData(1UL << 40)] // больше int: раньше OverflowException вместо FormatException
    public void Counts_beyond_the_data_are_refused_before_anything_is_allocated(ulong count)
    {
        var ring = Encode(PolygonHeader, 1, count, Z(0), Z(0));
        var rings = Encode(PolygonHeader, count, 4, Z(0), Z(0), Z(10), Z(0), Z(0), Z(10), Z(-10), Z(-10)); // оболочка цела
        var polygons = Encode(MultiPolygonHeader, count, 1);
        var before = GC.GetAllocatedBytesForCurrentThread();

        Assert.Throws<FormatException>(() => Twkb.Read(ring));
        Assert.Throws<FormatException>(() => Twkb.Read(rings));
        Assert.Throws<FormatException>(() => Twkb.Read(polygons));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1_000_000, "испорченное число вершин не должно выделять память");
    }

    private const byte PolygonHeader = 0x23; // тип 3, точность 1 в зигзаге — в старших битах

    private const byte MultiPolygonHeader = 0x26; // тип 6

    /// <summary>TWKB без рамки и размера из готовых чисел переменной длины: число колец, вершин, разности координат.</summary>
    private static byte[] Encode(byte header, params ulong[] values)
    {
        var bytes = new List<byte> { header, 0x00 };
        foreach (var value in values)
        {
            var rest = value;
            while (rest >= 0x80)
            {
                bytes.Add((byte)(rest | 0x80));
                rest >>= 7;
            }

            bytes.Add((byte)rest);
        }

        return [.. bytes];
    }

    /// <summary>Разность координат в зигзаг-кодировке (в единицах 0,1 м).</summary>
    private static ulong Z(long delta) => (ulong)((delta << 1) ^ (delta >> 63));
}
