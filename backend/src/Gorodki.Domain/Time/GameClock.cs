namespace Gorodki.Domain.Time;

/// <summary>
/// Игровые часы. Игровые сутки, суточный срез очков, «ночь» и тишина уведомлений
/// считаются по времени Минска, в каком бы часовом поясе ни работал сервер.
/// Текущее время код берёт только отсюда, поэтому в тестах его легко подменить.
/// </summary>
public sealed class GameClock(TimeProvider timeProvider)
{
    /// <summary>Часовой пояс игры — Минск (UTC+3 круглый год).</summary>
    public static TimeZoneInfo MinskTimeZone { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Minsk");

    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();

    public DateTimeOffset MinskNow => ToMinsk(UtcNow);

    /// <summary>Текущий игровой день (см. <see cref="GameDayOf"/>).</summary>
    public DateOnly Today => GameDayOf(UtcNow);

    /// <summary>Попадает ли текущее минское время в суточное окно (например, «ночь»).</summary>
    public bool IsNow(DailyWindow window) => window.Contains(TimeOnly.FromDateTime(MinskNow.DateTime));

    public static DateTimeOffset ToMinsk(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, MinskTimeZone);

    /// <summary>
    /// Игровой день — календарная дата в Минске. Сутки меняются в 00:00 по Минску (§3.5 плана),
    /// поэтому 21:30 UTC — это уже следующий игровой день.
    /// </summary>
    public static DateOnly GameDayOf(DateTimeOffset instant) =>
        DateOnly.FromDateTime(ToMinsk(instant).DateTime);
}
