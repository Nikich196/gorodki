using Gorodki.Domain.Geo;
using Gorodki.Domain.Runs;
using Gorodki.Domain.Territory;

namespace Gorodki.Domain.Tests.Runs;

/// <summary>Проверка заявки петли по вердиктам судьи и построение кольца (PLAN.md, §3.2).</summary>
public sealed class LoopRingTests
{
    private const long Start = 1_790_000_000_000;
    private static readonly LoopDetectorSettings Settings = new();

    [Fact]
    public void Walked_block_becomes_a_ring()
    {
        var points = Walk((0, 0), (100, 0), (100, 100), (0, 100), (0, 2));

        var ring = LoopRing.Build(points, Accepted(points), 0, points.Count - 1, closedByCrossing: false, Settings);

        Assert.True(ring.IsAccepted, ring.RejectCode);
        Assert.Equal(points.Count, ring.Ring.Count);
    }

    [Fact]
    public void Break_inside_the_loop_rejects_it_but_a_break_at_its_start_does_not()
    {
        var points = Walk((0, 0), (100, 0), (100, 100), (0, 100), (0, 2));
        var inside = Accepted(points);
        inside[40] = JudgeVerdict.Broken(TrackIssue.TooFast);
        var atStart = Accepted(points);
        atStart[0] = JudgeVerdict.Broken(TrackIssue.Teleport);

        Assert.Equal("segment_broken:tooFast", LoopRing.Build(points, inside, 0, points.Count - 1, false, Settings).RejectCode);
        Assert.True(LoopRing.Build(points, atStart, 0, points.Count - 1, false, Settings).IsAccepted);
    }

    [Fact]
    public void Points_the_judge_ignored_are_left_out()
    {
        var points = Walk((0, 0), (100, 0), (100, 100), (0, 100), (0, 2));
        var verdicts = Accepted(points);
        verdicts[10] = JudgeVerdict.Ignored(TrackIssue.PoorAccuracy);

        var ring = LoopRing.Build(points, verdicts, 0, points.Count - 1, false, Settings);

        Assert.Equal(points.Count - 1, ring.Ring.Count);
    }

    [Fact]
    public void Loop_shorter_than_150_metres_is_rejected()
    {
        var points = Walk((0, 0), (30, 0), (30, 30), (0, 30), (0, 1));

        Assert.Equal("too_short", LoopRing.Build(points, Accepted(points), 0, points.Count - 1, false, Settings).RejectCode);
    }

    [Fact]
    public void Ends_further_apart_than_R_are_not_closed()
    {
        // Точность 5 м: R = clamp(1,62·√(5² + 5²), 20, 50) = 20 м; концы в 60 м друг от друга.
        var points = Walk((0, 0), (100, 0), (100, 100), (0, 100), (0, 60));

        Assert.Equal("not_closed", LoopRing.Build(points, Accepted(points), 0, points.Count - 1, false, Settings).RejectCode);
    }

    [Fact]
    public void Crossing_closes_the_loop_through_the_crossing_point_even_when_ends_are_far_apart()
    {
        // Велосипед, точки редкие: первый отрезок идёт на восток по y = 0, последний пересекает его, двигаясь на юг по x = 20.
        // Концы в ~36 м друг от друга (больше R = 20 м), но петля замкнулась пересечением.
        List<(double X, double Y)> vertices = [(0, 0), (40, 0)];
        vertices.AddRange([(120, 0), (120, 120), (20, 120), (20, 15)]);
        vertices.Add((20, -15));
        var points = Points(vertices);

        var byCrossing = LoopRing.Build(points, Accepted(points), 0, points.Count - 1, closedByCrossing: true, Settings);
        var byDistance = LoopRing.Build(points, Accepted(points), 0, points.Count - 1, closedByCrossing: false, Settings);

        Assert.True(byCrossing.IsAccepted, byCrossing.RejectCode);
        // Точка пересечения — (20; 0) с точностью хранения координат (~1 см).
        Assert.Equal(20, byCrossing.Ring[0].X - Origin.X, tolerance: 0.05);
        Assert.Equal(0, byCrossing.Ring[0].Y - Origin.Y, tolerance: 0.05);
        Assert.Equal("not_closed", byDistance.RejectCode);
        var shape = CaptureShapeBuilder.Build(byCrossing.Ring, byCrossing.ClosingTolerance, null, new CaptureShapeSettings());
        Assert.True(shape.IsAccepted, shape.Rejection.ToString());
        Assert.InRange(shape.AreaSquareMeters, 100 * 120 * 0.95, 100 * 120 * 1.05);
    }

    // MARK: — вспомогательное

    /// <summary>Начало координат тестов — в Бресте, в UTM 34N.</summary>
    private static readonly (double X, double Y) Origin = (684_000, 5_775_000);

    private static JudgeVerdict[] Accepted(IReadOnlyList<TrackPoint> points) =>
        [.. Enumerable.Repeat(JudgeVerdict.Accepted, points.Count)];

    /// <summary>Обход по вершинам (метры от начала) с точкой каждые 3 м, раз в секунду.</summary>
    private static List<TrackPoint> Walk(params (double X, double Y)[] vertices)
    {
        var dense = new List<(double X, double Y)>();
        for (var i = 0; i + 1 < vertices.Length; i++)
        {
            var (x1, y1) = vertices[i];
            var (x2, y2) = vertices[i + 1];
            var steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1))) / 3));
            for (var s = 0; s < steps; s++)
            {
                dense.Add((x1 + ((x2 - x1) * s / steps), y1 + ((y2 - y1) * s / steps)));
            }
        }

        dense.Add(vertices[^1]);
        return Points(dense);
    }

    private static List<TrackPoint> Points(IReadOnlyList<(double X, double Y)> local) =>
        [.. local.Select((p, i) =>
        {
            var (latitude, longitude) = Utm34.Inverse(Origin.X + p.X, Origin.Y + p.Y);
            return TrackPoint.FromMeasurements(i, Start + (i * 1_000L), latitude, longitude, 5, null, PointFlags.None);
        })];
}
