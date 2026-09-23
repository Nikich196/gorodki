using Gorodki.Domain.Config;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Runs;

namespace Gorodki.Domain.Fog;

/// <summary>
/// Какие клетки тумана открывает забег (PLAN.md, §3.10): «из машины туман не открывается». Открывают только точки, которые
/// судья отрезков принял; полоса между соседними — если между ними нет разрыва и расстояние не больше порога лиги.
/// </summary>
/// <remarks>
/// Судья рвёт след с запаздыванием: «транспорт» — после 20 с в машине, «велосипед» — после 30 с. Поэтому перед разрывом
/// по правилам отрезков отбрасываются последние <see cref="BreakLookback"/>, а точки разрыва сами туман не открывают
/// (в машине судья рвёт след на каждой точке — иначе нарисовался бы пунктир). Телепорт — исключение: сам скачок и так
/// не рисуется, путь до него был честным.
/// </remarks>
public static class FogStamp
{
    /// <summary>Сколько пути перед разрывом не открывать: самое долгое правило отрезков — «велосипед», 30 с.</summary>
    public static readonly TimeSpan BreakLookback = TimeSpan.FromSeconds(30);

    public static FogLayer Of(IReadOnlyList<TrackPoint> points, IReadOnlyList<JudgeVerdict> verdicts, League league, ExplorationConfig rules)
    {
        var excluded = ExcludedPoints(points, verdicts);
        var layer = new FogLayer();
        var radius = rules.RevealRadiusMeters;
        var maxGap = rules.MaxGapMeters.For(league);
        TrackPoint? previous = null;
        for (var i = 0; i < points.Count; i++)
        {
            if (verdicts[i].Kind == VerdictKind.Ignored)
            {
                continue; // плохая точность — точка не в счёт, но полосу не рвёт
            }

            if (excluded[i])
            {
                previous = null; // разрыв: полосу через него не тянем
                continue;
            }

            var point = points[i];
            if (previous is { } from)
            {
                layer.RevealPath(from.Latitude, from.Longitude, point.Latitude, point.Longitude, radius, maxGap);
            }
            else
            {
                layer.RevealAround(point.Latitude, point.Longitude, radius);
            }

            previous = point;
        }

        return layer;
    }

    private static bool[] ExcludedPoints(IReadOnlyList<TrackPoint> points, IReadOnlyList<JudgeVerdict> verdicts)
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
}
