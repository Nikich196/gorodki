using Gorodki.Domain.Config;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Territory;
using NetTopologySuite.Geometries;

namespace Gorodki.Domain.Scoring;

/// <summary>Очки за захват и зачётные сотки, по которым считаются ступени суток.</summary>
/// <param name="Points">Очки (целые).</param>
/// <param name="Basis">Зачётные сотки захвата после его ступеней — их сумма за сутки двигает ступени суток.</param>
public readonly record struct CaptureScore(int Points, double Basis);

/// <summary>Очки за дистанцию и засчитанные метры, по которым считается суточный потолок.</summary>
public readonly record struct DistanceScore(int Points, double CountedMeters);

/// <summary>
/// Очки сезона (PLAN.md, §3.5) — чистые функции: на входе итог захвата или путь забега, на выходе очки. Числа — раздел
/// <c>scoring</c> игрового конфига (<see cref="ScoringConfig"/>). Как это собрано вместе — docs/architecture/scoring-and-seasons.md.
/// </summary>
public static class Scores
{
    /// <summary>Единица площади — «сотка» (§3.5).</summary>
    public const double SquareMetersPerSotka = 100;

    /// <summary>
    /// Ступени с убывающей отдачей: сколько зачётных единиц дают <paramref name="amount"/> единиц, если каждая следующая
    /// ступень оплачивается по своей ставке. После последней ограниченной ступени (если у неё есть граница) — ноль.
    /// </summary>
    public static double Tiered(double amount, IReadOnlyList<ScoreTier> tiers)
    {
        var result = 0.0;
        var from = 0.0;
        foreach (var tier in tiers)
        {
            if (amount <= from)
            {
                break;
            }

            var to = tier.UpToSotki ?? double.PositiveInfinity;
            result += (Math.Min(amount, to) - from) * tier.Rate;
            from = to;
        }

        return result;
    }

    /// <summary>
    /// Взвешенная площадь захвата, м² (§3.5: «вражеская земля и снятый уровень дают бонус»): ничья земля — как есть, взятая
    /// чужая — с бонусом за вражескую землю, треснувшая чужая — только бонус за снятый уровень. Своя освежённая, под щитом,
    /// «спорная» и прочее, что захват не взял, очков не дают: освежение — это удержание (срез E7), а не захват.
    /// </summary>
    public static double ScoredSquareMeters(IReadOnlyDictionary<PieceOutcome, double> areaByOutcome, ScoringConfig config) =>
        areaByOutcome.GetValueOrDefault(PieceOutcome.ClaimedNeutral)
        + (areaByOutcome.GetValueOrDefault(PieceOutcome.Transferred) * (1 + config.EnemyLandBonus))
        + (areaByOutcome.GetValueOrDefault(PieceOutcome.Cracked) * config.LevelRemovedBonus);

    /// <summary>
    /// Очки за захват: взвешенные сотки → ступени захвата (зачётные сотки) → ступени суток (сколько добавил этот захват к
    /// уже зачтённому за сутки <paramref name="dayBasisBefore"/>) → × ценность земли → × очков за сотку. Сумма за сутки от
    /// порядка захватов не зависит: она — ступени суток от суммы зачётных соток.
    /// </summary>
    /// <param name="areaByOutcome">Площадь по видам последствий, м² (<see cref="CaptureResult.AreaByOutcome"/>).</param>
    /// <param name="dayBasisBefore">Зачётные сотки прежних захватов игрока в этой лиге за те же игровые сутки.</param>
    /// <param name="landValue">Средняя ценность земли захвата (<see cref="LandValue.Factor"/>).</param>
    public static CaptureScore ForCapture(
        IReadOnlyDictionary<PieceOutcome, double> areaByOutcome, double dayBasisBefore, double landValue, ScoringConfig config)
    {
        var basis = Tiered(ScoredSquareMeters(areaByOutcome, config) / SquareMetersPerSotka, config.CaptureTiers);
        var before = Math.Max(0, dayBasisBefore);
        var daily = Tiered(before + basis, config.DailyTiers) - Tiered(before, config.DailyTiers);
        return new CaptureScore(Round(daily * landValue * config.PointsPerSotka), basis);
    }

    /// <summary>
    /// Очки за дистанцию (§3.5: «+10 за км, ≤20 км в день; в вело-лиге ×0,33»): засчитывается путь до суточного потолка
    /// лиги с учётом уже засчитанных за эти сутки метров <paramref name="dayMetersBefore"/>.
    /// </summary>
    public static DistanceScore ForDistance(double meters, double dayMetersBefore, League league, ScoringConfig config)
    {
        var room = (config.DistanceDailyCapKm * 1000) - Math.Max(0, dayMetersBefore);
        var counted = Math.Max(0, Math.Min(meters, room));
        return new DistanceScore(Round(counted / 1000 * config.DistancePointsPerKm * config.DistanceFactor.For(league)), counted);
    }

    /// <summary>Ступени годятся для очков: границы растут, последняя — без границы, ставки не отрицательны и не растут.</summary>
    public static bool AreDiminishing(IReadOnlyList<ScoreTier> tiers) =>
        tiers.Count > 0
        && tiers[^1].UpToSotki is null
        && tiers.Take(tiers.Count - 1).All(t => t.UpToSotki > 0)
        && tiers.Zip(tiers.Skip(1)).All(pair => pair.Second.UpToSotki is null || pair.Second.UpToSotki > pair.First.UpToSotki)
        && tiers.All(t => t.Rate >= 0)
        && tiers.Zip(tiers.Skip(1)).All(pair => pair.Second.Rate <= pair.First.Rate);

    private static int Round(double points) => (int)Math.Round(points, MidpointRounding.AwayFromZero);
}

/// <summary>Ценность земли захвата (§3.5: «город 1,0; поле, лес, промзона 0,5; Арена ×2»).</summary>
public static class LandValue
{
    /// <summary>
    /// Средняя ценность земли внутри контура <paramref name="capture"/> — множитель очков захвата. <b>Точка расширения:</b>
    /// слой землепользования и граница Арены появятся в конвейере OSM (C6, osm-pipeline v1); до них вся земля — «город»
    /// (<see cref="LandValueConfig.City"/>, 1,0), и множитель у всех захватов одинаковый.
    /// </summary>
    public static double Factor(Geometry capture, LandValueConfig config)
    {
        _ = capture; // C6: здесь — средняя по площади ценность по слою землепользования и Арене
        return config.City;
    }
}
