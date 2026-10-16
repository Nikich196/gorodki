using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Runs;
using Gorodki.Domain.Segments;
using NetTopologySuite.Geometries;

namespace Gorodki.Domain.Tests.Segments;

/// <summary>
/// Засчитывание прохода отрезка (PLAN.md, §3.13): ворота старта 25 м, затем ворота финиша, коридор 30 м, время интерполяцией,
/// «бег» быстрее 30 км/ч — нет. Отрезок в тестах — 400 м на восток, если не сказано иное; путь — точки раз в секунду.
/// </summary>
public sealed class SegmentEffortDetectorTests
{
    private const double X0 = 684_000;
    private const double Y0 = 5_775_000;
    private const long T0 = 1_790_000_000_000;
    private static readonly SegmentRules Rules = SegmentRules.Default;
    private static readonly SegmentCourse Straight = Course((0, 0), (400, 0));

    // ── Ворота по порядку ───────────────────────────────────────────────────

    [Fact]
    public void Pass_through_the_start_gate_and_then_the_finish_gate_is_counted()
    {
        var path = Walk(3, 1, (-50, 0), (450, 0));

        var effort = Assert.Single(Detect(Straight, path));

        // Старт — в x = 0 (через 50/3 с), финиш — в x = 400: 400 м за 400/3 с.
        Assert.Equal(T0 + 16_667, effort.StartMs);
        Assert.Equal(133_333, effort.ElapsedMs);
    }

    [Fact]
    public void Time_is_interpolated_between_points_that_are_seconds_apart()
    {
        // Точки раз в 7 с (21 м): ни одна не попадает в концы линии — время старта и финиша берётся между ними.
        var path = Walk(3, 7, (-10, 5), (450, 5));

        var effort = Assert.Single(Detect(Straight, path));

        Assert.Equal(T0 + 3_333, effort.StartMs);
        Assert.Equal(133_333, effort.ElapsedMs, tolerance: 1); // моменты округляются до миллисекунды
    }

    [Fact]
    public void Reverse_direction_is_not_counted()
    {
        Assert.Empty(Detect(Straight, Walk(3, 1, (450, 0), (-50, 0))));
    }

    [Fact]
    public void Finish_gate_alone_or_start_gate_alone_is_not_a_pass()
    {
        Assert.Empty(Detect(Straight, Walk(3, 1, (-50, 0), (300, 0)))); // не дошёл до финиша
        Assert.Empty(Detect(Straight, Walk(3, 1, (100, 0), (450, 0)))); // начал с середины
    }

    [Fact]
    public void Path_beside_the_line_but_outside_the_gates_is_not_a_pass()
    {
        // Параллельно линии в 28 м: в коридоре, но мимо ворот (25 м).
        Assert.Empty(Detect(Straight, Walk(3, 1, (-50, 28), (450, 28))));
    }

    // ── Коридор ─────────────────────────────────────────────────────────────

    [Fact]
    public void Leaving_the_corridor_voids_the_pass()
    {
        var path = Walk(3, 1, (-50, 0), (200, 0), (200, 45), (220, 0), (450, 0));

        Assert.Empty(Detect(Straight, path));
    }

    [Fact]
    public void Swerving_inside_the_corridor_is_fine()
    {
        var path = Walk(3, 1, (-50, 0), (150, 0), (200, 25), (250, 0), (450, 0));

        Assert.Single(Detect(Straight, path));
    }

    [Fact]
    public void Cutting_across_a_bend_is_not_counted_even_inside_the_corridor()
    {
        // Отрезок-«подкова»: вверх 200 м, вправо 56 м, вниз 200 м. Срез поперёк на высоте 100 м весь в коридоре (≤ 28 м от
        // ветвей), но пропускает 256 м линии.
        var horseshoe = Course((0, 0), (0, 200), (56, 200), (56, 0));

        var cut = Walk(3, 1, (0, -40), (0, 100), (56, 100), (56, -40));
        var full = Walk(3, 1, (0, -40), (0, 200), (56, 200), (56, -40));

        Assert.Empty(Detect(horseshoe, cut));
        Assert.Single(Detect(horseshoe, full));
    }

    // ── Скорость лиги ───────────────────────────────────────────────────────

    [Fact]
    public void Run_faster_than_30_km_h_is_not_counted_but_the_same_ride_is()
    {
        var fast = Walk(9, 1, (-50, 0), (450, 0)); // 32,4 км/ч
        var brisk = Walk(8, 1, (-50, 0), (450, 0)); // 28,8 км/ч

        Assert.Empty(Detect(Straight, fast, League.Run));
        Assert.Single(Detect(Straight, fast, League.Bike));
        Assert.Single(Detect(Straight, brisk, League.Run));
    }

    // ── Несколько проходов и повтор старта ──────────────────────────────────

