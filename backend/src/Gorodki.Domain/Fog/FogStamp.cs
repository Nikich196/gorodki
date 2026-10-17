using Gorodki.Domain.Config;
using Gorodki.Domain.Geo;
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

    public static FogLayer Of(IReadOnlyList<TrackPoint> points, IReadOnlyList<JudgeVerdict> verdicts, League league, ExplorationConfig rules) =>
        WithRadar(points, verdicts, league, rules, radarMeters: 0).Layer;

    /// <summary>
    /// То же с «Радаром» (Рюкзак, §3.11: «радиус открытия тумана ×2 на 3 км»): первые <paramref name="radarMeters"/>
    /// засчитанного пути открывают туман радиусом <see cref="ExplorationConfig.RevealRadiusMeters"/> ×
    /// <see cref="ExplorationConfig.RadarMultiplier"/>. Путь меряется так же, как полоса: между соседними засчитанными
    /// точками без разрыва; отрезок, начатый под «Радаром», открывается целиком с большим радиусом. Возвращает слой и
    /// сколько метров «Радара» забег израсходовал (не больше <paramref name="radarMeters"/>).
    /// </summary>
    public static (FogLayer Layer, double RadarUsedMeters) WithRadar(
        IReadOnlyList<TrackPoint> points, IReadOnlyList<JudgeVerdict> verdicts, League league, ExplorationConfig rules, double radarMeters)
    {
        var excluded = JudgedPath.Excluded(points, verdicts);
        var layer = new FogLayer();
        var maxGap = rules.MaxGapMeters.For(league);
        var walked = 0.0;
        double Radius() => walked < radarMeters ? rules.RevealRadiusMeters * rules.RadarMultiplier : rules.RevealRadiusMeters;
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
                layer.RevealPath(from.Latitude, from.Longitude, point.Latitude, point.Longitude, Radius(), maxGap);
                walked += Geodesy.Distance(from.Latitude, from.Longitude, point.Latitude, point.Longitude);
            }
            else
            {
                layer.RevealAround(point.Latitude, point.Longitude, Radius());
            }

            previous = point;
        }

        return (layer, Math.Min(walked, Math.Max(0, radarMeters)));
    }
}
