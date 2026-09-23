using Gorodki.Domain.Runs;

namespace Gorodki.Domain.Tests.Runs;

public sealed class SeqRangeTests
{
    [Fact]
    public void Adjacent_and_overlapping_ranges_merge()
    {
        var merged = SeqRange.Merge([new(100, 199), new(0, 99), new(250, 260), new(255, 300)]);

        Assert.Equal(new SeqRange[] { new(0, 199), new(250, 300) }, merged);
    }

    [Fact]
    public void Missing_ranges_are_the_holes_up_to_the_last_point()
    {
        var missing = SeqRange.Missing([new(10, 19), new(40, 49)], lastSeq: 59);

        Assert.Equal(new SeqRange[] { new(0, 9), new(20, 39), new(50, 59) }, missing);
    }

    [Fact]
    public void Nothing_is_missing_when_everything_arrived()
    {
        Assert.Empty(SeqRange.Missing([new(0, 99), new(100, 120)], lastSeq: 120));
    }

    [Fact]
    public void Run_without_points_misses_nothing()
    {
        Assert.Empty(SeqRange.Missing([], lastSeq: -1));
    }

    [Fact]
    public void Nothing_received_means_everything_is_missing()
    {
        Assert.Equal(new SeqRange[] { new(0, 5) }, SeqRange.Missing([], lastSeq: 5));
    }
}
