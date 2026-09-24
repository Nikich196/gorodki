using System.Text.RegularExpressions;
using Gorodki.Domain.Geo;
using NetTopologySuite.Geometries;

namespace Gorodki.Domain.Tests.Geo;

public sealed class GeoOpsTests
{
    [Fact]
    public void Overlay_results_lie_on_the_grid_and_are_valid()
    {
        // Входные вершины намеренно не на сетке.
        var a = GeoOps.Factory.CreatePolygon(
        [
            new Coordinate(0.013, 0.027), new Coordinate(100.049, 0.011), new Coordinate(100.031, 100.044),
            new Coordinate(0.022, 100.038), new Coordinate(0.013, 0.027),
        ]);
        var b = GeoOps.Factory.CreatePolygon(
        [
            new Coordinate(50.017, 50.033), new Coordinate(150.041, 50.019), new Coordinate(150.028, 150.046),
            new Coordinate(50.012, 150.021), new Coordinate(50.017, 50.033),
        ]);

        var intersection = GeoOps.Intersection(a, b);
        var union = GeoOps.Union(a, b);

        Assert.True(GeoOps.IsOnGrid(intersection));
        Assert.True(GeoOps.IsOnGrid(union));
        Assert.True(intersection.IsValid);
        Assert.True(union.IsValid);
        Assert.InRange(intersection.Area, 2490, 2510);
    }

    [Fact]
    public void Bow_tie_is_split_into_two_faces()
    {
        var bowTie = GeoOps.Factory.CreateLineString(
        [
            new Coordinate(0, 0), new Coordinate(100, 100), new Coordinate(100, 0), new Coordinate(0, 100), new Coordinate(0, 0),
        ]);

        var faces = GeoOps.Polygonize(GeoOps.Node([bowTie]));

        Assert.Equal(2, faces.Count);
        Assert.All(faces, face => Assert.Equal(2500, face.Area, 6));
    }

    [Fact]
    public void Interior_point_stays_inside_a_thin_wedge()
    {
        // Клин ~5 см шириной там, где ищется внутренняя точка (найден property-тестом, seed 6DWzKFVPPhQo).
        var wedge = GeoOps.Factory.CreatePolygon(
        [
            new Coordinate(683993, 5774584.8), new Coordinate(683984.1, 5774574.9), new Coordinate(684000, 5774592.8),
            new Coordinate(684000, 5774587.8), new Coordinate(683993, 5774584.8),
        ]);

        // Встроенная точка NTS округляется до сетки 0,1 м и вылетает из клина — поэтому она под запретом.
        Assert.False(wedge.Contains(wedge.InteriorPoint));
        Assert.True(wedge.Contains(GeoOps.Factory.CreatePoint(GeoOps.InteriorPoint(wedge))));
    }

    [Fact]
    public void Narrowness_uses_half_width()
    {
        var strip = TestGeometry.RectanglePolygon(0, 0, 100, 10); // ширина 10 м → полуширина 5 м

        Assert.False(GeoOps.IsNarrowerThan(strip, 4));
        Assert.True(GeoOps.IsNarrowerThan(strip, 6));
    }

    [Fact]
    public void Shared_boundary_of_neighbours_is_their_common_edge()
    {
        var left = TestGeometry.RectanglePolygon(0, 0, 50, 30);
        var right = TestGeometry.RectanglePolygon(50, 0, 50, 30);

        Assert.Equal(30, GeoOps.SharedBoundaryLength(left, right), 6);
    }

