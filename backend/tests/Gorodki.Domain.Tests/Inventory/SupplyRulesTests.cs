using Gorodki.Domain.Inventory;

namespace Gorodki.Domain.Tests.Inventory;

/// <summary>«Припасы» (PLAN.md, §3.11): за каждые 2 км — фишка, не больше 4 в сутки и 12 ячеек.</summary>
public sealed class SupplyRulesTests
{
    [Theory]
    [InlineData(1_999, 0, 0, 0)]
    [InlineData(2_000, 0, 0, 1)]
    [InlineData(9_500, 0, 0, 4)] // 4 полных по 2 км
    [InlineData(20_000, 0, 0, 4)] // больше 4 за сутки — нет
    [InlineData(6_000, 3, 0, 1)] // три уже выданы сегодня
    [InlineData(6_000, 4, 0, 0)]
    [InlineData(6_000, 0, 11, 1)] // одна свободная ячейка
    [InlineData(6_000, 0, 12, 0)] // Рюкзак полон
    [InlineData(0, 0, 0, 0)]
    public void Items_per_run_are_capped_by_the_day_and_by_free_slots(double meters, int today, int occupied, int expected)
    {
        Assert.Equal(expected, SupplyRules.Count(meters, today, occupied));
    }

    [Fact]
    public void Radar_and_streak_freeze_alternate_starting_with_radar()
    {
        Assert.Equal([true, false, true, false], Enumerable.Range(0, 4).Select(SupplyRules.IsRadar));
    }
}
