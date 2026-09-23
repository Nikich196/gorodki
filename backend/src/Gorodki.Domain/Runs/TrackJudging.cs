using Gorodki.Domain.Leagues;

namespace Gorodki.Domain.Runs;

/// <summary>Как сервер прогоняет след через судью (PLAN.md, §3.9, слой 3 — повторная проверка).</summary>
public static class TrackJudging
{
    /// <summary>Итог судейства забега.</summary>
    /// <param name="Points">Точки по порядку номеров.</param>
    /// <param name="Verdicts">Вердикт каждой точки.</param>
    /// <param name="LateSensorRecords">
    /// Сколько записей датчиков отброшено: они пришли в куске позже, хотя более ранний кусок уже объявил, что всё до этого
    /// момента отправлено. Честный телефон так не делает — это признак для оценки доверия.
    /// </param>
    public sealed record RunJudgement(IReadOnlyList<TrackPoint> Points, IReadOnlyList<JudgeVerdict> Verdicts, int LateSensorRecords);

    /// <summary>
    /// Судит забег по кускам, идущим подряд по номерам. Записи датчиков только дописываются: запись со временем не позже
    /// отметки полноты какого-либо более раннего куска отбрасывается. Иначе можно было бы задним числом «дослать»
    /// датчики и поменять уже вынесенные вердикты.
    /// </summary>
    public static RunJudgement JudgeRun(LeagueRules rules, IReadOnlyList<TrackChunk> chunksInOrder)
    {
        var points = new List<TrackPoint>();
        var motion = new List<MotionSample>();
        var steps = new List<StepSample>();
        var completeThrough = long.MinValue;
        var late = 0;
        foreach (var chunk in chunksInOrder)
        {
            points.AddRange(chunk.Points);
            foreach (var sample in chunk.Motion)
            {
                if (sample.TimeMs > completeThrough)
                {
                    motion.Add(sample);
                }
                else
                {
                    late++;
                }
            }

            foreach (var sample in chunk.Steps)
            {
                if (sample.EndMs > completeThrough)
                {
                    steps.Add(sample);
                }
                else
                {
                    late++;
                }
            }

            completeThrough = Math.Max(completeThrough, chunk.SensorsCompleteThroughMs);
        }

        return new RunJudgement(points, JudgeAll(rules, points, motion, steps), late);
    }

    /// <summary>
    /// Вердикт каждой точке по порядку. Перед точкой судья получает все записи движения не позже её времени и все записи
    /// шагомера, закончившиеся не позже; «сейчас» — время самой точки (свежесть проверяет телефон: устаревшие точки
    /// он не нумерует). Записи датчиков сортируются и очищаются от повторов: запоздавшие приходят со следующими кусками.
    /// </summary>
    public static IReadOnlyList<JudgeVerdict> JudgeAll(
        LeagueRules rules, IReadOnlyList<TrackPoint> points, IEnumerable<MotionSample> motion, IEnumerable<StepSample> steps)
    {
        var judge = new SegmentJudge(rules);
        var motionQueue = new Queue<MotionSample>(motion.Distinct().OrderBy(m => m.TimeMs));
        var stepQueue = new Queue<StepSample>(steps.Distinct().OrderBy(s => s.EndMs).ThenBy(s => s.StartMs));
        var verdicts = new JudgeVerdict[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            while (motionQueue.TryPeek(out var sample) && sample.TimeMs <= point.TimeMs)
            {
                judge.Record(motionQueue.Dequeue());
            }

            while (stepQueue.TryPeek(out var sample) && sample.EndMs <= point.TimeMs)
            {
                judge.Record(stepQueue.Dequeue());
            }

            verdicts[i] = judge.Judge(point, now: point.TimeMs / 1000.0);
        }

        return verdicts;
    }
}
