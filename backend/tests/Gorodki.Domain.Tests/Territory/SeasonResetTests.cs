using CsCheck;
using Gorodki.Domain.Territory;

namespace Gorodki.Domain.Tests.Territory;

/// <summary>
/// Смена сезона (PLAN.md, §3.4; #30, п. 1–2): мягкий сброс куска и «касались в этом сезоне» (<see cref="ParcelState.TouchedAt"/>).
/// </summary>
public sealed class SeasonResetTests
{
    private static readonly Guid Owner = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Anna = new("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Boris = new("00000000-0000-0000-0000-00000000000b");

    /// <summary>Начало Сезона 1 — 30.11 00:00 по Минску.</summary>
    private static readonly DateTimeOffset T = new(2026, 11, 29, 21, 0, 0, TimeSpan.Zero);

    private static readonly TerritoryRules Rules = new();
    private static readonly TimeSpan D = Rules.DecayInterval;

    private static ParcelState Land(int level, DateTimeOffset visited) => new()
    {
        OwnerId = Owner,
        Level = level,
        LastVisitAt = visited,
        LastLevelUpAt = visited.AddHours(-30),
        TouchedAt = visited,
    };

    [Fact]
    public void Level_goes_to_one_and_shield_siege_and_the_loss_window_are_lifted()
    {
        var land = Land(3, T.AddHours(-5)) with
        {
            ShieldUntil = T.AddHours(7),
            SiegeUntil = T.AddHours(19),
            LossWindowSince = T.AddHours(-5),
            LossAttackers = AttackerSet.Of([Anna]),
        };

        var reset = SeasonReset.Soft(land, T, Rules);

        Assert.Equal(land with
        {
            Level = 1,
            LastVisitAt = T,
            ShieldUntil = null,
            SiegeUntil = null,
            LossWindowSince = null,
            LossAttackers = AttackerSet.Empty,
        }, reset);
    }

    [Fact]
    public void Recently_visited_land_keeps_one_full_decay_step()
    {
        // L3 с визитом вчера: до исчезновения было 17 дней — остаётся меньшее, 6 дней от начала сезона.
        var reset = SeasonReset.Soft(Land(3, T.AddDays(-1)), T, Rules);

        Assert.Equal(T, reset.LastVisitAt);
        Assert.Equal(1, Decay.EffectiveLevel(reset, T + D - TimeSpan.FromMilliseconds(1), Rules));
        Assert.Equal(0, Decay.EffectiveLevel(reset, T + D, Rules));
    }

    [Fact]
    public void Land_about_to_disappear_keeps_only_what_it_had_left()
    {
        // L2, визит 10 дней назад: действует L1, исчезнет через 2 дня — и после сброса тоже через 2 дня.
        var land = Land(2, T.AddDays(-10));

        var reset = SeasonReset.Soft(land, T, Rules);

        Assert.Equal(Decay.LostAt(land, Rules), Decay.LostAt(reset, Rules));
        Assert.Equal(T.AddDays(2), Decay.LostAt(reset, Rules));
    }

    [Fact]
    public void Level_one_land_keeps_its_last_visit()
    {
        var land = Land(1, T.AddDays(-3));

        Assert.Equal(land.LastVisitAt, SeasonReset.Soft(land, T, Rules).LastVisitAt);
    }

    [Fact]
    public void Ghost_disappears_exactly_when_it_would_have_without_the_reset()
    {
        var ghost = Land(2, T.AddDays(-13)); // угас сутки назад
        Assert.True(Decay.IsGhost(ghost, T, Rules));

        var reset = SeasonReset.Soft(ghost, T, Rules);

        Assert.Equal(Decay.LostAt(ghost, Rules), Decay.LostAt(reset, Rules));
        Assert.True(Decay.IsGhost(reset, T, Rules));
        Assert.Equal(Decay.IsGhost(ghost, T.AddDays(2), Rules), Decay.IsGhost(reset, T.AddDays(2), Rules));
    }

    [Fact]
    public void Visit_after_the_season_start_is_not_moved_back()
    {
        // Задача сброса опоздала, а игрок уже пробежал по участку в новом сезоне.
        var land = Land(3, T.AddHours(1));

        var reset = SeasonReset.Soft(land, T, Rules);

        Assert.Equal((1, T.AddHours(1)), (reset.Level, reset.LastVisitAt));
    }

    [Fact]
    public void Owner_contour_level_up_time_and_the_touch_are_kept()
    {
        var land = Land(3, T.AddDays(-1)) with { TouchedAt = T.AddDays(-2) };

        var reset = SeasonReset.Soft(land, T, Rules);

        Assert.Equal((land.OwnerId, land.LastLevelUpAt, land.TouchedAt), (reset.OwnerId, reset.LastLevelUpAt, reset.TouchedAt));
        Assert.True(reset.TouchedAt < T); // «касались в этом сезоне» сброс не даёт
    }

    [Fact]
    public void Reset_keeps_living_land_alive_for_the_lesser_of_one_step_and_what_was_left_and_twice_changes_nothing()
    {
        var states =
            from level in Gen.Int[1, 3]
            from visitedHours in Gen.Double[-600, 0]
            from levelledHours in Gen.Double[0, 100]
            from shielded in Gen.Bool
            select Land(level, T.AddHours(visitedHours)) with
            {
                LastLevelUpAt = T.AddHours(visitedHours - levelledHours),
                ShieldUntil = shielded ? T.AddHours(3) : null,
            };

        states.Sample(land =>
        {
            var reset = SeasonReset.Soft(land, T, Rules);
            var left = Decay.LostAt(land, Rules) - T;

            Assert.Equal(1, reset.Level);
            Assert.Equal(T + (left < D ? left : D), Decay.LostAt(reset, Rules));
            Assert.Equal(Decay.EffectiveLevel(land, T, Rules) > 0, Decay.EffectiveLevel(reset, T, Rules) > 0);
            Assert.Equal(reset, SeasonReset.Soft(reset, T, Rules));
        }, iter: 2_000);
    }

    // ── «Касались в этом сезоне» (§3.4): владелец взял участок или освежил его своим забегом ──

    [Fact]
    public void Taking_land_touches_it_at_the_time_of_the_loop()
    {
        var at = T.AddHours(3);

        var (neutral, _) = CaptureRules.Decide(null, new CaptureContext(Anna, at, new HashSet<Guid>()), Rules);
        var (transferred, outcome) = CaptureRules.Decide(Land(1, T.AddHours(-30)), new CaptureContext(Anna, at, new HashSet<Guid>()), Rules);

        Assert.Equal(at, neutral!.TouchedAt);
        Assert.Equal((PieceOutcome.Transferred, at), (outcome, transferred!.TouchedAt));
    }

    [Fact]
    public void Owners_own_loop_and_visit_touch_the_land_but_a_clan_mate_and_an_attacker_do_not()
    {
        var land = Land(2, T.AddDays(-2));
        var at = T.AddHours(3);

        var (refreshed, _) = CaptureRules.Decide(land, new CaptureContext(Owner, at, new HashSet<Guid>()), Rules);
        var visited = CaptureRules.Visit(land, at, Rules);
        var (forClanMate, clan) = CaptureRules.Decide(land, new CaptureContext(Anna, at, new HashSet<Guid> { Owner }), Rules);
        var (cracked, crack) = CaptureRules.Decide(land, new CaptureContext(Boris, at, new HashSet<Guid>()), Rules);

        Assert.Equal(at, refreshed!.TouchedAt);
        Assert.Equal(at, visited!.TouchedAt);
        Assert.Equal((PieceOutcome.RefreshedForClanMate, at, land.TouchedAt), (clan, forClanMate!.LastVisitAt, forClanMate.TouchedAt));
        Assert.Equal((PieceOutcome.Cracked, land.TouchedAt), (crack, cracked!.TouchedAt));
    }

    [Fact]
    public void Visit_from_before_a_last_visit_moved_by_the_reset_still_touches_the_land_and_is_traced()
    {
        // Забег кончился в 23:50, визиты посчитаны после полуночи смены сезона: «последний визит» сброс уже сдвинул на
        // полночь, а касание — время визита. Проекция и откат узнают такой визит по касанию (VisitReplay).
        // Уровень рос 2 часа назад — визит его не поднимает, и время визита не видно ни в повышении, ни в «последнем визите».
        var written = SeasonReset.Soft(Land(3, T.AddHours(-2)) with { LastLevelUpAt = T.AddHours(-2) }, T, Rules);
        var at = T.AddMinutes(-10);

        var current = CaptureRules.Visit(written, at, Rules)!;
        var trace = VisitReplay.Trace(written, current, Rules);

        Assert.Equal((1, T, at), (current.Level, current.LastVisitAt, current.TouchedAt));
        Assert.Equal([at], trace);
        Assert.Equal(current, VisitReplay.Apply(written, trace!, Rules));
    }
}
