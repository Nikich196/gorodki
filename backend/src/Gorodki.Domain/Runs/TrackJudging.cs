using Gorodki.Domain.Leagues;

namespace Gorodki.Domain.Runs;

/// <summary>Как сервер прогоняет след через судью (PLAN.md, §3.9, слой 3 — повторная проверка).</summary>
public static class TrackJudging
{
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
