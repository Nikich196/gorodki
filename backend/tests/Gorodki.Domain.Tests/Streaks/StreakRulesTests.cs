using Gorodki.Domain.Streaks;
using Gorodki.Domain.Time;

namespace Gorodki.Domain.Tests.Streaks;

/// <summary>
/// Серия (PLAN.md, §3.7, §3.11): день серии — сутки по Минску с километром засчитанного пути, пропуск сжигает серию,
/// «Заморозка серии» спасает один пропуск.
/// </summary>
public sealed class StreakRulesTests
{
    /// <summary>«Сегодня» тестов — игровые сутки по Минску.</summary>
    private static readonly DateOnly Today = new(2026, 11, 20);

    /// <summary>Утро сегодняшних суток: сегодня ещё не бегали.</summary>
    private static readonly DateTimeOffset Now = At(0, 9);

    [Fact]
    public void Without_runs_there_is_no_streak()
    {
        var streak = StreakRules.Compute([], [], Now);

        Assert.Equal((0, false, 0, false), (streak.Days, streak.TodayCounted, streak.FreezesLeft, streak.FreezeActive));
        Assert.Empty(streak.FrozenDays);
    }

    [Fact]
    public void Days_in_a_row_up_to_yesterday_are_a_streak_and_today_is_not_missed_yet()
    {
        var streak = StreakRules.Compute(Runs(-3, -2, -1), [], Now);

        Assert.Equal((3, false), (streak.Days, streak.TodayCounted));
    }

    [Fact]
    public void A_counted_today_adds_to_the_streak()
    {
        var streak = StreakRules.Compute(Runs(-3, -2, -1, 0), [], Now);
        var first = StreakRules.Compute(Runs(0), [], Now);

        Assert.Equal((4, true), (streak.Days, streak.TodayCounted));
        Assert.Equal((1, true), (first.Days, first.TodayCounted));
    }

    [Fact]
    public void A_missed_day_burns_the_streak()
    {
        Assert.Equal(3, StreakRules.Compute(Runs(-5, -3, -2, -1), [], Now).Days); // −4 пропущен: серия — с −3
        Assert.Equal(0, StreakRules.Compute(Runs(-3, -2), [], Now).Days); // вчера пропущен — серия сгорела
        Assert.Equal(1, StreakRules.Compute(Runs(-3, -2, 0), [], Now).Days); // и начинается заново сегодня
    }

    [Fact]
    public void Days_change_at_midnight_in_Minsk_not_in_UTC()
    {
        // 23:59:59 по Минску (20:59:59 UTC) — ещё тот день, 00:00 по Минску (21:00 UTC предыдущей даты) — уже следующий.
        var lastSecond = new DateTimeOffset(2026, 11, 19, 20, 59, 59, TimeSpan.Zero);
        var midnight = new DateTimeOffset(2026, 11, 19, 21, 0, 0, TimeSpan.Zero);
        Assert.Equal((Today.AddDays(-1), Today), (GameClock.GameDayOf(lastSecond), GameClock.GameDayOf(midnight)));

        var late = StreakRules.Compute([new StreakRun(lastSecond, 2_000)], [], Now);
        var early = StreakRules.Compute([new StreakRun(midnight, 2_000)], [], Now);

        Assert.Equal((1, false), (late.Days, late.TodayCounted)); // вчерашний день
        Assert.Equal((1, true), (early.Days, early.TodayCounted)); // сегодняшний
    }

    [Fact]
    public void Yesterday_counts_until_midnight_and_burns_right_after_it()
    {
        // Бегал только позавчера: весь вчерашний день серия жива, в 00:00 по Минску — сгорает.
        var runs = Runs(-2);

        Assert.Equal(1, StreakRules.Compute(runs, [], At(-1, 23, 59, 59)).Days);
        Assert.Equal(0, StreakRules.Compute(runs, [], At(0, 0)).Days);
    }

    [Fact]
    public void Runs_of_a_day_add_up_to_a_kilometre()
    {
        Assert.Equal(1, StreakRules.Compute([Run(-1, 600), Run(-1, 400)], [], Now).Days); // ровно километр за сутки
        Assert.Equal(1, StreakRules.Compute([Run(-1, StreakRules.MinDayMeters)], [], Now).Days);
        Assert.Equal(0, StreakRules.Compute([Run(-1, 999.9)], [], Now).Days);
        Assert.Equal(0, StreakRules.Compute([Run(-1, 600), Run(-2, 600)], [], Now).Days); // километр за двое суток — не день
    }

    [Fact]
    public void Order_of_runs_does_not_matter()
    {
        var runs = Runs(-6, -5, -3, -2, -1, 0);
        var forward = StreakRules.Compute(runs, [At(-5, 10)], Now);
        var backward = StreakRules.Compute(runs.AsEnumerable().Reverse(), [At(-5, 10)], Now);

        Assert.Equal((forward.Days, forward.TodayCounted, forward.FreezesLeft), (backward.Days, backward.TodayCounted, backward.FreezesLeft));
        Assert.Equal(forward.FrozenDays, backward.FrozenDays);
    }

    [Fact]
    public void A_run_from_last_night_still_being_counted_does_not_burn_the_streak()
    {
        // Вчерашний забег ещё без пути (идёт через полночь или ждёт границы публичности): вчера ещё не пропуск.
        var waiting = StreakRules.Compute([.. Runs(-3, -2), Run(-1, null)], [], Now);
        var counted = StreakRules.Compute(Runs(-3, -2, -1), [], Now);

        Assert.Equal((2, false), (waiting.Days, waiting.TodayCounted)); // не 0 — и не 3, пока путь не посчитан
        Assert.Equal(3, counted.Days);
    }

