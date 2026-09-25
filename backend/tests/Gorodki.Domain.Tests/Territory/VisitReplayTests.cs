using CsCheck;
using Gorodki.Domain.Territory;

namespace Gorodki.Domain.Tests.Territory;

/// <summary>
/// Визиты после захвата (<see cref="VisitReplay"/>): какие визиты перевели записанное захватом состояние в текущее и как
/// те же визиты ложатся на землю «до» захвата — по правилам <see cref="CaptureRules.Visit"/>, а не копией итога.
/// </summary>
public sealed class VisitReplayTests
{
    private static readonly Guid Anna = new("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Boris = new("00000000-0000-0000-0000-00000000000b");
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly TerritoryRules Rules = new();

    private static ParcelState Land(int level, DateTimeOffset visited, DateTimeOffset levelled) => new()
    {
        OwnerId = Anna,
        Level = level,
        LastVisitAt = visited,
        LastLevelUpAt = levelled,
    };

    private static ParcelState Visit(ParcelState state, DateTimeOffset at) => CaptureRules.Visit(state, at, Rules)!;

    [Fact]
    public void Unchanged_land_has_an_empty_trace()
    {
        var written = Land(2, T0, T0.AddHours(-3));

        var trace = VisitReplay.Trace(written, written, Rules);

        Assert.NotNull(trace);
        Assert.Empty(trace);
    }

    [Fact]
    public void Visit_that_only_refreshes_the_land_is_traced_by_its_time()
    {
        var written = Land(1, T0, T0);
        var current = Visit(written, T0.AddMinutes(10));

        var trace = VisitReplay.Trace(written, current, Rules);

        Assert.Equal([T0.AddMinutes(10)], trace);
        Assert.Equal(current, VisitReplay.Apply(written, trace!, Rules));
    }

    [Fact]
    public void Visit_that_levels_up_is_traced_by_its_time()
    {
        var written = Land(2, T0, T0.AddHours(-21));
        var current = Visit(written, T0.AddMinutes(10));
        Assert.Equal((3, T0.AddMinutes(10)), (current.Level, current.LastLevelUpAt));

        var trace = VisitReplay.Trace(written, current, Rules);

        Assert.Equal([T0.AddMinutes(10)], trace);
        Assert.Equal(current, VisitReplay.Apply(written, trace!, Rules));
    }

    [Fact]
    public void Level_up_and_a_later_visit_are_both_traced()
    {
        // Два забега в окне: первый поднял уровень, второй только освежил.
        var written = Land(1, T0, T0.AddHours(-21));
        var current = Visit(Visit(written, T0.AddMinutes(5)), T0.AddMinutes(15));

        var trace = VisitReplay.Trace(written, current, Rules);

        Assert.Equal([T0.AddMinutes(5), T0.AddMinutes(15)], trace);
        Assert.Equal(current, VisitReplay.Apply(written, trace!, Rules));
    }

    [Fact]
    public void Visit_from_before_the_last_visit_is_traced_by_its_level_up()
    {
        // Забег пришёл с опозданием: время визита раньше последнего, но уровень он поднял (прошло 20 ч с повышения).
        var written = Land(1, T0, T0.AddHours(-21));
        var current = Visit(written, T0.AddMinutes(-30));
        Assert.Equal((2, T0, T0.AddMinutes(-30)), (current.Level, current.LastVisitAt, current.LastLevelUpAt));

        var trace = VisitReplay.Trace(written, current, Rules);

        Assert.Equal([T0.AddMinutes(-30)], trace);
        Assert.Equal(current, VisitReplay.Apply(written, trace!, Rules));
    }

    [Fact]
    public void Visit_across_a_decay_step_is_replayed_with_the_decay_of_the_land_it_lands_on()
    {
        // Записанный кусок (L2) к визиту угас до L1 и вырос обратно до L2. Прежняя земля (L3, другие часы угасания)
        // от того же визита — L2 + 1 = L3: визит считается заново, итог не копируется.
        var written = Land(2, T0, T0);
        var at = T0.AddDays(6).AddHours(1);
        var current = Visit(written, at);
        Assert.Equal(2, current.Level);
        var before = Land(3, T0.AddDays(-2), T0.AddDays(-2));

        var trace = VisitReplay.Trace(written, current, Rules);

        Assert.Equal([at], trace);
        Assert.Equal(before with { Level = 3, LastVisitAt = at, LastLevelUpAt = at }, VisitReplay.Apply(before, trace!, Rules));
    }

    [Fact]
    public void Visit_to_a_besieged_part_is_replayed_as_a_level_up_on_the_land_before_the_crack()
    {
        // Треснувшая часть в осаде: визит её только освежает. На целом куске до трещины тот же визит поднял бы уровень —
        // так и должно выйти в проекции, иначе она показала бы то, чего без захвата не было бы.
        var before = Land(2, T0.AddHours(-1), T0.AddHours(-30));
        var cracked = before with
        {
            Level = 1,
            SiegeUntil = T0.AddHours(23),
            LossWindowSince = T0.AddHours(-1),
            LossAttackers = AttackerSet.Of([Boris]),
        };
        var current = Visit(cracked, T0);
        Assert.Equal((1, T0), (current.Level, current.LastVisitAt));

        var trace = VisitReplay.Trace(cracked, current, Rules);

        Assert.Equal([T0], trace);
        Assert.Equal(before with { Level = 3, LastVisitAt = T0, LastLevelUpAt = T0 }, VisitReplay.Apply(before, trace!, Rules));
    }

    [Fact]
    public void Changes_that_no_visit_makes_have_no_trace()
    {
        var written = Land(2, T0, T0.AddHours(-3)) with { LossWindowSince = T0.AddHours(-1), LossAttackers = AttackerSet.Of([Boris]) };
        var visited = Visit(written, T0.AddMinutes(10));
        (string Name, ParcelState Current)[] changes =
        [
            ("другой владелец", visited with { OwnerId = Boris }),
            ("щит", visited with { ShieldUntil = T0.AddHours(12) }),
            ("окно снятия уровней", visited with { LossWindowSince = T0.AddMinutes(10) }),
            ("кто снимал уровни", visited with { LossAttackers = AttackerSet.Empty }),
            ("уровень упал без угасания", written with { Level = 1 }),
            ("визит назад во времени", written with { LastVisitAt = T0.AddMinutes(-10) }),
            ("повышение без визита", written with { Level = 3 }),
        ];

        foreach (var (name, current) in changes)
        {
            Assert.True(VisitReplay.Trace(written, current, Rules) is null, name);
        }
    }

    [Fact]
    public void Siege_that_changed_is_not_explained_by_a_visit()
    {
        // Вторая трещина за окном лимита невозможна, но осаду может сдвинуть и то, чего здесь нет (новые правила) —
        // любое изменение осады означает, что земля изменилась не только визитом.
        var written = Land(1, T0, T0.AddHours(-3)) with { SiegeUntil = T0.AddHours(20) };
        var current = Visit(written, T0.AddMinutes(10)) with { SiegeUntil = T0.AddHours(24) };

        Assert.Null(VisitReplay.Trace(written, current, Rules));
        Assert.Null(VisitReplay.Trace(written, written with { SiegeUntil = null }, Rules));
    }

    [Fact]
    public void Land_that_decayed_to_nothing_is_not_visited()
    {
        // Угасшую до нуля землю визит не возвращает (CaptureRules.Visit даёт null) — сервер её не трогает, и перенос тоже.
        var written = Land(1, T0, T0);
        var lost = T0.AddDays(7);

        Assert.Null(VisitReplay.Trace(written, written with { LastVisitAt = lost }, Rules));
        Assert.Equal(written, VisitReplay.Apply(written, [lost], Rules));
    }

    [Fact]
    public void Visits_are_replayed_in_ascending_order()
    {
        // Порядок важен: раньше — повышение, позже — только освежение. В обратном порядке повышение пришлось бы на позднее время.
        var before = Land(1, T0, T0.AddHours(-21));
        var expected = before with { Level = 2, LastLevelUpAt = T0.AddMinutes(5), LastVisitAt = T0.AddMinutes(15) };

        Assert.Equal(expected, VisitReplay.Apply(before, [T0.AddMinutes(15), T0.AddMinutes(5)], Rules));
        Assert.Equal(expected, VisitReplay.Apply(before, [T0.AddMinutes(5), T0.AddMinutes(15)], Rules));
    }

    [Fact]
    public void Any_single_visit_is_traced_and_replayed_exactly()
    {
        var states =
            from level in Gen.Int[1, 3]
            from visitedHours in Gen.Double[-200, 0]
            from levelledHours in Gen.Double[0, 300]
            from besieged in Gen.Bool
            from siegeHours in Gen.Double[-24, 24]
            from visitMinutes in Gen.Double[-600, 600]
            select (
                State: Land(level, T0.AddHours(visitedHours), T0.AddHours(visitedHours - levelledHours)) with
                {
                    SiegeUntil = besieged ? T0.AddHours(siegeHours) : null,
                },
                At: T0.AddMinutes(visitMinutes));

        states.Sample(sample =>
        {
            var (written, at) = sample;
            if (CaptureRules.Visit(written, at, Rules) is not { } current || current == written)
            {
                return; // сервер такой визит не записывает
            }

            var trace = VisitReplay.Trace(written, current, Rules);

            Assert.NotNull(trace);
            Assert.Equal(current, VisitReplay.Apply(written, trace, Rules));
        }, iter: 2_000);
    }
}
