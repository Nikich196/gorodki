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
}
