using Gorodki.Domain.Time;
using Microsoft.Extensions.Time.Testing;

namespace Gorodki.Domain.Tests.Time;

public sealed class GameClockTests
{
    [Fact]
    public void Minsk_is_utc_plus_three()
    {
        var instant = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

        var minsk = GameClock.ToMinsk(instant);

        Assert.Equal(TimeSpan.FromHours(3), minsk.Offset);
        Assert.Equal(12, minsk.Hour);
    }

    [Theory]
    // Старт Сезона 0 — 16.11 в 00:00 по Минску, это 15.11 в 21:00 UTC.
    [InlineData("2026-11-15T20:59:59Z", "2026-11-15")]
    [InlineData("2026-11-15T21:00:00Z", "2026-11-16")]
    [InlineData("2026-11-16T20:59:59Z", "2026-11-16")]
    public void Game_day_changes_at_midnight_in_Minsk(string utc, string expectedDay)
    {
        var instant = DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(DateOnly.Parse(expectedDay, System.Globalization.CultureInfo.InvariantCulture), GameClock.GameDayOf(instant));
    }

    [Fact]
    public void Today_follows_the_injected_time_provider()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 12, 31, 20, 30, 0, TimeSpan.Zero));
        var clock = new GameClock(time);

        Assert.Equal(new DateOnly(2026, 12, 31), clock.Today);

        time.Advance(TimeSpan.FromMinutes(30)); // 21:00 UTC = полночь в Минске

        Assert.Equal(new DateOnly(2027, 1, 1), clock.Today);
    }

    [Fact]
    public void IsNow_checks_the_window_in_Minsk_time()
    {
        var night = new DailyWindow(new TimeOnly(23, 0), new TimeOnly(6, 0));
        // 20:30 UTC = 23:30 в Минске — уже ночь, хотя по UTC ещё вечер.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 20, 30, 0, TimeSpan.Zero));
        var clock = new GameClock(time);

        Assert.True(clock.IsNow(night));

        time.Advance(TimeSpan.FromHours(7)); // 06:30 в Минске

        Assert.False(clock.IsNow(night));
    }
}
