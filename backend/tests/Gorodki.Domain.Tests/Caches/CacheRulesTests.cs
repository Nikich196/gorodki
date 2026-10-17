using Gorodki.Domain.Caches;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Runs;

namespace Gorodki.Domain.Tests.Caches;

/// <summary>Находка тайника: «пройти в 30 м в валидном забеге» (PLAN.md, §3.12) — только по пути, принятому судьёй.</summary>
public sealed class CacheRulesTests
{
    private const long Start = 1_790_000_000_000;
    private static readonly (double X, double Y) Origin = (684_500, 5_775_500);

    [Fact]
    public void Cache_within_30_metres_of_the_path_is_found_when_the_path_first_comes_that_close()
    {
        var points = Line(count: 200); // 1,4 м в секунду на восток
        var near = Spot(140, 20); // в 20 м от точки 100; в 30 м путь подходит на 140 − √(30² − 20²) ≈ 117,6 м — точка 84
        var far = Spot(140, 35);

        var found = CacheRules.Finds(points, Accepted(points.Length), [near, far]);

        var at = Assert.Single(found, f => f.Key == near.Id).Value;
        Assert.InRange(at, Start + (83 * 1_000L), Start + (85 * 1_000L));
        Assert.DoesNotContain(far.Id, found.Keys);
    }

    [Fact]
    public void Sparse_points_find_a_cache_between_them_by_the_segment()
    {
        // Велосипед: точки через 80 м, тайник посередине в 10 м от пути — от обеих точек он дальше 30 м.
        var points = new[] { Point(0, 0, 0), Point(1, 80, 0) };
        var cache = Spot(40, 10);

        var found = CacheRules.Finds(points, Accepted(2), [cache]);

        Assert.Equal(Start + 500, found[cache.Id]); // ровно посередине по времени
    }

    [Fact]
    public void Path_from_a_car_finds_nothing()
    {
        // Точка в секунду через 14 м (~50 км/ч), судья порвал след на 150-й точке — «транспорт»: 30 с до разрыва не в счёт.
        var points = Line(count: 200, spacing: 14);
        var verdicts = Accepted(points.Length);
        verdicts[150] = JudgeVerdict.Broken(TrackIssue.Vehicle);
        var cache = Spot(14 * 135, 5);

        Assert.Empty(CacheRules.Finds(points, verdicts, [cache]));
    }

    [Fact]
    public void Poor_accuracy_point_alone_does_not_find_but_the_path_goes_on()
    {
        // Тайник у точки 50; точка 50 отброшена (плохая точность), но путь 49 → 51 проходит рядом.
        var points = Line(count: 100);
        var verdicts = Accepted(points.Length);
        verdicts[50] = JudgeVerdict.Ignored(TrackIssue.PoorAccuracy);
        var cache = Spot(70, 0);

        Assert.True(CacheRules.Finds(points, verdicts, [cache]).ContainsKey(cache.Id));
    }

    private static CacheSpot Spot(double east, double north)
    {
        var (latitude, longitude) = Utm34.Inverse(Origin.X + east, Origin.Y + north);
        return new CacheSpot(Guid.NewGuid(), latitude, longitude);
    }

    private static JudgeVerdict[] Accepted(int count) => [.. Enumerable.Repeat(JudgeVerdict.Accepted, count)];

    private static TrackPoint[] Line(int count, double spacing = 1.4) =>
        [.. Enumerable.Range(0, count).Select(i => Point(i, spacing * i, 0))];

    private static TrackPoint Point(int seq, double east, double north)
    {
        var (latitude, longitude) = Utm34.Inverse(Origin.X + east, Origin.Y + north);
        return TrackPoint.FromMeasurements(seq, Start + (seq * 1_000L), latitude, longitude, 5, 1.4, PointFlags.None);
    }
}
