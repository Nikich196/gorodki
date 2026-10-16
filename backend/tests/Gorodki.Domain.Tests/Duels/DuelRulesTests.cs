using Gorodki.Domain.Duels;

namespace Gorodki.Domain.Tests.Duels;

/// <summary>Правила дуэлей (PLAN.md, §3.14): условия, кто впереди и кто победил, серия побед и редкость значка.</summary>
public sealed class DuelRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(7, true)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(14, false)]
    public void Duel_lasts_one_three_or_seven_days(int days, bool allowed)
    {
        Assert.Equal(allowed, DuelRules.IsAllowedDays(days));
    }

    [Fact]
    public void Account_duels_from_exactly_48_hours()
    {
        Assert.False(DuelRules.IsOldEnough(T0, T0.AddHours(48).AddTicks(-1)));
        Assert.True(DuelRules.IsOldEnough(T0, T0.AddHours(48)));
    }

    [Theory]
    [InlineData(10.0, 5.0, false, DuelOutcome.ChallengerWon)]
    [InlineData(5.0, 10.0, false, DuelOutcome.OpponentWon)]
    [InlineData(0.0, 0.0, false, DuelOutcome.Draw)] // 0 : 0 — ничья, а не победа вызвавшего
    [InlineData(9_000.04, 9_000.01, false, DuelOutcome.Draw)] // разница в сотых — на экране та же цифра
    [InlineData(61_200.0, 59_800.0, true, DuelOutcome.OpponentWon)] // время: меньше — лучше
    [InlineData(59_800.0, 61_200.0, true, DuelOutcome.ChallengerWon)]
    public void Higher_score_wins_except_time_where_lower_wins(double challenger, double opponent, bool lowerIsBetter, DuelOutcome expected)
    {
        Assert.Equal(expected, DuelRules.Decide(challenger, opponent, lowerIsBetter));
    }

    [Fact]
    public void Missing_result_loses_and_two_missing_are_a_draw()
    {
        // segmentTime без прохода — null: хуже любого времени.
        Assert.Equal(DuelOutcome.ChallengerWon, DuelRules.Decide(95_000, null, lowerIsBetter: true));
        Assert.Equal(DuelOutcome.OpponentWon, DuelRules.Decide(null, 95_000, lowerIsBetter: true));
        Assert.Equal(DuelOutcome.Draw, DuelRules.Decide(null, null, lowerIsBetter: true));
        Assert.Null(DuelRules.Leader(null, null, lowerIsBetter: true));
    }

    [Fact]
    public void Leader_is_the_side_that_would_win_now()
    {
        Assert.Equal(DuelSideKind.Challenger, DuelRules.Leader(1, 0, lowerIsBetter: false));
        Assert.Equal(DuelSideKind.Opponent, DuelRules.Leader(0, 1, lowerIsBetter: false));
        Assert.Null(DuelRules.Leader(3, 3, lowerIsBetter: false));
    }

    [Fact]
    public void Swapping_the_sides_swaps_the_result()
    {
        var scores = new double?[] { null, 0, 0.04, 1, 99.95, 100, 5_000 };
        foreach (var lower in new[] { false, true })
        {
            foreach (var a in scores)
            {
                foreach (var b in scores)
                {
                    var forward = DuelRules.Decide(a, b, lower);
                    var backward = DuelRules.Decide(b, a, lower);
                    Assert.Equal(
                        forward switch
                        {
                            DuelOutcome.ChallengerWon => DuelOutcome.OpponentWon,
                            DuelOutcome.OpponentWon => DuelOutcome.ChallengerWon,
                            _ => forward,
                        },
                        backward);
                }
            }
        }
    }

    [Fact]
    public void Streak_counts_wins_since_the_last_loss_and_skips_draws()
    {
        Assert.Equal(0, DuelRules.WinStreak([]));
        Assert.Equal(3, DuelRules.WinStreak([true, true, true]));
        Assert.Equal(2, DuelRules.WinStreak([true, null, true, false, true, true])); // ничья не рвёт и не продлевает
        Assert.Equal(0, DuelRules.WinStreak([false, true, true]));
        Assert.Equal(0, DuelRules.WinStreak([null, null]));
    }

    [Theory]
    [InlineData(0, DuelBadge.None)]
    [InlineData(1, DuelBadge.Bronze)]
    [InlineData(2, DuelBadge.Bronze)]
    [InlineData(3, DuelBadge.Silver)]
    [InlineData(4, DuelBadge.Silver)]
    [InlineData(5, DuelBadge.Gold)]
    [InlineData(12, DuelBadge.Gold)]
    public void Badge_rarity_grows_with_the_win_streak(int streak, DuelBadge badge)
    {
        Assert.Equal(badge, DuelRules.BadgeFor(streak));
    }

    [Fact]
    public void Numbers_follow_the_plan()
    {
        // §3.14: 1, 3 или 7 дней; аккаунт старше 48 ч; ≤2 активных; пара — не чаще раза в неделю.
        Assert.Equal([1, 3, 7], DuelRules.AllowedDays);
        Assert.Equal(TimeSpan.FromHours(48), DuelRules.MinAccountAge);
        Assert.Equal(2, DuelRules.MaxActive);
        Assert.Equal(TimeSpan.FromDays(7), DuelRules.PairCooldown);
    }
}
