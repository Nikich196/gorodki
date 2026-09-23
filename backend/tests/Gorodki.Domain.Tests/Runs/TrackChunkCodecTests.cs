using CsCheck;
using Gorodki.Domain.Runs;

namespace Gorodki.Domain.Tests.Runs;

public sealed class TrackChunkCodecTests
{
    private static readonly Gen<TrackChunk> ChunkGen =
        from firstSeq in Gen.Int[0, 1_000_000]
        from start in Gen.Long[1_700_000_000_000, 1_900_000_000_000]
        from sensorsMark in Gen.Long[0, 10_000_000]
        from raw in Gen.Select(
                Gen.Int[0, 5_000],
                Gen.Double[-90, 90],
                Gen.Double[-180, 180],
                Gen.Double[0, 7_000],
                Gen.Double[-1, 700].Nullable(),
                Gen.Byte[0, 3])
            .Array[0, 60]
        from motion in Gen.Select(Gen.Long[0, 10_000_000], Gen.Byte[0, 5]).Array[0, 20]
        from steps in Gen.Select(Gen.Long[0, 10_000_000], Gen.Long[0, 60_000], Gen.Int[-1, 500]).Array[0, 20]
        select new TrackChunk(
            firstSeq,
            start + sensorsMark,
            raw.Select((p, i) => TrackPoint.FromMeasurements(firstSeq + i, start + p.Item1, p.Item2, p.Item3, p.Item4, p.Item5, (PointFlags)p.Item6))
                .ToArray(),
            motion.Select(m => new MotionSample(start + m.Item1, (MotionActivity)m.Item2)).ToArray(),
            steps.Select(s => new StepSample(start + s.Item1, start + s.Item1 + s.Item2, s.Item3 < 0 ? null : s.Item3)).ToArray());

    [Fact]
    public void Any_chunk_reads_back_exactly()
    {
        ChunkGen.Sample(chunk =>
        {
            var restored = TrackChunkCodec.Decode(TrackChunkCodec.Encode(chunk));

            Assert.True(chunk.SameContentAs(restored));
        });
    }

    [Fact]
    public void Same_measurements_give_the_same_bytes_and_hash()
    {
        var a = Chunk(firstSeq: 10, (52.0976123456, 23.6880987654, 4.87, 2.915));
        var b = Chunk(firstSeq: 10, (52.0976123456, 23.6880987654, 4.87, 2.915));

        Assert.Equal(TrackChunkCodec.Hash(TrackChunkCodec.Encode(a)), TrackChunkCodec.Hash(TrackChunkCodec.Encode(b)));
    }

    [Fact]
    public void Same_points_at_another_place_in_the_run_give_another_hash()
    {
        var a = Chunk(firstSeq: 0, (52.1, 23.7, 5, null));
        var b = Chunk(firstSeq: 1, (52.1, 23.7, 5, null));

        Assert.NotEqual(TrackChunkCodec.Hash(TrackChunkCodec.Encode(a)), TrackChunkCodec.Hash(TrackChunkCodec.Encode(b)));
    }

    [Fact]
    public void Values_are_stored_with_1_cm_steps_and_unknown_speed_stays_unknown()
    {
        var point = Chunk(firstSeq: 0, (52.09761234567, 23.68809876543, 4.87, null)).Points[0];

        Assert.Equal(520976123, point.LatitudeE7);
        Assert.Equal(236880988, point.LongitudeE7);
        Assert.Equal(4.9, point.AccuracyMeters);
        Assert.Null(point.SpeedMetersPerSecond);
    }

    [Fact]
    public void Point_is_21_bytes()
    {
        var one = TrackChunkCodec.Encode(Chunk(firstSeq: 0, (52.1, 23.7, 5, 3)));
        var two = TrackChunkCodec.Encode(Chunk(firstSeq: 0, (52.1, 23.7, 5, 3), (52.1, 23.7, 5, 3)));

        Assert.Equal(21, two.Length - one.Length);
    }

    [Fact]
    public void Damaged_data_is_refused_not_misread()
    {
        var encoded = TrackChunkCodec.Encode(Chunk(firstSeq: 5, (52.1, 23.7, 5, 3), (52.2, 23.8, 6, null)));

        for (var length = 0; length < encoded.Length; length++)
        {
            Assert.Throws<FormatException>(() => TrackChunkCodec.Decode(encoded.AsSpan(0, length)));
        }

        Assert.Throws<FormatException>(() => TrackChunkCodec.Decode([.. encoded, 0]));
        var otherVersion = encoded.ToArray();
        otherVersion[0] = 2;
        Assert.Throws<FormatException>(() => TrackChunkCodec.Decode(otherVersion));
    }

    [Fact]
    public void Huge_count_in_damaged_data_does_not_allocate_memory()
    {
        byte[] data = [TrackChunkCodec.FormatVersion, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x7F];

        Assert.Throws<FormatException>(() => TrackChunkCodec.Decode(data));
    }

    [Fact]
    public void Points_must_go_in_a_row()
    {
        var gap = new TrackChunk(
            0,
            0,
            [TrackPoint.FromMeasurements(0, 0, 52, 23, 5, null, PointFlags.None), TrackPoint.FromMeasurements(2, 1, 52, 23, 5, null, PointFlags.None)],
            [],
            []);

        Assert.Throws<ArgumentException>(() => TrackChunkCodec.Encode(gap));
    }

    [Theory]
    [InlineData(double.NaN, 23.7, 5.0)]
    [InlineData(52.1, double.PositiveInfinity, 5.0)]
    [InlineData(52.1, 23.7, double.NaN)]
    [InlineData(91.0, 23.7, 5.0)]
    [InlineData(52.1, -181.0, 5.0)]
    [InlineData(52.1, 23.7, -1.0)]
    public void Impossible_measurements_are_refused(double latitude, double longitude, double accuracy)
    {
        Assert.ThrowsAny<ArgumentException>(() => TrackPoint.FromMeasurements(0, 0, latitude, longitude, accuracy, null, PointFlags.None));
    }

    private static TrackChunk Chunk(int firstSeq, params (double Lat, double Lon, double Acc, double? Speed)[] points) =>
        new(
            firstSeq,
            1_758_600_000_000,
            points.Select((p, i) => TrackPoint.FromMeasurements(firstSeq + i, 1_758_600_000_000 + (i * 1000), p.Lat, p.Lon, p.Acc, p.Speed, PointFlags.None))
                .ToArray(),
            [],
            []);
}
