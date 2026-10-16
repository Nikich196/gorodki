namespace Gorodki.Domain.Inventory;

/// <summary>
/// «Припасы» (PLAN.md, §3.11): «за каждые 2 км валидного движения, до 4 в день. Геоданные не нужны»; Рюкзак — 12 ячеек,
/// срок 7 дней. Чистые правила выдачи — без базы; выдаёт сервер после подсчёта пути забега (вместе с визитами).
/// </summary>
/// <remarks>
/// Решено 16.10 (можно пересмотреть): «2 км» считаются по каждому забегу отдельно (забеги в 1,5 км и 1,5 км за день
/// фишки не дают) — так выдача — одно действие над одним забегом и не требует учёта «остатка» метров. Путь — весь
/// засчитанный судьёй (<c>runs.accepted_meters</c>), без обрезки 200 м: число километров не говорит, где бегали.
/// </remarks>
public static class SupplyRules
{
    /// <summary>Одна фишка за столько засчитанного пути, м.</summary>
    public const double MetersPerItem = 2_000;

    /// <summary>Не больше стольких фишек за игровые сутки.</summary>
    public const int DailyLimit = 4;

    /// <summary>Ячеек в Рюкзаке: полный Рюкзак новых «Припасов» не принимает.</summary>
    public const int Slots = 12;

    /// <summary>Сколько дней фишка ждёт активации, потом пропадает.</summary>
    public const int LifetimeDays = 7;

    /// <summary>«Радар» действует столько метров засчитанного пути после активации (§3.11: «×2 на 3 км»).</summary>
    public const double RadarMeters = 3_000;

    /// <summary>
    /// Сколько фишек даёт забег: по одной за каждые полные 2 км, но не больше, чем осталось до дневного предела и
    /// свободных ячеек.
    /// </summary>
    /// <param name="acceptedMeters">Засчитанный путь забега, м.</param>
    /// <param name="grantedToday">Сколько фишек уже выдано за игровые сутки забега.</param>
    /// <param name="occupiedSlots">Сколько ячеек Рюкзака уже занято.</param>
    public static int Count(double acceptedMeters, int grantedToday, int occupiedSlots)
    {
        var earned = acceptedMeters > 0 ? (int)Math.Floor(acceptedMeters / MetersPerItem) : 0;
        return Math.Max(0, Math.Min(earned, Math.Min(DailyLimit - grantedToday, Slots - occupiedSlots)));
    }

    /// <summary>
    /// Какая по счёту фишка игрока (0, 1, 2… за всё время) — «Радар»: в Сезоне 0 выдаются только «Радар» и «Заморозка
    /// серии», по очереди — первая «Радар». По очереди, а не случайно: тип предсказуем и проверяем тестом, а двух
    /// «Заморозок» подряд не бывает.
    /// </summary>
    public static bool IsRadar(int issuedBefore) => issuedBefore % 2 == 0;
}
