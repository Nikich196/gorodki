using Gorodki.Domain.Config;
using Gorodki.Domain.Fog;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Runs;

namespace Gorodki.Domain.Tests.Fog;

/// <summary>Какие клетки открывает забег: «из машины туман не открывается» (PLAN.md, §3.10).</summary>
public sealed class FogStampTests
{
    private const long Start = 1_790_000_000_000;
    private static readonly ExplorationConfig Rules = new();

    [Fact]
    public void Accepted_walk_opens_a_continuous_strip()
    {
        var points = Line(count: 200);

        var layer = FogStamp.Of(points, Accepted(points.Length), League.Run, Rules);

        Assert.True(layer.IsRevealed(CellAt(points[0])));
        Assert.True(layer.IsRevealed(CellAt(points[199])));
        Assert.True(layer.IsRevealed(CellAt(points[100])));
    }

    [Fact]
    public void Vehicle_break_hides_the_last_30_seconds_and_the_break_itself()
    {
        // Машина: точка в секунду через 14 м (~50 км/ч); на 150-й секунде судья порвал след — «транспорт».
        // Пешком 30 с — это 42 м, их закрывают соседние круги по 25 м; правило нужно именно против машины.
        var points = Line(count: 200, spacing: 14);
        var verdicts = Accepted(points.Length);
        verdicts[150] = JudgeVerdict.Broken(TrackIssue.Vehicle);

        var layer = FogStamp.Of(points, verdicts, League.Run, Rules);

        Assert.True(layer.IsRevealed(CellAt(points[60])));
        Assert.False(layer.IsRevealed(CellAt(points[135]))); // последние 30 с перед разрывом — 420 м пути не открыты
        Assert.True(layer.IsRevealed(CellAt(points[199]))); // после разрыва — снова честное движение
    }

    [Fact]
    public void Poor_accuracy_point_is_skipped_but_the_strip_goes_on()
    {
        var points = Line(count: 100);
        var withIgnored = Accepted(points.Length);
        withIgnored[50] = JudgeVerdict.Ignored(TrackIssue.PoorAccuracy);

        var full = FogStamp.Of(points, Accepted(points.Length), League.Run, Rules);
        var skipped = FogStamp.Of(points, withIgnored, League.Run, Rules);

        Assert.Equal(full.CellCount, skipped.CellCount);
    }

    [Fact]
    public void Path_before_a_teleport_is_kept()
    {
        var points = Line(count: 100);
        var verdicts = Accepted(points.Length);
        verdicts[99] = JudgeVerdict.Broken(TrackIssue.Teleport);

        var layer = FogStamp.Of(points, verdicts, League.Run, Rules);

        Assert.True(layer.IsRevealed(CellAt(points[97])));
    }

    [Fact]
    public void Gap_longer_than_the_league_threshold_is_not_painted()
    {
        // Две точки в 150 м: пешком (порог 100 м) — только круги, на велосипеде (порог 200 м) — полоса.
        var points = new[] { Point(0, 0, 0), Point(1, 150, 0) };

        var onFoot = FogStamp.Of(points, Accepted(2), League.Run, Rules);
        var onBike = FogStamp.Of(points, Accepted(2), League.Bike, Rules);

        Assert.False(onFoot.IsRevealed(CellAt(Point(2, 75, 0))));
        Assert.True(onBike.IsRevealed(CellAt(Point(2, 75, 0))));
    }

    private static readonly (double X, double Y) Origin = (684_500, 5_775_500);

    private static JudgeVerdict[] Accepted(int count) => [.. Enumerable.Repeat(JudgeVerdict.Accepted, count)];

    /// <summary>Прямая на восток: точка в секунду через <paramref name="spacing"/> метров.</summary>
    private static TrackPoint[] Line(int count, double spacing = 1.4) =>
        [.. Enumerable.Range(0, count).Select(i => Point(i, spacing * i, 0))];

    private static TrackPoint Point(int seq, double east, double north)
    {
        var (latitude, longitude) = Utm34.Inverse(Origin.X + east, Origin.Y + north);
        return TrackPoint.FromMeasurements(seq, Start + (seq * 1_000L), latitude, longitude, 5, 1.4, PointFlags.None);
    }

    private static FogCell CellAt(TrackPoint point) => FogGrid.Cell(point.Latitude, point.Longitude);
}
