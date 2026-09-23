using Gorodki.Domain.Runs;

namespace Gorodki.Domain.Tests.Runs;

public sealed class TrackChunkRulesTests
{
    private const long Start = 1_758_600_000_000;

    private static ChunkLimits Limits(long start = Start, long? clientNow = null) =>
        new(start, clientNow ?? start + 3_600_000, MaxRunHours: 4, MaxSeq: 28_800, MaxPoints: 1_200, MaxSamples: 1_200);

    [Fact]
    public void Normal_chunk_passes()
    {
        Assert.Empty(TrackChunkRules.Check(Chunk(0, Start + 1_000, count: 60), Limits()));
    }

    [Fact]
    public void Phone_clock_three_hours_off_does_not_matter()
    {
        const long skew = -3 * 3_600_000;

        Assert.Empty(TrackChunkRules.Check(Chunk(0, Start + skew + 1_000, count: 60), Limits(Start + skew)));
    }

    [Fact]
    public void Points_long_before_the_start_or_after_4_hours_are_refused()
    {
        var early = TrackChunkRules.Check(Chunk(0, Start - 120_000, count: 1) with { SensorsCompleteThroughMs = Start }, Limits());
        var late = TrackChunkRules.Check(Chunk(0, Start + (4 * 3_600_000) + (11 * 60_000), count: 1), Limits(clientNow: Start + (5 * 3_600_000)));

        Assert.Equal(new ChunkProblem("points[0]", "time_window"), Assert.Single(early));
        Assert.Equal(new ChunkProblem("points[0]", "time_window"), Assert.Single(late));
    }

    [Fact]
    public void Points_from_the_future_by_the_same_clock_are_refused()
    {
        var problems = TrackChunkRules.Check(Chunk(0, Start + 1_000, count: 1), Limits(clientNow: Start + 1_000 - (3 * 60_000)));

        Assert.Contains(new ChunkProblem("points[0]", "time_future"), problems);
    }

    [Fact]
    public void Time_must_strictly_increase()
    {
        var chunk = Chunk(0, Start + 1_000, count: 3) with
        {
            Points =
            [
                Point(0, Start + 1_000),
                Point(1, Start + 1_000),
                Point(2, Start + 3_000),
            ],
        };

        Assert.Equal(new ChunkProblem("points[1]", "time_order"), Assert.Single(TrackChunkRules.Check(chunk, Limits())));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(28_801)]
    [InlineData(int.MaxValue)]
    public void Seq_outside_the_run_limit_is_refused_without_overflow(int firstSeq)
    {
        var chunk = new TrackChunk(firstSeq, Start, [Point(0, Start + 1_000)], [], []);

        Assert.Equal(new ChunkProblem("firstSeq", "seq_limit"), Assert.Single(TrackChunkRules.Check(chunk, Limits())));
    }

    [Fact]
    public void Chunk_that_runs_past_the_seq_limit_is_refused()
    {
        var problems = TrackChunkRules.Check(Chunk(28_790, Start + 1_000, count: 20), Limits());

        Assert.Contains(new ChunkProblem("points", "seq_limit"), problems);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_201)]
    public void Empty_or_huge_chunk_is_refused(int count)
    {
        var problems = TrackChunkRules.Check(Chunk(0, Start + 1_000, count), Limits(clientNow: Start + 4 * 3_600_000));

        Assert.Equal(new ChunkProblem("points", "count"), Assert.Single(problems));
    }

    [Fact]
    public void Broken_sensor_records_are_refused()
    {
        var chunk = Chunk(0, Start + 1_000, count: 1) with
        {
            Motion = [new MotionSample(Start + 1_000, (MotionActivity)42)],
            Steps = [new StepSample(Start + 5_000, Start + 4_000, 10), new StepSample(Start + 1_000, Start + 2_000, -3)],
        };

        var problems = TrackChunkRules.Check(chunk, Limits());

        Assert.Equal(
            new[] { new ChunkProblem("motion[0]", "activity"), new ChunkProblem("steps[0]", "steps_invalid"), new ChunkProblem("steps[1]", "steps_invalid") },
            problems);
    }

    [Fact]
    public void Unknown_step_count_is_fine()
    {
        var chunk = Chunk(0, Start + 1_000, count: 1) with { Steps = [new StepSample(Start + 1_000, Start + 2_000, null)] };

        Assert.Empty(TrackChunkRules.Check(chunk, Limits()));
    }

    [Fact]
    public void Sensor_mark_must_be_within_the_run()
    {
        var chunk = Chunk(0, Start + 1_000, count: 1) with { SensorsCompleteThroughMs = 0 };

        Assert.Equal(new ChunkProblem("sensorsCompleteThroughMs", "time_window"), Assert.Single(TrackChunkRules.Check(chunk, Limits())));
    }

    [Fact]
    public void Problem_list_is_capped()
    {
        var chunk = Chunk(0, Start - 3_600_000, count: 500);

        Assert.Equal(TrackChunkRules.MaxProblems, TrackChunkRules.Check(chunk, Limits()).Count);
    }

    private static TrackPoint Point(int seq, long timeMs) =>
        TrackPoint.FromMeasurements(seq, timeMs, 52.0976, 23.688, 5, 2.5, PointFlags.None);

    private static TrackChunk Chunk(int firstSeq, long firstTimeMs, int count) =>
        new(
            firstSeq,
            firstTimeMs,
            Enumerable.Range(0, count).Select(i => Point(firstSeq + i, firstTimeMs + (i * 1_000L))).ToArray(),
            [],
            []);
}
