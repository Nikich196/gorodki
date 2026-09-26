using Gorodki.Domain.Leagues;

namespace Gorodki.Domain.Config;

/// <summary>
/// Очки сезона (PLAN.md, §3.5: «баланс „пешеход против бегуна ≤3×“»). Из плана — сотка, ценность земли и дистанция
/// (+10 за км, до 20 км в сутки, «Вело» ×0,33). Ступени, бонусы и очки за сотку план числом не называет: это
/// <b>предварительные</b> значения, их уточнят симуляция 20 ботов на 30 дней (E23) и полевой тест №1; новые числа — новая
/// версия конфига. «Гроза гигантов» и штраф за травлю в Сезоне 0 не действуют («ничего не усложнять»).
/// Как считается — docs/architecture/scoring-and-seasons.md.
/// </summary>
public sealed record ScoringConfig
{
    /// <summary>Очков за зачётную сотку (100 м²) на полной ступени суток. <b>Предварительно.</b></summary>
    public double PointsPerSotka { get; init; } = 1;

    /// <summary>
    /// Ступени одного захвата (§3.5, «на захват … с убывающей отдачей»): взвешенные сотки захвата → зачётные.
    /// <b>Предварительно:</b> до 1 га — полностью, до 5 га — половина, до 20 га — четверть, дальше — 5 %.
    /// </summary>
    public IReadOnlyList<ScoreTier> CaptureTiers { get; init; } = [new(100, 1), new(500, 0.5), new(2_000, 0.25), new(null, 0.05)];

    /// <summary>
    /// Ступени суток (§3.5, «на сутки … с убывающей отдачей»): зачётные сотки всех захватов игрока в лиге за игровые сутки
    /// по Минску. <b>Предварительно:</b> до 10 га — полностью, до 30 га — половина, дальше — четверть.
    /// </summary>
    public IReadOnlyList<ScoreTier> DailyTiers { get; init; } = [new(1_000, 1), new(3_000, 0.5), new(null, 0.25)];

    /// <summary>
    /// Бонус за вражескую землю (§3.5): взятая чужая земля (L1 перешла к игроку) считается с множителем 1 + бонус.
    /// <b>Предварительно.</b>
    /// </summary>
    public double EnemyLandBonus { get; init; } = 0.5;

    /// <summary>
    /// Бонус за снятый уровень (§3.5): треснувшая чужая земля (L2/L3 потеряла уровень, но осталась у владельца) считается с
    /// этим множителем — её не взяли, но уровень сняли. <b>Предварительно.</b>
    /// </summary>
    public double LevelRemovedBonus { get; init; } = 0.5;

    /// <summary>Ценность земли (§3.5).</summary>
    public LandValueConfig LandValue { get; init; } = new();

    /// <summary>Дистанция: очков за километр (§3.5).</summary>
    public double DistancePointsPerKm { get; init; } = 10;

    /// <summary>Потолок дистанции за игровые сутки в лиге, км (§3.5: «≤20 км в день»).</summary>
    public double DistanceDailyCapKm { get; init; } = 20;

    /// <summary>Множитель дистанции по лиге (§3.5: «В вело-лиге ×0,33»).</summary>
    public PerLeague<double> DistanceFactor { get; init; } = new(Run: 1, Bike: 0.33);
}

/// <summary>Ступень: каждая сотка до <paramref name="UpToSotki"/> (считая с начала) даёт <paramref name="Rate"/>.</summary>
/// <param name="UpToSotki">Верхняя граница ступени, сотки; <c>null</c> — без границы (последняя ступень).</param>
/// <param name="Rate">Сколько зачётных соток даёт сотка на этой ступени.</param>
public sealed record ScoreTier(double? UpToSotki, double Rate);

/// <summary>
/// Ценность земли (§3.5): «город 1,0; поле, лес, промзона 0,5; Арена ×2 — всё с Сезона 0, слой землепользования — в
/// osm-pipeline v1». Слоя землепользования и границы Арены пока нет (конвейер OSM, C6): до них вся земля — «город»
/// (<see cref="Scoring.LandValue.Factor"/>).
/// </summary>
public sealed record LandValueConfig
{
    public double City { get; init; } = 1;

    /// <summary>Поле, лес, промзона.</summary>
    public double Rural { get; init; } = 0.5;

    /// <summary>Во сколько раз дороже земля в Арене БрГТУ.</summary>
    public double ArenaMultiplier { get; init; } = 2;
}
