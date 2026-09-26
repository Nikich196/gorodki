using CsCheck;
using Gorodki.Domain.Config;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Scoring;
using Gorodki.Domain.Territory;
using NetTopologySuite.Geometries;

namespace Gorodki.Domain.Tests.Scoring;

/// <summary>Очки сезона (PLAN.md, §3.5): ступени с убывающей отдачей, бонусы, ценность земли, дистанция.</summary>
public sealed class ScoresTests
{
    private static readonly ScoringConfig Config = GameConfig.Default.Scoring;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(300, 200)] // 100 + 200 × 0,5
    [InlineData(1_000, 425)] // 100 + 200 + 500 × 0,25
    [InlineData(3_000, 725)] // 100 + 200 + 375 + 1 000 × 0,05
    public void Capture_tiers_pay_each_step_at_its_own_rate(double sotki, double expected)
    {
        Assert.Equal(expected, Scores.Tiered(sotki, Config.CaptureTiers), 9);
    }

    [Fact]
    public void Nothing_is_paid_beyond_a_bounded_last_tier()
    {
        ScoreTier[] tiers = [new(10, 1), new(20, 0.5)];

        Assert.Equal(15, Scores.Tiered(1_000, tiers), 9);
    }

    [Fact]
    public void Every_further_sotka_is_worth_no_more_than_the_previous_one()
    {
        // Убывающая отдача (§3.5): прибавка за те же d соток дальше по ступеням — не больше, чем раньше.
        var cases = from a in Gen.Double[0, 5_000] from b in Gen.Double[0, 5_000] from d in Gen.Double[0, 500] select (a, b, d);
        cases.Sample(sample =>
        {
            var (a, b, d) = sample;
            var (low, high) = a <= b ? (a, b) : (b, a);
            foreach (var tiers in new[] { Config.CaptureTiers, Config.DailyTiers })
            {
                var earlier = Scores.Tiered(low + d, tiers) - Scores.Tiered(low, tiers);
                var later = Scores.Tiered(high + d, tiers) - Scores.Tiered(high, tiers);
                Assert.True(later <= earlier + 1e-9, $"{low}..{high}, +{d}: {earlier} → {later}");
                Assert.True(earlier <= d + 1e-9); // полная ставка — 1
            }
        });
    }

    [Fact]
    public void Default_tiers_are_diminishing_and_broken_ones_are_recognised()
    {
        Assert.True(Scores.AreDiminishing(Config.CaptureTiers));
        Assert.True(Scores.AreDiminishing(Config.DailyTiers));

        Assert.False(Scores.AreDiminishing([]));
        Assert.False(Scores.AreDiminishing([new(100, 1)])); // последняя — с границей
        Assert.False(Scores.AreDiminishing([new(100, 0.5), new(null, 1)])); // ставка растёт
        Assert.False(Scores.AreDiminishing([new(500, 1), new(100, 0.5), new(null, 0.1)])); // границы не по порядку
        Assert.False(Scores.AreDiminishing([new(null, 1), new(null, 0.5)])); // безграничная — не последняя
        Assert.False(Scores.AreDiminishing([new(100, -1), new(null, -2)]));
    }

    [Fact]
    public void Enemy_land_and_a_removed_level_give_a_bonus_and_untaken_land_gives_nothing()
    {
        var areas = new Dictionary<PieceOutcome, double>
        {
            [PieceOutcome.ClaimedNeutral] = 5_000,
            [PieceOutcome.Transferred] = 2_000, // × 1,5
            [PieceOutcome.Cracked] = 1_000, // × 0,5
            [PieceOutcome.Refreshed] = 9_000,
            [PieceOutcome.RefreshedForClanMate] = 9_000,
            [PieceOutcome.Shielded] = 9_000,
            [PieceOutcome.LossLimited] = 9_000,
            [PieceOutcome.Superseded] = 9_000,
            [PieceOutcome.NewAccountLimited] = 9_000,
            [PieceOutcome.Contested] = 9_000,
        };

        Assert.Equal(5_000 + 3_000 + 500, Scores.ScoredSquareMeters(areas, Config), 9);
    }

    [Fact]
    public void Capture_points_follow_the_capture_and_the_daily_tiers()
    {
        var hectare = new Dictionary<PieceOutcome, double> { [PieceOutcome.ClaimedNeutral] = 10_000 };

        var first = Scores.ForCapture(hectare, dayBasisBefore: 0, landValue: 1, Config);
        // Сутки уже почти на границе первой ступени (1 000 зачётных соток): 50 — полностью, 50 — за половину.
        var late = Scores.ForCapture(hectare, dayBasisBefore: 950, landValue: 1, Config);
        var arena = Scores.ForCapture(hectare, dayBasisBefore: 0, landValue: 2, Config);

        Assert.Equal(new CaptureScore(100, 100), first);
        Assert.Equal(new CaptureScore(75, 100), late);
        Assert.Equal(200, arena.Points);
    }

    [Fact]
    public void Nothing_taken_gives_no_points()
    {
        var refreshed = new Dictionary<PieceOutcome, double> { [PieceOutcome.Refreshed] = 50_000 };

        Assert.Equal(new CaptureScore(0, 0), Scores.ForCapture(refreshed, 0, 1, Config));
    }

    [Fact]
    public void Days_points_do_not_depend_on_the_order_of_its_captures()
    {
        // Сумма ступеней суток «телескопическая»: Σ (T(до + a) − T(до)) = T(Σ a). Порядок меняет только округление.
        Gen.Double[2_500, 400_000].Array[1, 12].Sample(areas =>
        {
            int Total(IEnumerable<double> order)
            {
                var before = 0.0;
                var points = 0;
                foreach (var area in order)
                {
                    var score = Scores.ForCapture(new Dictionary<PieceOutcome, double> { [PieceOutcome.ClaimedNeutral] = area }, before, 1, Config);
                    before += score.Basis;
                    points += score.Points;
                }

                return points;
            }

            var basis = areas.Sum(a => Scores.Tiered(a / Scores.SquareMetersPerSotka, Config.CaptureTiers));
            var exact = Scores.Tiered(basis, Config.DailyTiers);
            Assert.InRange(Total(areas), exact - areas.Length, exact + areas.Length);
            Assert.InRange(Total(areas.Reverse()), exact - areas.Length, exact + areas.Length);
        });
    }

    [Fact]
    public void Land_is_all_city_until_the_land_use_layer_exists()
    {
        var square = new GeometryFactory().CreatePolygon(
            [new Coordinate(0, 0), new Coordinate(100, 0), new Coordinate(100, 100), new Coordinate(0, 100), new Coordinate(0, 0)]);

        Assert.Equal(1, LandValue.Factor(square, Config.LandValue));
    }

    [Theory]
    [InlineData(5_000, 0, 50, 5_000)]
    [InlineData(1_449, 0, 14, 1_449)]
    [InlineData(5_000, 18_000, 20, 2_000)] // до потолка 20 км осталось 2 км
    [InlineData(5_000, 20_000, 0, 0)]
    [InlineData(25_000, 0, 200, 20_000)]
    [InlineData(0, 0, 0, 0)]
    public void Distance_gives_ten_points_a_kilometre_up_to_twenty_kilometres_a_day(
        double meters, double before, int points, double counted)
    {
        Assert.Equal(new DistanceScore(points, counted), Scores.ForDistance(meters, before, League.Run, Config));
    }

    [Fact]
    public void Bike_distance_is_worth_a_third()
    {
        Assert.Equal(new DistanceScore(33, 10_000), Scores.ForDistance(10_000, 0, League.Bike, Config));
        Assert.Equal(66, Scores.ForDistance(30_000, 0, League.Bike, Config).Points); // тот же потолок 20 км
    }
}
