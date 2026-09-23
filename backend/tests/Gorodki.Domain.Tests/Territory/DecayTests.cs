using Gorodki.Domain.Territory;

namespace Gorodki.Domain.Tests.Territory;

/// <summary>Угасание земли (PLAN.md, §3.3): −1 уровень за 6 дней без визита, L1 уходит в ничью, 3 дня — «призрак».</summary>
public sealed class DecayTests
{
    private static readonly Guid Owner = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Anna = new("00000000-0000-0000-0000-00000000000a");
    private static readonly DateTimeOffset Visit = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly TerritoryRules Rules = new();

    private static ParcelState Land(int level) => new()
    {
        OwnerId = Owner,
        Level = level,
        LastVisitAt = Visit,
        LastLevelUpAt = Visit,
    };

    [Theory]
    [InlineData(0, 3)]
    [InlineData(6 * 24 - 1, 3)]
    [InlineData(6 * 24, 2)]
    [InlineData(12 * 24, 1)]
    [InlineData(18 * 24, 0)]
    [InlineData(100 * 24, 0)]
    public void Level_three_loses_a_level_every_six_days(int hours, int expected)
    {
        Assert.Equal(expected, Decay.EffectiveLevel(Land(3), Visit.AddHours(hours), Rules));
    }

    [Fact]
    public void Lost_land_is_a_ghost_for_three_days()
    {
        var land = Land(1);

        Assert.False(Decay.IsGhost(land, Visit.AddDays(5), Rules)); // ещё своя
        Assert.True(Decay.IsGhost(land, Visit.AddDays(6), Rules));
        Assert.True(Decay.IsGhost(land, Visit.AddDays(9).AddMinutes(-1), Rules));
        Assert.False(Decay.IsGhost(land, Visit.AddDays(9), Rules));
    }

    [Fact]
    public void Decayed_land_is_neutral_even_for_its_former_owner()
    {
        var (state, outcome) = CaptureRules.Decide(Land(1), new CaptureContext(Anna, Visit.AddDays(7), new HashSet<Guid>()), Rules);

        Assert.Equal(PieceOutcome.ClaimedNeutral, outcome);
        Assert.Equal((Anna, 1), (state!.OwnerId, state.Level));
    }

    [Fact]
    public void Level_two_that_decayed_to_one_changes_hands_instead_of_cracking()
    {
        var (state, outcome) = CaptureRules.Decide(Land(2), new CaptureContext(Anna, Visit.AddDays(6), new HashSet<Guid>()), Rules);

        Assert.Equal(PieceOutcome.Transferred, outcome);
        Assert.Equal(Anna, state!.OwnerId);
    }

    [Fact]
    public void Crack_takes_a_level_from_the_stored_level_so_decay_is_never_counted_twice()
    {
        // L3, 7 дней без визита — действует L2. Трещина: действует L1; хранится L2 с тем же визитом.
        var at = Visit.AddDays(7);

        var (state, outcome) = CaptureRules.Decide(Land(3), new CaptureContext(Anna, at, new HashSet<Guid>()), Rules);

        Assert.Equal(PieceOutcome.Cracked, outcome);
        Assert.Equal((2, Visit), (state!.Level, state.LastVisitAt));
        Assert.Equal(1, Decay.EffectiveLevel(state, at, Rules));
    }

    [Fact]
    public void Owner_visit_fixes_the_decayed_level_and_restarts_the_clock()
    {
        // L3, 7 дней без визита — действует L2; визит: L2 + 1 за рост уровня (последний рост — 7 дней назад) = L3.
        var at = Visit.AddDays(7);

        var (state, outcome) = CaptureRules.Decide(Land(3), new CaptureContext(Owner, at, new HashSet<Guid>()), Rules);

        Assert.Equal(PieceOutcome.Refreshed, outcome);
        Assert.Equal((3, at), (state!.Level, state.LastVisitAt));
        Assert.Equal(3, Decay.EffectiveLevel(state, at.AddDays(5), Rules));
    }

    [Fact]
    public void Untouched_protected_piece_keeps_its_stored_state()
    {
        var shielded = Land(3) with { ShieldUntil = Visit.AddDays(8) };

        var (state, outcome) = CaptureRules.Decide(shielded, new CaptureContext(Anna, Visit.AddDays(7), new HashSet<Guid>()), Rules);

        Assert.Equal(PieceOutcome.Shielded, outcome);
        Assert.Equal(shielded, state);
    }
}
