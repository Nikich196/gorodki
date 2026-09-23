using Gorodki.Domain.Config;
using Gorodki.Domain.Runs;

namespace Gorodki.Domain.Tests.Runs;

/// <summary>То, чего нет в общих эталонах: проверки «вживую» и серверный порядок подачи датчиков.</summary>
public sealed class TrackJudgingTests
{
    private const long Start = 1_790_000_000_000;

    private static readonly Gorodki.Domain.Leagues.LeagueRules Run = GameConfig.Default.Leagues.Run;

    [Fact]
    public void Stale_and_backwards_points_are_ignored_without_breaking_the_segment()
    {
        var judge = new SegmentJudge(Run);
        var first = Point(0, Start, east: 0);

        Assert.Equal(JudgeVerdict.Accepted, judge.Judge(first, now: Start / 1000.0));
        Assert.Equal(JudgeVerdict.Ignored(TrackIssue.StaleFix), judge.Judge(Point(1, Start + 1_000, east: 2), now: (Start / 1000.0) + 16));
        Assert.Equal(JudgeVerdict.Ignored(TrackIssue.TimeWentBackwards), judge.Judge(Point(2, Start - 1_000, east: 4), now: Start / 1000.0));
        Assert.Equal(JudgeVerdict.Accepted, judge.Judge(Point(3, Start + 2_000, east: 4), now: (Start / 1000.0) + 2));
    }

    [Fact]
    public void Late_sensor_records_count_once_and_in_time_order()
    {
        // 60 с по 2 м/с; «транспорт» с 10-й секунды пришёл дважды и не по порядку (запоздал в следующем куске).
        var points = Enumerable.Range(0, 60).Select(i => Point(i, Start + (i * 1_000L), east: 2.0 * i)).ToArray();
        MotionSample[] motion =
        [
            new(Start + 40_000, MotionActivity.Automotive),
            new(Start + 10_000, MotionActivity.Automotive),
            new(Start + 10_000, MotionActivity.Automotive),
        ];

        var verdicts = TrackJudging.JudgeAll(Run, points, motion, []);

        // «Транспорт» 20 с подряд — разрыв на 30-й секунде, как если бы записи пришли вовремя.
        Assert.Equal(JudgeVerdict.Broken(TrackIssue.Vehicle), verdicts[30]);
        Assert.All(verdicts.Take(30), v => Assert.Equal(JudgeVerdict.Accepted, v));
    }

    [Fact]
    public void Sensor_records_only_append_they_cannot_rewrite_the_past()
    {
        // Кусок 0…29 объявил: датчики до 29-й секунды отправлены полностью. Кусок 30…59 «дослал» «транспорт» с 5-й секунды —
        // так нельзя переписать уже вынесенные вердикты: запись отброшена и посчитана.
        var first = new TrackChunk(0, Start + 29_000, Points(0, 30), [new(Start, MotionActivity.Walking)], []);
        var second = new TrackChunk(30, Start + 59_000, Points(30, 30), [new(Start + 5_000, MotionActivity.Automotive)], []);

        var judgement = TrackJudging.JudgeRun(Run, [first, second]);

        Assert.Equal(1, judgement.LateSensorRecords);
        Assert.Equal(60, judgement.Points.Count);
        Assert.All(judgement.Verdicts, v => Assert.Equal(JudgeVerdict.Accepted, v));
    }

    [Fact]
    public void Newcomer_run_is_judged_with_the_newcomer_accuracy()
    {
        var config = GameConfig.Default;

        Assert.Equal(35, config.JudgeRulesFor(Gorodki.Domain.Leagues.League.Run, newcomer: true).MaxAccuracyMeters);
        Assert.Equal(25, config.JudgeRulesFor(Gorodki.Domain.Leagues.League.Run, newcomer: false).MaxAccuracyMeters);
    }

    [Fact]
    public void Verdicts_are_written_like_the_phone_writes_them()
    {
        Assert.Equal("accepted", JudgeVerdict.Accepted.ToString());
        Assert.Equal("ignored:poorAccuracy", JudgeVerdict.Ignored(TrackIssue.PoorAccuracy).ToString());
        Assert.Equal("broken:strideOutOfRange", JudgeVerdict.Broken(TrackIssue.StrideOutOfRange).ToString());
    }

    private static TrackPoint[] Points(int firstSeq, int count) =>
        Enumerable.Range(firstSeq, count).Select(i => Point(i, Start + (i * 1_000L), east: 2.0 * i)).ToArray();

    /// <summary>Точка в <paramref name="east"/> метрах к востоку от центра Бреста (градус долготы здесь ≈ 68,4 км).</summary>
    private static TrackPoint Point(int seq, long timeMs, double east) =>
        TrackPoint.FromMeasurements(seq, timeMs, 52.0976, 23.688 + (east / 68_365.0), 5, null, PointFlags.None);
}