    [Fact]
    public void Several_passes_in_one_run_are_all_counted_in_order()
    {
        // Туда (засчитан), обратно (не засчитан — не по порядку), снова туда (засчитан).
        var path = Walk(3, 1, (-50, 0), (450, 0), (-50, 0), (450, 0));

        var efforts = Detect(Straight, path);

        Assert.Equal(2, efforts.Count);
        Assert.True(efforts[0].StartMs < efforts[1].StartMs);
        Assert.All(efforts, e => Assert.Equal(133_333, e.ElapsedMs, tolerance: 1));
    }

    [Fact]
    public void Coming_back_to_the_start_restarts_the_clock()
    {
        // Ушёл от старта на 100 м, вернулся в ворота и начал заново: старт — второй проход через начало линии.
        var path = Walk(3, 1, (-50, 0), (100, 0), (-20, 0), (450, 0));

        var effort = Assert.Single(Detect(Straight, path));

        Assert.Equal(133_333, effort.ElapsedMs, tolerance: 1);
    }

    // ── Разрывы ─────────────────────────────────────────────────────────────

    [Fact]
    public void Gap_between_points_breaks_the_pass_in_run_but_not_a_shorter_one_in_bike()
    {
        // Между x = 150 и x = 300 точек нет (GPS пропадал): 150 м — разрыв для бега (100 м), не для вело (200 м).
        var path = Walk(3, 1, (-50, 0), (150, 0)).Concat(Walk(3, 1, (300, 0), (450, 0), startMs: T0 + 120_000)).ToList();

        Assert.Empty(Detect(Straight, path, League.Run));
        Assert.Single(Detect(Straight, path, League.Bike));
    }

    [Fact]
    public void Break_by_the_league_judge_splits_the_path_and_voids_the_pass()
    {
        var points = Walk(3, 1, (-50, 0), (450, 0))
            .Select((p, i) =>
            {
                var (latitude, longitude) = Utm34.Inverse(p.X, p.Y);
                return TrackPoint.FromMeasurements(i, p.TimeMs, latitude, longitude, 5, 3, PointFlags.None);
            })
            .ToList();
        var clean = points.Select(_ => JudgeVerdict.Accepted).ToList();
        var broken = clean.Select((v, i) => i == 100 ? JudgeVerdict.Broken(TrackIssue.Vehicle) : v).ToList();
        var noisy = clean.Select((v, i) => i == 100 ? JudgeVerdict.Ignored(TrackIssue.PoorAccuracy) : v).ToList();

        Assert.Single(SegmentEffortDetector.Detect(Straight, SegmentEffortDetector.Stretches(points, clean), League.Run, Rules));
        Assert.Empty(SegmentEffortDetector.Detect(Straight, SegmentEffortDetector.Stretches(points, broken), League.Run, Rules));
        // Точка с плохой точностью не в счёт, но путь не рвёт.
        Assert.Single(SegmentEffortDetector.Detect(Straight, SegmentEffortDetector.Stretches(points, noisy), League.Run, Rules));
        Assert.Equal(2, SegmentEffortDetector.Stretches(points, broken).Count);
    }

    [Fact]
    public void Path_far_from_the_segment_gives_nothing()
    {
        Assert.Empty(Detect(Straight, Walk(3, 1, (-50, 500), (450, 500))));
    }

    private static List<SegmentEffort> Detect(SegmentCourse course, IReadOnlyList<PathPoint> path, League league = League.Run) =>
        [.. SegmentEffortDetector.Detect(course, [path], league, Rules)];

    private static SegmentCourse Course(params (double X, double Y)[] line) =>
        new([.. line.Select(p => new Coordinate(X0 + p.X, Y0 + p.Y))]);

    /// <summary>Путь по вершинам (метры от начала координат теста) со скоростью <paramref name="speed"/> м/с, точка раз в <paramref name="everySeconds"/> с.</summary>
    private static List<PathPoint> Walk(double speed, double everySeconds, params (double X, double Y)[] vertices) =>
        Walk(speed, everySeconds, vertices, T0);

    private static List<PathPoint> Walk(double speed, double everySeconds, (double X, double Y) from, (double X, double Y) to, long startMs) =>
        Walk(speed, everySeconds, [from, to], startMs);

    private static List<PathPoint> Walk(double speed, double everySeconds, (double X, double Y)[] vertices, long startMs)
    {
        var lengths = vertices.Zip(vertices.Skip(1), (a, b) => Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)))).ToList();
        var total = lengths.Sum();
        var points = new List<PathPoint>();
        for (var t = 0.0; t * speed <= total + 1e-9; t += everySeconds)
        {
            var s = t * speed;
            var leg = 0;
            while (leg < lengths.Count - 1 && s > lengths[leg])
            {
                s -= lengths[leg];
                leg++;
            }

            var f = lengths[leg] == 0 ? 0 : Math.Min(1, s / lengths[leg]);
            var (ax, ay) = vertices[leg];
            var (bx, by) = vertices[leg + 1];
            points.Add(new PathPoint(X0 + ax + ((bx - ax) * f), Y0 + ay + ((by - ay) * f), startMs + (long)Math.Round(t * 1000)));
        }

        return points;
    }
}
