using Gorodki.Domain.Time;

namespace Gorodki.Domain.Tests.Time;

/// <summary>Календарь сезонов (PLAN.md, §3.4): С0 16–29.11, С1 30.11–13.12, С2 14.12 → показ; смена — в полночь по Минску.</summary>
public sealed class SeasonCalendarTests
{
    private static readonly SeasonCalendar Plan = new(
    [
        new Season(0, "Сезон 0 (бета)", SeasonCalendar.MinskMidnight(new DateOnly(2026, 11, 16))),
        new Season(1, "Сезон 1", SeasonCalendar.MinskMidnight(new DateOnly(2026, 11, 30))),
        new Season(2, "Сезон 2", SeasonCalendar.MinskMidnight(new DateOnly(2026, 12, 14))),
    ]);

    [Fact]
    public void Minsk_midnight_is_21_00_utc_the_day_before()
    {
        var start = SeasonCalendar.MinskMidnight(new DateOnly(2026, 11, 16));

        Assert.Equal(new DateTimeOffset(2026, 11, 15, 21, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(TimeSpan.Zero, start.Offset); // в базу — только UTC
    }

    [Fact]
    public void Season_changes_exactly_at_minsk_midnight()
    {
        var c1 = SeasonCalendar.MinskMidnight(new DateOnly(2026, 11, 30));

        Assert.Null(Plan.At(SeasonCalendar.MinskMidnight(new DateOnly(2026, 11, 16)).AddTicks(-1))); // предсезонье
        Assert.Equal(0, Plan.At(SeasonCalendar.MinskMidnight(new DateOnly(2026, 11, 16)))?.Number);
        Assert.Equal(0, Plan.At(c1.AddTicks(-1))?.Number); // 29.11, 23:59:59.9999999 по Минску
        Assert.Equal(1, Plan.At(c1)?.Number);
        Assert.Equal(2, Plan.At(new DateTimeOffset(2026, 12, 29, 12, 0, 0, TimeSpan.Zero))?.Number); // показ
    }

    [Fact]
    public void Season_ends_when_the_next_one_starts()
    {
        Assert.Equal(SeasonCalendar.MinskMidnight(new DateOnly(2026, 11, 30)), Plan.EndOf(Plan.Seasons[0]));
        Assert.Null(Plan.EndOf(Plan.Seasons[2]));
    }

    [Fact]
    public void Calendar_refuses_seasons_out_of_order_or_not_at_midnight()
    {
        var midnight = SeasonCalendar.MinskMidnight(new DateOnly(2026, 11, 16));

        Assert.Throws<ArgumentException>(() => new SeasonCalendar([new Season(1, "С1", midnight)])); // не с нуля
        Assert.Throws<ArgumentException>(() => new SeasonCalendar([new Season(0, "С0", midnight.AddHours(3))])); // 03:00 по Минску
        Assert.Throws<ArgumentException>(() => new SeasonCalendar(
        [
            new Season(0, "С0", midnight),
            new Season(1, "С1", midnight.AddDays(-1)),
        ]));
    }

    [Fact]
    public void All_time_is_not_a_season_number()
    {
        Assert.DoesNotContain(Plan.Seasons, s => s.Number == SeasonCalendar.AllTime);
    }
}
