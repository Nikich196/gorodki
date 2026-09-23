namespace Gorodki.Domain.Runs;

/// <summary>
/// Путь, который судья отрезков признал пройденным «своими ногами» (PLAN.md, §3.2, §3.10): по нему открывается туман и
/// засчитываются визиты. Точки с плохой точностью пропускаются, но путь не рвут; разрыв по правилам отрезков рвёт путь.
/// </summary>
/// <remarks>
/// Судья рвёт след с запаздыванием: «транспорт» — после 20 с в машине, «велосипед» — после 30 с. Поэтому перед разрывом
/// отбрасываются последние <see cref="BreakLookback"/>, а точки разрыва сами не считаются (в машине судья рвёт след на
/// каждой точке). Телепорт — исключение: сам скачок и так не считается, путь до него был честным.
/// </remarks>
public static class JudgedPath
{
    /// <summary>Сколько пути перед разрывом не засчитывать: самое долгое правило отрезков — «велосипед», 30 с.</summary>
    public static readonly TimeSpan BreakLookback = TimeSpan.FromSeconds(30);

    /// <summary>Точки, которые путь не засчитывает (разрывы и последние 30 с перед ними).</summary>
    public static bool[] Excluded(IReadOnlyList<TrackPoint> points, IReadOnlyList<JudgeVerdict> verdicts)
    {
        var excluded = new bool[points.Count];
        var lookbackMs = (long)BreakLookback.TotalMilliseconds;
        for (var i = 0; i < points.Count; i++)
        {
            if (verdicts[i].Kind != VerdictKind.SegmentBroken)
            {
                continue;
            }

            excluded[i] = true;
            if (verdicts[i].Issue == TrackIssue.Teleport)
            {
                continue;
            }

            for (var j = i - 1; j >= 0 && points[i].TimeMs - points[j].TimeMs <= lookbackMs; j--)
            {
                excluded[j] = true;
            }
        }

        return excluded;
    }

    /// <summary>Засчитанные участки пути: пары соседних (без точек с плохой точностью) точек без разрыва между ними.</summary>
    public static IEnumerable<(TrackPoint From, TrackPoint To)> Segments(IReadOnlyList<TrackPoint> points, IReadOnlyList<JudgeVerdict> verdicts)
    {
        var excluded = Excluded(points, verdicts);
        TrackPoint? previous = null;
        for (var i = 0; i < points.Count; i++)
        {
            if (verdicts[i].Kind == VerdictKind.Ignored)
            {
                continue; // плохая точность — точка не в счёт, но путь не рвёт
            }

            if (excluded[i])
            {
                previous = null;
                continue;
            }

            if (previous is { } from)
            {
                yield return (from, points[i]);
            }

            previous = points[i];
        }
    }
}
