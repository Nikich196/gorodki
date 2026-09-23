namespace Gorodki.Domain.Time;

/// <summary>
/// Суточное окно по местному времени: начало включительно, конец не включительно.
/// Окно может переходить через полночь: «ночь» 23:00–06:00 (§3.7 плана),
/// «тишина уведомлений» 22:00–08:00 (§3.17). Сами числа хранятся в игровом конфиге.
/// </summary>
public readonly record struct DailyWindow(TimeOnly Start, TimeOnly End)
{
    /// <remarks>
    /// <see cref="TimeOnly.IsBetween"/> сам разбирает окна через полночь:
    /// для 23:00–06:00 время 02:00 внутри, а 12:00 — снаружи.
    /// </remarks>
    public bool Contains(TimeOnly time) => time.IsBetween(Start, End);
}
