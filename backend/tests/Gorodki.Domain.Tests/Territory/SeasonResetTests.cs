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

    // ── Петля или визит до полуночи, применённые после смены сезона (SeasonReset.Late): как «событие, потом сброс» ──

    [Fact]
    public void Late_visit_does_not_raise_the_level_of_reset_land()
    {
        // L1, взятый 30 ч назад; забег кончился в 23:50, визиты посчитаны после задачи смены сезона. Сброшенная земля — та же
        // L1 с давним повышением: визит поднял бы её до L2 (12 дней жизни и трещина вместо перехода), а в порядке «визит,
        // потом сброс» подъём снял бы сброс: L1, «последний визит» — полночь.
        var before = Land(1, T.AddHours(-30));
        var at = T.AddMinutes(-10);
        var reset = SeasonReset.Soft(before, T, Rules);

        var late = SeasonReset.Late(reset, CaptureRules.Visit(reset, at, Rules), at, T, Rules);

        Assert.Equal(2, CaptureRules.Visit(reset, at, Rules)!.Level);
        Assert.Equal(SeasonReset.Soft(CaptureRules.Visit(before, at, Rules)!, T, Rules), late);
        Assert.Equal((1, T, at, at), (late!.Level, late.LastVisitAt, late.LastLevelUpAt, late.TouchedAt));
    }

    [Fact]
    public void Late_visit_keeps_what_the_new_season_already_gave_the_land()
    {
        // После полуночи владелец поднял уровень своей петлёй; визит забега, кончившегося до полуночи, посчитан позже —
        // он ничего не меняет, и второго сброса нет: L2 нового сезона остаётся.
        var reset = SeasonReset.Soft(Land(1, T.AddHours(-30)), T, Rules);
        var (levelled, outcome) = CaptureRules.Decide(reset, new CaptureContext(Owner, T.AddHours(2), new HashSet<Guid>(), ResetSeasonStart: T), Rules);
        var at = T.AddMinutes(-10);

        var late = SeasonReset.Late(levelled, CaptureRules.Visit(levelled!, at, Rules), at, T, Rules);

        Assert.Equal((PieceOutcome.Refreshed, 2), (outcome, levelled!.Level));
        Assert.Equal(levelled, late);
    }

    [Fact]
    public void Late_loops_leave_land_as_if_they_came_before_the_change()
    {
        var before = Land(1, T.AddHours(-30));
        var reset = SeasonReset.Soft(before, T, Rules);
        var at = T.AddMinutes(-10);
        var none = new HashSet<Guid>();

        // Своя петля в 23:50 — освежение без подъёма уровня в новый сезон.
        var (own, ownOutcome) = CaptureRules.Decide(reset, new CaptureContext(Owner, at, none, ResetSeasonStart: T), Rules);
        var (ownIdeal, _) = CaptureRules.Decide(before, new CaptureContext(Owner, at, none), Rules);
        Assert.Equal((PieceOutcome.Refreshed, SeasonReset.Soft(ownIdeal!, T, Rules)), (ownOutcome, own));
        Assert.Equal(1, own!.Level);

        // Чужая петля в 23:50 взяла L1 — без щита в новый сезон.
        var (taken, takenOutcome) = CaptureRules.Decide(reset, new CaptureContext(Boris, at, none, ResetSeasonStart: T), Rules);
        var (takenIdeal, _) = CaptureRules.Decide(before, new CaptureContext(Boris, at, none), Rules);
        Assert.Equal((PieceOutcome.Transferred, SeasonReset.Soft(takenIdeal!, T, Rules)), (takenOutcome, taken));
        Assert.Equal((Boris, 1, (DateTimeOffset?)null, at), (taken!.OwnerId, taken.Level, taken.ShieldUntil, taken.TouchedAt));
        Assert.NotNull(CaptureRules.Decide(reset, new CaptureContext(Boris, at, none), Rules).State!.ShieldUntil); // без правила — щит

        // Ничья земля — как обычно.
        var (neutral, _) = CaptureRules.Decide(null, new CaptureContext(Boris, at, none, ResetSeasonStart: T), Rules);
        Assert.Equal(CaptureRules.Decide(null, new CaptureContext(Boris, at, none), Rules).State, neutral);
    }

    [Fact]
    public void Late_event_resets_what_it_changes_and_never_touches_what_the_new_season_gave()
    {
        // Любая земля до полуночи → сброс → одно событие нового сезона (или ни одного) → опоздавшее событие до полуночи:
        // визит владельца, его петля, петля соклановца или чужая. Изменённый кусок — сброшенный (L1, без щита и осады);
        // кусок с уровнем или щитом нового сезона опоздавшее событие не меняет вовсе; без событий нового сезона и без щита
        // «до» — владелец и уровень те же, что в порядке «событие, потом сброс».
        var vera = new Guid("00000000-0000-0000-0000-0000000000cc");
        var cases =
            from level in Gen.Int[1, 3]
            from visitedHours in Gen.Double[-200, -0.5]
            from levelledHours in Gen.Double[0, 60]
            from shieldHours in Gen.Double[-12, 12]
            from besieged in Gen.Bool
            from newSeason in Gen.Int[0, 4]
            from newSeasonHours in Gen.Double[0.01, 30]
            from late in Gen.Int[0, 3]
            from lateMinutes in Gen.Double[0.5, 180]
            select (level, visitedHours, levelledHours, shieldHours, besieged, newSeason, newSeasonHours, late, lateMinutes);

        cases.Sample(c =>
        {
            var before = Land(c.level, T.AddHours(c.visitedHours)) with
            {
                LastLevelUpAt = T.AddHours(c.visitedHours - c.levelledHours),
                ShieldUntil = c.shieldHours > 0 ? T.AddHours(c.shieldHours) : null,
                SiegeUntil = c.besieged ? T.AddHours(5) : null,
            };
            var reset = SeasonReset.Soft(before, T, Rules);

            // Событие нового сезона: визит владельца, его петля, петля соклановца, чужая петля.
            var t = T.AddHours(c.newSeasonHours);
            var current = c.newSeason switch
            {
                1 => CaptureRules.Visit(reset, t, Rules) ?? reset,
                2 => Loop(reset, Owner, t, []),
                3 => Loop(reset, Anna, t, [Owner]),
                4 => Loop(reset, Boris, t, []),
                _ => reset,
            };

            // Опоздавшее событие до полуночи.
            var at = T.AddMinutes(-c.lateMinutes);
            var after = c.late switch
            {
                0 => SeasonReset.Late(current, CaptureRules.Visit(current, at, Rules), at, T, Rules),
                1 => Loop(current, current.OwnerId, at, []),
                2 => Loop(current, Anna == current.OwnerId ? Boris : Anna, at, [current.OwnerId]),
                _ => Loop(current, vera, at, []),
            };

            if (after is not null && after != current)
            {
                Assert.Equal((1, (DateTimeOffset?)null, (DateTimeOffset?)null), (after.Level, after.ShieldUntil, after.SiegeUntil));
            }

            if (current.Level > 1 || current.ShieldUntil > T || current.SiegeUntil > T)
            {
                Assert.Equal(current, after);
            }

            if (c.newSeason == 0 && before.ShieldUntil is null && before.SiegeUntil is null && after is not null)
            {
                var ideal = c.late switch
                {
                    0 => CaptureRules.Visit(before, at, Rules),
                    1 => CaptureRules.Decide(before, new CaptureContext(before.OwnerId, at, new HashSet<Guid>()), Rules).State,
                    2 => CaptureRules.Decide(before, new CaptureContext(Anna, at, new HashSet<Guid> { before.OwnerId }), Rules).State,
                    _ => CaptureRules.Decide(before, new CaptureContext(vera, at, new HashSet<Guid>()), Rules).State,
                };
                var idealReset = SeasonReset.Soft(ideal ?? before, T, Rules);
                Assert.Equal((idealReset.OwnerId, idealReset.Level), (after.OwnerId, after.Level));
            }
        }, iter: 5_000);

        static ParcelState Loop(ParcelState land, Guid who, DateTimeOffset at, Guid[] clanMates) =>
            CaptureRules.Decide(land, new CaptureContext(who, at, clanMates.ToHashSet(), ResetSeasonStart: T), Rules).State!;
    }

    [Fact]
    public void Loop_after_the_season_start_and_land_the_late_loop_does_not_change_are_not_reset_again()
    {
        var reset = SeasonReset.Soft(Land(1, T.AddHours(-30)), T, Rules);
        var none = new HashSet<Guid>();

        // Петля уже в новом сезоне — обычная: щит у взятой земли остаётся.
        var after = T.AddHours(1);
        var (taken, _) = CaptureRules.Decide(reset, new CaptureContext(Boris, after, none, ResetSeasonStart: T), Rules);
        Assert.Equal(after + Rules.TransferShield, taken!.ShieldUntil);

        // Опоздавшая петля по земле, которой уже коснулись в новом сезоне (L2 после полуночи), её не трогает: не
        // сбрасывает второй раз ни уровень, ни щит.
        var (levelled, _) = CaptureRules.Decide(reset, new CaptureContext(Owner, T.AddHours(2), none, ResetSeasonStart: T), Rules);
        var at = T.AddMinutes(-10);
        Assert.Equal((levelled, PieceOutcome.Superseded), CaptureRules.Decide(levelled, new CaptureContext(Boris, at, none, ResetSeasonStart: T), Rules));
        Assert.Equal(levelled, CaptureRules.Decide(levelled, new CaptureContext(Owner, at, none, ResetSeasonStart: T), Rules).State);
        Assert.Equal(taken, CaptureRules.Decide(taken, new CaptureContext(Boris, at, none, ResetSeasonStart: T), Rules).State);
    }
}
