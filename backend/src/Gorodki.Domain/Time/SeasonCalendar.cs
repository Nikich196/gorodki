namespace Gorodki.Domain.Time;

/// <summary>Сезон: номер как в плане (С0, С1, …), начало — полночь по Минску (PLAN.md, §3.4).</summary>
/// <param name="Number">Номер: 0, 1, 2… — подряд.</param>
/// <param name="Name">Название для людей: «Сезон 0 (бета)».</param>
/// <param name="StartsAt">Начало — 00:00 по Минску; конец сезона — начало следующего.</param>
public sealed record Season(int Number, string Name, DateTimeOffset StartsAt);

/// <summary>
/// Календарь сезонов: какой сезон идёт в данный момент. Общий для всего, что живёт по сезонам (PLAN.md, §3.4):
/// мягкий сброс земли, сезонный слой тумана, короны, лимиты инвайтов, ключ размещения фишек.
/// </summary>
public sealed class SeasonCalendar
{
    /// <summary>«За всё время» — значение сезона у данных, которые не делятся по сезонам (например, туман).</summary>
    public const int AllTime = -1;

    private readonly Season[] _seasons;

    /// <exception cref="ArgumentException">Номера не подряд с нуля, сезоны не по порядку или начало — не полночь по Минску.</exception>
    public SeasonCalendar(IEnumerable<Season> seasons)
    {
        _seasons = [.. seasons.OrderBy(s => s.Number)];
        for (var i = 0; i < _seasons.Length; i++)
        {
            var season = _seasons[i];
            if (season.Number != i)
            {
                throw new ArgumentException($"Сезоны должны идти подряд с нуля: на месте {i} — {season.Number}.", nameof(seasons));
            }

            if (GameClock.ToMinsk(season.StartsAt).TimeOfDay != TimeSpan.Zero)
            {
                throw new ArgumentException($"Сезон {i} начинается не в полночь по Минску: {GameClock.ToMinsk(season.StartsAt):O}.", nameof(seasons));
            }

            if (i > 0 && season.StartsAt <= _seasons[i - 1].StartsAt)
            {
                throw new ArgumentException($"Сезон {i} начинается не позже предыдущего.", nameof(seasons));
            }
        }
    }

    public IReadOnlyList<Season> Seasons => _seasons;

    /// <summary>Сезон, идущий в этот момент; <c>null</c> — до первого сезона (предсезонье, полевые тесты).</summary>
    public Season? At(DateTimeOffset instant) => _seasons.LastOrDefault(s => s.StartsAt <= instant);

    /// <summary>Конец сезона — начало следующего; <c>null</c> — последний сезон идёт до показа.</summary>
    public DateTimeOffset? EndOf(Season season) =>
        season.Number + 1 < _seasons.Length ? _seasons[season.Number + 1].StartsAt : null;

    /// <summary>Полночь по Минску в начале этого дня — в UTC (так время хранится в базе).</summary>
    public static DateTimeOffset MinskMidnight(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, GameClock.MinskTimeZone.GetUtcOffset(local)).ToUniversalTime();
    }
}