    [Fact]
    public void Only_yesterday_waits_for_its_run_to_be_counted()
    {
        // Путь позавчерашнего забега так и не посчитан (забег без точек, ошибка): позавчера — пропуск, серия — с вчера.
        Assert.Equal(1, StreakRules.Compute([.. Runs(-4, -3, -1), Run(-2, null)], [], Now).Days);
        Assert.Equal(0, StreakRules.Compute([Run(0, null)], [], Now).Days); // сегодняшний — не засчитан, пока нет пути
    }

    [Fact]
    public void A_freeze_saves_one_missed_day_without_adding_it()
    {
        var streak = StreakRules.Compute(Runs(-4, -2, -1), [At(-4, 10)], Now);

        Assert.Equal(3, streak.Days); // −4, −2, −1: спасённый −3 не прибавляется
        Assert.Equal([Today.AddDays(-3)], streak.FrozenDays);
        Assert.Equal((0, false), (streak.FreezesLeft, streak.FreezeActive));
    }

    [Fact]
    public void One_freeze_saves_only_one_day_and_two_save_two()
    {
        var one = StreakRules.Compute(Runs(-5, -2, -1), [At(-5, 10)], Now);
        var two = StreakRules.Compute(Runs(-5, -2, -1), [At(-5, 10), At(-5, 11)], Now);

        Assert.Equal((2, 0), (one.Days, one.FreezesLeft)); // −4 спасён, −3 — пропуск: серия заново с −2
        Assert.Equal([Today.AddDays(-4)], one.FrozenDays);
        Assert.Equal((3, 0), (two.Days, two.FreezesLeft));
        Assert.Equal([Today.AddDays(-4), Today.AddDays(-3)], two.FrozenDays);
    }

    [Fact]
    public void A_freeze_activated_on_the_missed_day_saves_it_but_the_next_morning_is_too_late()
    {
        var sameEvening = StreakRules.Compute(Runs(-4, -3, -1), [At(-2, 23, 59)], Now);
        var nextMorning = StreakRules.Compute(Runs(-4, -3, -1), [At(-1, 0, 0, 1)], Now);

        Assert.Equal((3, 0), (sameEvening.Days, sameEvening.FreezesLeft));
        Assert.Equal((1, 1), (nextMorning.Days, nextMorning.FreezesLeft)); // −2 сгорел; заморозка ждёт следующего пропуска
        Assert.True(nextMorning.FreezeActive);
    }

    [Fact]
    public void A_freeze_waits_while_there_is_no_streak()
    {
        // Серия сгорела в −7, заморозка активирована в −6: пропуски −6 и −5 без серии её не тратят — она спасает −2.
        var streak = StreakRules.Compute(Runs(-8, -4, -3, -1), [At(-6, 12)], Now);

        Assert.Equal(3, streak.Days);
        Assert.Equal([Today.AddDays(-2)], streak.FrozenDays);
    }

    [Fact]
    public void Runs_and_freezes_from_the_future_are_ignored()
    {
        // Сутки забега — по часам сервера, «завтра» не бывает; но и чужая ошибка часов не должна ничего засчитать.
        var streak = StreakRules.Compute([Run(1, 2_000)], [At(0, 8), At(1, 8)], Now);

        Assert.Equal((0, false, 1), (streak.Days, streak.TodayCounted, streak.FreezesLeft));
    }

    [Fact]
    public void Today_and_last_night_still_being_counted_never_spend_a_freeze()
    {
        var today = StreakRules.Compute(Runs(-2, -1), [At(-1, 10)], Now);
        var waiting = StreakRules.Compute([.. Runs(-3, -2), Run(-1, null)], [At(-3, 10)], Now);
        var onlyFreeze = StreakRules.Compute([], [At(0, 8)], Now);

        Assert.Equal((2, true), (today.Days, today.FreezeActive));
        Assert.Equal((2, true), (waiting.Days, waiting.FreezeActive));
        Assert.Equal((0, true), (onlyFreeze.Days, onlyFreeze.FreezeActive));
    }

    [Fact]
    public void A_run_counted_later_gives_the_freeze_back()
    {
        // Забег из офлайна за −2 посчитан позже: серия пересчитывается по истории, заморозка на −2 больше не нужна.
        var before = StreakRules.Compute(Runs(-3, -1), [At(-3, 10)], Now);
        var after = StreakRules.Compute(Runs(-3, -2, -1), [At(-3, 10)], Now);

        Assert.Equal((2, 0), (before.Days, before.FreezesLeft));
        Assert.Equal((3, 1), (after.Days, after.FreezesLeft));
        Assert.Empty(after.FrozenDays);
    }

    // MARK: — вспомогательное

    /// <summary>Момент <paramref name="hour"/>:<paramref name="minute"/>:<paramref name="second"/> по Минску в сутки «сегодня + <paramref name="days"/>».</summary>
    private static DateTimeOffset At(int days, int hour, int minute = 0, int second = 0) =>
        SeasonCalendar.MinskMidnight(Today.AddDays(days)).Add(new TimeSpan(hour, minute, second));

    /// <summary>Забег в полдень суток «сегодня + <paramref name="days"/>».</summary>
    private static StreakRun Run(int days, double? meters) => new(At(days, 12), meters);

    /// <summary>По забегу на 2 км в каждые из суток.</summary>
    private static StreakRun[] Runs(params int[] days) => [.. days.Select(d => Run(d, 2_000))];
}
