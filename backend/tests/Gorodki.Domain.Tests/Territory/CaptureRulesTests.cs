using Gorodki.Domain.Territory;
using static Gorodki.Domain.Tests.Geo.TestGeometry;

namespace Gorodki.Domain.Tests.Territory;

/// <summary>Правила куска (PLAN.md, §3.3): лимиты снятия уровней и петли, пришедшие с опозданием.</summary>
public sealed class CaptureRulesTests
{
    private static readonly Guid Owner = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Anna = new("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Boris = new("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid Vera = new("00000000-0000-0000-0000-00000000000c");
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly TerritoryRules Rules = new();

    private static ParcelState Land(int level, DateTimeOffset? visited = null) => new()
    {
        OwnerId = Owner,
        Level = level,
        LastVisitAt = visited ?? T0.AddDays(-2),
        LastLevelUpAt = T0.AddDays(-2),
    };

    private static (ParcelState? State, PieceOutcome Outcome) Attack(ParcelState? piece, Guid attacker, DateTimeOffset at) =>
        CaptureRules.Decide(piece, new CaptureContext(attacker, at, new HashSet<Guid>()), Rules);

    [Fact]
    public void One_attacker_removes_at_most_one_level_per_window()
    {
        // Три круга вокруг квартала за один забег: раньше L3 уходил нападающему.
        var (afterFirst, first) = Attack(Land(3), Anna, T0);
        var (afterSecond, second) = Attack(afterFirst, Anna, T0.AddMinutes(10));
        var (afterThird, third) = Attack(afterSecond, Anna, T0.AddMinutes(20));

        Assert.Equal((PieceOutcome.Cracked, PieceOutcome.LossLimited, PieceOutcome.LossLimited), (first, second, third));
        Assert.Equal((Owner, 2), (afterThird!.OwnerId, afterThird.Level));
    }

    [Fact]
    public void All_attackers_together_remove_at_most_two_levels_per_window()
    {
        var (a, _) = Attack(Land(3), Anna, T0);
        var (b, byBoris) = Attack(a, Boris, T0.AddHours(1));
        var (c, byVera) = Attack(b, Vera, T0.AddHours(2));

        Assert.Equal((PieceOutcome.Cracked, PieceOutcome.LossLimited), (byBoris, byVera));
        Assert.Equal((Owner, 1), (c!.OwnerId, c.Level));
        Assert.Equal(AttackerSet.Of([Anna, Boris]), c.LossAttackers);
    }

    [Fact]
    public void Level_two_can_change_hands_after_two_different_attackers()
    {
        var (cracked, _) = Attack(Land(2), Anna, T0);
        var (taken, outcome) = Attack(cracked, Boris, T0.AddHours(1));

        Assert.Equal(PieceOutcome.Transferred, outcome);
        Assert.Equal((Boris, 1), (taken!.OwnerId, taken.Level));
        Assert.Equal(0, taken.LossAttackers.Count);
    }

    [Fact]
    public void Window_expires_after_20_hours()
    {
        var (cracked, _) = Attack(Land(3), Anna, T0);
        var (again, outcome) = Attack(cracked!, Anna, T0.AddHours(20));

        Assert.Equal(PieceOutcome.Cracked, outcome);
        Assert.Equal(1, again!.Level);
        Assert.Equal(T0.AddHours(20), again.LossWindowSince);
        Assert.Equal(AttackerSet.Of([Anna]), again.LossAttackers);
    }

    [Fact]
    public void Late_loop_does_not_take_land_the_owner_visited_after_it()
    {
        // Анна пробежала петлю в 9:00 без сети, Борис свою в 10:00 онлайн. Петля Анны дошла последней.
        var (borisLand, _) = CaptureRules.Decide(null, new CaptureContext(Boris, T0.AddHours(1), new HashSet<Guid>()), Rules);

        var (after, outcome) = Attack(borisLand, Anna, T0);

        Assert.Equal(PieceOutcome.Superseded, outcome);
        Assert.Equal(borisLand, after);
    }

    [Fact]
    public void Late_visit_of_own_land_does_not_move_time_back()
    {
        var land = Land(1, visited: T0.AddHours(3));

        var (after, outcome) = CaptureRules.Decide(land, new CaptureContext(Owner, T0, new HashSet<Guid>()), Rules);

        Assert.Equal(PieceOutcome.Refreshed, outcome);
        Assert.Equal(T0.AddHours(3), after!.LastVisitAt);
    }

    [Fact]
    public void Same_attackers_in_another_order_are_the_same_state()
    {
        // Куски с одинаковым состоянием сливаются — набор нападающих сравнивается по составу.
        Assert.Equal(AttackerSet.Of([Boris, Anna]), AttackerSet.Empty.With(Anna).With(Boris));
        Assert.Equal(AttackerSet.Of([Anna, Boris]).GetHashCode(), AttackerSet.Of([Boris, Anna]).GetHashCode());
        Assert.Equal(Land(2) with { LossAttackers = AttackerSet.Of([Anna]) }, Land(2) with { LossAttackers = AttackerSet.Of([Anna]) });
    }

    [Fact]
    public void Three_laps_around_a_level_three_quarter_leave_it_with_the_owner()
    {
        // То же на настоящей геометрии: круги Анны вокруг квартала владельца L3 в одном забеге.
        var map = new TerritoryMap();
        var quarter = RectanglePolygon(100, 100, 200, 200);
        var tile = Gorodki.Domain.Geo.TileKey.Of(quarter.EnvelopeInternal.MinX, quarter.EnvelopeInternal.MinY);
        map.Load([new Parcel(tile, quarter, Land(3))]);

        var results = Enumerable.Range(0, 3)
            .Select(lap => map.Apply(RectanglePolygon(90, 90, 220, 220), new CaptureContext(Anna, T0.AddMinutes(10 * lap), new HashSet<Guid>())))
            .ToList();

        Assert.Equal(40_000, results[0].Area(PieceOutcome.Cracked), 3);
        Assert.Equal(40_000, results[2].Area(PieceOutcome.LossLimited), 3);
        Assert.Equal(40_000, map.AreaOf(Owner), 3);
        Assert.Empty(TerritoryInvariants.Check(map));
    }

    [Fact]
    public void New_account_takes_neutral_land_but_removes_no_levels()
    {
        // Аккаунт моложе 48 ч или с пробегом меньше 3 км (§3.3): ничью землю берёт, чужую не трогает, свою освежает.
        var newcomer = new CaptureContext(Anna, T0, new HashSet<Guid>(), CanRemoveLevels: false);

        var (neutral, neutralOutcome) = CaptureRules.Decide(null, newcomer, Rules);
        var weak = Land(1);
        var (afterWeak, weakOutcome) = CaptureRules.Decide(weak, newcomer, Rules);
        var strong = Land(3);
        var (afterStrong, strongOutcome) = CaptureRules.Decide(strong, newcomer, Rules);
        var own = Land(1) with { OwnerId = Anna };
        var (_, ownOutcome) = CaptureRules.Decide(own, newcomer, Rules);

        Assert.Equal((Anna, PieceOutcome.ClaimedNeutral), (neutral!.OwnerId, neutralOutcome));
        Assert.Equal((weak, PieceOutcome.NewAccountLimited), (afterWeak, weakOutcome));
        Assert.Equal((strong, PieceOutcome.NewAccountLimited), (afterStrong, strongOutcome));
        Assert.Equal(PieceOutcome.Refreshed, ownOutcome);
    }

    // ── Большая петля (§3.3, решено 25.09 в #48) ─────────────────────────────

    private static (ParcelState? State, PieceOutcome Outcome) BigLoop(ParcelState? piece, Guid attacker, DateTimeOffset at, params Guid[] clan) =>
        CaptureRules.Decide(piece, new CaptureContext(attacker, at, clan.ToHashSet(), BigLoop: true), Rules);

    [Fact]
    public void Big_loop_only_marks_enemy_level_one_contested_for_24_hours()
    {
        var land = Land(1);

        var (after, outcome) = BigLoop(land, Anna, T0);

        Assert.Equal(PieceOutcome.Contested, outcome);
        Assert.Equal(land with { ContestedUntil = T0.AddHours(24) }, after);
    }

    [Fact]
    public void Big_loop_leaves_enemy_level_three_without_crack_siege_or_loss_counters()
    {
        var land = Land(3);

        var (after, outcome) = BigLoop(land, Anna, T0);

        Assert.Equal(PieceOutcome.Contested, outcome);
        Assert.Equal(land with { ContestedUntil = T0.AddHours(24) }, after);

        // Счётчики снятия не тронуты: обычная петля того же игрока сразу после — всё ещё «трещина».
        var (_, next) = Attack(after, Anna, T0.AddMinutes(10));
        Assert.Equal(PieceOutcome.Cracked, next);
    }

    [Fact]
    public void Big_loop_mark_is_extended_by_a_later_loop_and_never_shortened_by_an_earlier_one()
    {
        var (first, _) = BigLoop(Land(2), Anna, T0);
        var (extended, _) = BigLoop(first, Boris, T0.AddHours(5));
        var (late, lateOutcome) = BigLoop(extended, Vera, T0.AddHours(1)); // пришла из офлайна позже

        Assert.Equal(T0.AddHours(29), extended!.ContestedUntil);
        Assert.Equal((extended, PieceOutcome.Contested), (late, lateOutcome));
    }

    [Fact]
    public void Big_loop_takes_neutral_and_decayed_land_and_refreshes_own_and_clan_land()
    {
        var decayed = Land(1, visited: T0.AddDays(-7)); // L1 без визита 6 дней — угасла, ничья
        var own = Land(2) with { OwnerId = Anna };
        var mates = Land(2);

        var (neutral, neutralOutcome) = BigLoop(null, Anna, T0);
        var (fromDecayed, decayedOutcome) = BigLoop(decayed, Anna, T0);
        var (refreshed, ownOutcome) = BigLoop(own, Anna, T0);
        var (mate, mateOutcome) = BigLoop(mates, Anna, T0, Owner);

        Assert.Equal((Anna, 1, PieceOutcome.ClaimedNeutral), (neutral!.OwnerId, neutral.Level, neutralOutcome));
        Assert.Equal((Anna, PieceOutcome.ClaimedNeutral), (fromDecayed!.OwnerId, decayedOutcome));
        Assert.Equal((PieceOutcome.Refreshed, T0), (ownOutcome, refreshed!.LastVisitAt));
        Assert.Equal((PieceOutcome.RefreshedForClanMate, Owner, T0), (mateOutcome, mate!.OwnerId, mate.LastVisitAt));
        Assert.All(new[] { neutral, fromDecayed, refreshed, mate }, s => Assert.Null(s!.ContestedUntil));
    }

    [Fact]
    public void Big_loop_does_not_mark_shielded_superseded_or_new_account_targets()
    {
        var shielded = Land(1) with { ShieldUntil = T0.AddHours(1) };
        var visitedLater = Land(1, visited: T0.AddHours(1));
        var weak = Land(1);

        Assert.Equal((shielded, PieceOutcome.Shielded), BigLoop(shielded, Anna, T0));
        Assert.Equal((visitedLater, PieceOutcome.Superseded), BigLoop(visitedLater, Anna, T0));
        Assert.Equal(
            (weak, PieceOutcome.NewAccountLimited),
            CaptureRules.Decide(weak, new CaptureContext(Anna, T0, new HashSet<Guid>(), CanRemoveLevels: false, BigLoop: true), Rules));
    }

    [Fact]
    public void Contested_mark_has_no_game_power_and_survives_owners_visit()
    {
        // Пометка не мешает ни росту уровня, ни обычному захвату — и визит её не снимает (§3.3: «без игровой силы»).
        var marked = Land(1) with { ContestedUntil = T0.AddHours(24) };

        var visited = CaptureRules.Visit(marked, T0, Rules)!;
        var (taken, outcome) = Attack(marked, Anna, T0);

        Assert.Equal((2, T0.AddHours(24)), (visited.Level, visited.ContestedUntil));
        Assert.Equal((PieceOutcome.Transferred, Anna), (outcome, taken!.OwnerId));
        Assert.Null(taken.ContestedUntil);
    }
}