    [Fact]
    public void Collinear_vertices_are_dropped_and_the_shape_is_kept()
    {
        // Лишние вершины на прямых: на стыке кольца (первая точка), на стороне, на наклонной стороне и в дыре. Излом
        // (60; 0) — угол на сантиметры, но угол: он остаётся. В итоге — только углы, в каноническом порядке обхода.
        var polygon = GeoOps.Factory.CreatePolygon(
            GeoOps.Factory.CreateLinearRing(
            [
                new Coordinate(0, 50), new Coordinate(0, 0), new Coordinate(30, 0), new Coordinate(60, 0), new Coordinate(100, 0.1),
                new Coordinate(100, 100), new Coordinate(50, 150), new Coordinate(20, 180), new Coordinate(0, 100), new Coordinate(0, 50),
            ]),
            [GeoOps.Factory.CreateLinearRing(
            [
                new Coordinate(10, 10), new Coordinate(10, 20), new Coordinate(20, 20), new Coordinate(20, 15), new Coordinate(20, 10),
                new Coordinate(10, 10),
            ])]);

        var result = GeoOps.WithoutCollinearVertices(polygon);

        Assert.True(result.IsValid);
        Assert.True(result.EqualsTopologically(polygon));
        Assert.Equal(7, result.Shell.NumPoints); // (0;0), (60;0), (100;0,1), (100;100), (20;180), (0;100) и замыкающая
        Assert.Equal(5, result.Holes[0].NumPoints);
        Assert.True(result.EqualsExact(result.Normalized()));
    }

    [Fact]
    public void Vertices_off_the_grid_are_never_dropped()
    {
        // Без сетки «на прямой» точно не проверить — такую вершину не трогаем.
        var polygon = GeoOps.Factory.CreatePolygon(
        [
            new Coordinate(0, 0), new Coordinate(33.333, 0), new Coordinate(100, 0), new Coordinate(100, 100), new Coordinate(0, 0),
        ]);

        Assert.Equal(5, GeoOps.WithoutCollinearVertices(polygon).Shell.NumPoints);
    }

    [Fact]
    public void Tiles_are_ordered_and_cover_the_envelope()
    {
        var envelope = new Envelope(TestGeometry.OriginX - 10, TestGeometry.OriginX + 10, TestGeometry.OriginY - 10, TestGeometry.OriginY + 10);

        var tiles = TileKey.Covering(envelope);

        Assert.Equal([new TileKey(683, 5774), new TileKey(683, 5775), new TileKey(684, 5774), new TileKey(684, 5775)], tiles);
        Assert.Equal(tiles.Order().ToList(), tiles);
    }
}

/// <summary>
/// Архитектурный тест: геометрические операции над участками — только через <see cref="GeoOps"/>.
/// Обычные Geometry.Intersection/Difference/Union в NTS идут старым алгоритмом без фиксированной сетки
/// и могут выдать неправильную геометрию или TopologyException (PLAN.md, §7.3).
/// </summary>
public sealed class GeoOpsArchitectureTests
{
    // Вызовы вида GeoOps.Difference(...) разрешены — запрещены только методы самой геометрии.
    // .InteriorPoint запрещён: NTS округляет точку до сетки, и у тонкого клина она выходит наружу.
    private static readonly Regex ForbiddenCall = new(
        @"(?<!GeoOps)\.(Intersection|Difference|Union|SymmetricDifference)\(|(?<!GeoOps)\.InteriorPoint\b",
        RegexOptions.Compiled);

    [Theory]
    [InlineData("var x = parcel.Geometry.Difference(capture);", true)]
    [InlineData("var x = a.Union(b);", true)]
    [InlineData("var x = GeoOps.Difference(a, b);", false)]
    [InlineData("var p = face.InteriorPoint.Coordinate;", true)]
    [InlineData("var p = GeoOps.InteriorPoint(face);", false)]
    [InlineData("var ok = a.EnvelopeInternal.Intersects(b.EnvelopeInternal);", false)]
    public void Rule_catches_direct_calls_and_allows_the_facade(string line, bool forbidden)
    {
        Assert.Equal(forbidden, ForbiddenCall.IsMatch(line));
    }

    [Fact]
    public void No_direct_overlay_calls_outside_GeoOps()
    {
        var sourceRoot = Path.Combine(FindBackendRoot(), "src");
        var offenders = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => Path.GetFileName(path) != "GeoOps.cs")
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (path, line, index))
                .Where(x => ForbiddenCall.IsMatch(x.line) && !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal)))
            .Select(x => $"{Path.GetRelativePath(sourceRoot, x.path)}:{x.index + 1}: {x.line.Trim()}")
            .ToList();

        Assert.Empty(offenders);
    }

    private static string FindBackendRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Gorodki.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Не найден backend/Gorodki.slnx");
    }
}
