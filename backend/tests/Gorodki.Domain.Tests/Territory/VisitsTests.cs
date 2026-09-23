using Gorodki.Domain.Geo;
using Gorodki.Domain.Runs;
using Gorodki.Domain.Territory;
using static Gorodki.Domain.Tests.Geo.TestGeometry;

namespace Gorodki.Domain.Tests.Territory;

/// <summary>
/// Визиты (PLAN.md, §3.3): «визит — ≥50 м следа внутри участка; +1 уровень не чаще раза в 20 ч; не считаются первые и
/// последние 200 м забега». Путь, который засчитывается, сколько его прошло по каждому куску и что визит делает с куском.
/// </summary>
public sealed class VisitsTests
{
    private const long Start = 1_790_000_000_000;
    private static readonly Guid Owner = new("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly TerritoryRules Rules = new();

    // ── Путь без первых и последних 200 м ───────────────────────────────────

    [Fact]
    public void Path_loses_its_first_and_last_200_meters()
    {
        var path = Visits.TrimmedPath(Pairs(Line(71, spacing: 10)), 200); // 700 м на восток

        Assert.Equal(300, path.Sum(s => s.From.Distance(s.To)), 0.01);
        Assert.Equal(OriginX + 200, path[0].From.X, 0.01);
        Assert.Equal(OriginX + 500, path[^1].To.X, 0.01);
    }

    [Fact]
    public void Short_run_has_no_visits_at_all()
    {
        Assert.Empty(Visits.TrimmedPath(Pairs(Line(40, spacing: 10)), 200)); // 390 м — весь забег «у дома»
    }

    [Fact]
    public void Trim_cuts_inside_a_segment_and_keeps_its_time()
    {
        var points = new[] { Point(0, 0), Point(1, 300), Point(2, 600) }; // два участка по 300 м

        var path = Visits.TrimmedPath(Pairs(points), 200);

        Assert.Equal(2, path.Count);
        Assert.Equal(OriginX + 200, path[0].From.X, 0.01);
        Assert.Equal(OriginX + 300, path[0].To.X, 0.01);
        Assert.Equal(OriginX + 400, path[1].To.X, 0.01);
        Assert.Equal((points[1].TimeMs, points[2].TimeMs), (path[0].TimeMs, path[1].TimeMs));
    }

    // ── Сколько пути внутри куска ───────────────────────────────────────────

    [Fact]
    public void Meters_inside_each_piece_are_counted_after_the_trim()
    {
        // Путь по y = 50 от 0 до 700 м; засчитано x ∈ [200, 500]. Границы кусков — не на точках следа.
        var path = Visits.TrimmedPath(Pairs(Line(71, spacing: 10, north: 50)), 200);
        var pieces = new[] { RectanglePolygon(255, 0, 100, 100), RectanglePolygon(465, 0, 100, 100), RectanglePolygon(5, 0, 100, 100) };

        var inside = Visits.Inside(path, pieces);

        Assert.Equal(100, inside[0].Meters, 0.01);
        Assert.Equal(Start + 36_000, inside[0].LastTimeMs); // последний участок, задевший кусок, — 350→360 м
        Assert.Equal(35, inside[1].Meters, 0.01);
        Assert.False(inside.ContainsKey(2)); // этот кусок — в первых 200 м
    }

    [Fact]
    public void Piece_the_path_passes_by_is_not_visited()
    {
        var path = Visits.TrimmedPath(Pairs(Line(71, spacing: 10, north: 50)), 200);

        var inside = Visits.Inside(path, [RectanglePolygon(250, 60, 100, 100)]);

        Assert.Empty(inside);
    }

    [Fact]
    public void Path_inside_a_privacy_zone_is_not_counted()
    {
        // Путь по y = 50 через кусок x ∈ [255, 355]; зона радиусом 30 м с центром на пути срезает 60 м из 100.
        var path = Visits.TrimmedPath(Pairs(Line(71, spacing: 10, north: 50)), 200);
        var piece = RectanglePolygon(255, 0, 100, 100);

        var partly = Visits.Inside(path, [piece], PrivacyZones.Area([At(300, 50)], 30));
        var covered = Visits.Inside(path, [piece], PrivacyZones.Area([At(300, 50)], 100));

        Assert.Equal(40, partly[0].Meters, 0.5);
        Assert.Empty(covered); // весь путь по куску — в зоне
        Assert.Null(PrivacyZones.Area([], 400));
    }

    // ── Засчитанный путь ────────────────────────────────────────────────────

    [Fact]
    public void Poor_accuracy_point_is_skipped_without_breaking_the_path()
    {
        var points = Line(10);
        var verdicts = Accepted(10);
        verdicts[5] = JudgeVerdict.Ignored(TrackIssue.PoorAccuracy);

        var segments = JudgedPath.Segments(points, verdicts).Select(s => (s.From.Seq, s.To.Seq)).ToList();

        Assert.Equal(8, segments.Count);
        Assert.Contains((4, 6), segments);
    }

    [Fact]
    public void Break_drops_the_last_30_seconds_and_the_break_itself()
    {
        var points = Line(100);
        var verdicts = Accepted(100);
        verdicts[60] = JudgeVerdict.Broken(TrackIssue.Vehicle);

        var segments = JudgedPath.Segments(points, verdicts).Select(s => (s.From.Seq, s.To.Seq)).ToList();

        Assert.Equal(29 + 38, segments.Count); // 0→29 и 61→99: точки 30…60 (30 с до разрыва и сам разрыв) не в счёт
        Assert.Contains((28, 29), segments);
        Assert.Contains((61, 62), segments);
        Assert.DoesNotContain(segments, s => s.Item1 is >= 30 and <= 60 || s.Item2 is >= 30 and <= 60);
    }

    [Fact]
    public void Path_before_a_teleport_is_kept_but_the_jump_is_not()
    {
        var points = Line(100);
        var verdicts = Accepted(100);
        verdicts[50] = JudgeVerdict.Broken(TrackIssue.Teleport);

        var segments = JudgedPath.Segments(points, verdicts).Select(s => (s.From.Seq, s.To.Seq)).ToList();

        Assert.Equal(49 + 48, segments.Count);
        Assert.Contains((48, 49), segments);
        Assert.DoesNotContain((49, 51), segments);
    }

    [Fact]
    public void Length_of_the_accepted_path_is_its_meters()
    {
        var points = Line(71, spacing: 10);
        var verdicts = Accepted(71);
        verdicts[40] = JudgeVerdict.Broken(TrackIssue.Teleport); // скачок 390→400→410 м не в счёт: минус два участка

        Assert.Equal(700, JudgedPath.Length(Pairs(points)), 0.01);
        Assert.Equal(680, JudgedPath.Length(JudgedPath.Segments(points, verdicts)), 0.01);
    }

    // ── Правило визита ──────────────────────────────────────────────────────

    [Fact]
    public void Visit_after_20_hours_adds_a_level()
    {
        var visited = CaptureRules.Visit(Land(1, visited: T0.AddHours(-21), leveledUp: T0.AddHours(-21)), T0, Rules);

        Assert.Equal((2, T0, T0), (visited!.Level, visited.LastVisitAt, visited.LastLevelUpAt));
    }

    [Fact]
    public void Visit_sooner_only_refreshes_the_land()
    {
        var land = Land(1, visited: T0.AddHours(-19), leveledUp: T0.AddHours(-19));

        var visited = CaptureRules.Visit(land, T0, Rules);

        Assert.Equal((1, T0, land.LastLevelUpAt), (visited!.Level, visited.LastVisitAt, visited.LastLevelUpAt));
    }

    [Fact]
    public void Besieged_or_full_land_does_not_grow()
    {
        var besieged = Land(1, visited: T0.AddDays(-1), leveledUp: T0.AddDays(-1)) with { SiegeUntil = T0.AddHours(1) };
        var full = Land(3, visited: T0.AddDays(-1), leveledUp: T0.AddDays(-1));

        Assert.Equal(1, CaptureRules.Visit(besieged, T0, Rules)!.Level);
        Assert.Equal(3, CaptureRules.Visit(full, T0, Rules)!.Level);
    }

    [Fact]
    public void Visit_fixes_the_decayed_level_and_grows_from_it()
    {
        // Уровень 3, неделю без визитов: действующий — 2 (минус уровень за 6 дней). Визит закрепляет 2 и добавляет 1.
        var visited = CaptureRules.Visit(Land(3, visited: T0.AddDays(-7), leveledUp: T0.AddDays(-7)), T0, Rules);

        Assert.Equal(3, visited!.Level);
        Assert.Equal(T0, visited.LastVisitAt);
    }

    [Fact]
    public void Land_that_has_decayed_away_is_not_returned_by_a_visit()
    {
        Assert.Null(CaptureRules.Visit(Land(1, visited: T0.AddDays(-7), leveledUp: T0.AddDays(-7)), T0, Rules));
    }

    [Fact]
    public void Late_visit_does_not_move_the_last_visit_back()
    {
        var land = Land(1, visited: T0.AddHours(1), leveledUp: T0.AddHours(1));

        var visited = CaptureRules.Visit(land, T0, Rules);

        Assert.Equal(land, visited);
    }

    // ── Вспомогательное ─────────────────────────────────────────────────────

    private static ParcelState Land(int level, DateTimeOffset visited, DateTimeOffset leveledUp) => new()
    {
        OwnerId = Owner,
        Level = level,
        LastVisitAt = visited,
        LastLevelUpAt = leveledUp,
    };

    private static JudgeVerdict[] Accepted(int count) => [.. Enumerable.Repeat(JudgeVerdict.Accepted, count)];

    private static IEnumerable<(TrackPoint From, TrackPoint To)> Pairs(IReadOnlyList<TrackPoint> points) =>
        JudgedPath.Segments(points, Accepted(points.Count));

    /// <summary>Прямая на восток от «начала»: точка в секунду через <paramref name="spacing"/> метров.</summary>
    private static TrackPoint[] Line(int count, double spacing = 1.4, double north = 0) =>
        [.. Enumerable.Range(0, count).Select(i => Point(i, spacing * i, north))];

    /// <summary>Точка в метрах от «начала»; через широту и долготу метры возвращаются с точностью до миллиметров.</summary>
    private static TrackPoint Point(int seq, double east, double north = 0)
    {
        var (latitude, longitude) = Utm34.Inverse(OriginX + east, OriginY + north);
        return TrackPoint.FromMeasurements(seq, Start + (seq * 1_000L), latitude, longitude, 5, 1.4, PointFlags.None);
    }
}
