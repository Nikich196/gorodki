using Gorodki.Domain.Time;

namespace Gorodki.Domain.Tests.Time;

public sealed class DailyWindowTests
{
    private static readonly DailyWindow Night = new(new TimeOnly(23, 0), new TimeOnly(6, 0));
    private static readonly DailyWindow Lunch = new(new TimeOnly(12, 0), new TimeOnly(14, 0));

    [Theory]
    [InlineData(23, 0, true)]   // начало окна включительно
    [InlineData(2, 30, true)]   // после полуночи
    [InlineData(5, 59, true)]
    [InlineData(6, 0, false)]   // конец окна не включительно
    [InlineData(12, 0, false)]
    [InlineData(22, 59, false)]
    public void Window_across_midnight(int hour, int minute, bool expected)
    {
        Assert.Equal(expected, Night.Contains(new TimeOnly(hour, minute)));
    }

    [Theory]
    [InlineData(12, 0, true)]
    [InlineData(13, 59, true)]
    [InlineData(14, 0, false)]
    [InlineData(11, 59, false)]
    [InlineData(2, 0, false)]
    public void Window_within_one_day(int hour, int minute, bool expected)
    {
        Assert.Equal(expected, Lunch.Contains(new TimeOnly(hour, minute)));
    }
}
