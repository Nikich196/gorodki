using Gorodki.Domain.Config;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Runs;

namespace Gorodki.Domain.Fog;

/// <summary>
/// Какие клетки тумана открывает забег (PLAN.md, §3.10): «из машины туман не открывается». Открывают только точки, которые
/// судья отрезков принял; полоса между соседними — если между ними нет разрыва и расстояние не больше порога лиги.
/// </summary>
/// <remarks>Засчитанный путь и его разрывы — <see cref="JudgedPath"/> (общий с визитами).</remarks>
public static class FogStamp
{
    /// <summary>Сколько пути перед разрывом не открывать (<see cref="JudgedPath.BreakLookback"/>).</summary>
    public static readonly TimeSpan BreakLookback = JudgedPath.BreakLookback;

    public static FogLayer Of(IReadOnlyList<TrackPoint> points, IReadOnlyList<JudgeVerdict> verdicts, League league, ExplorationConfig rules)
    {
        var excluded = JudgedPath.Excluded(points, verdicts);
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
}
