using Gorodki.Domain.Time;

namespace Gorodki.Domain.Streaks;

/// <summary>Забег — для серии.</summary>
/// <param name="StartedAt">
/// Начало забега по часам сервера (поправка на часы телефона уже снята): по нему — игровые сутки забега, как у очков за
/// дистанцию.
/// </param>
/// <param name="AcceptedMeters">
/// Засчитанный судьёй путь, м; <c>null</c> — ещё не посчитан: забег идёт или ждёт границы публичности своего конца (путь
/// считается вместе с визитами, через 20–25 минут после финиша).
/// </param>
public readonly record struct StreakRun(DateTimeOffset StartedAt, double? AcceptedMeters);

/// <summary>Серия на момент запроса.</summary>
/// <param name="Days">
/// Засчитанных дней в текущей серии — с сегодняшним, если он уже засчитан; день, спасённый «Заморозкой серии», не
/// прибавляется. 0 — серии нет.
/// </param>
/// <param name="TodayCounted">Сегодняшние игровые сутки уже засчитаны.</param>
/// <param name="FreezesLeft">Активированные и ещё не потраченные «Заморозки серии»: каждая спасёт один следующий пропуск.</param>
/// <param name="FrozenDays">Пропущенные дни, которые спасли заморозки, по порядку — по одной заморозке на день.</param>
public sealed record StreakState(int Days, bool TodayCounted, int FreezesLeft, IReadOnlyList<DateOnly> FrozenDays)
{
    /// <summary>Есть активированная и ещё не потраченная «Заморозка серии».</summary>
    public bool FreezeActive => FreezesLeft > 0;
}

/// <summary>
/// Серия (PLAN.md, §3.7: «серия с „заморозкой“», без ночных требований; §3.11: «Заморозка серии» — «серия не сгорает за
/// пропущенный день») — чистая функция: на входе свои забеги и моменты активации заморозок, на выходе серия на сейчас.
/// Сутки — игровые, по Минску (<see cref="GameClock.GameDayOf"/>).
/// </summary>
/// <remarks>
/// <para>
/// План чисел и подробностей не называет — решено 15.10 (можно пересмотреть): день серии — сутки, в которые живые забеги
/// игрока вместе (обе лиги) засчитали не меньше <see cref="MinDayMeters"/> пути; день, спасённый заморозкой, серию не
/// удлиняет; без серии заморозка не тратится.
/// </para>
/// <para>
/// Сегодняшние сутки не пропущены, пока идут. Вчерашние с забегом, путь которого ещё не посчитан (забег через полночь или
/// ждёт границы публичности), — тоже: иначе серия «сгорала» бы на 20–25 минут после полуночи и тратила заморозку. Такой
/// день прибавляется, когда путь посчитан.
/// </para>
/// <para>
/// Серия считается заново по всей истории при каждом запросе, ничего не хранится: забег из офлайна, посчитанный позже,
/// возвращает и свой день, и заморозку, которую серия на него было потратила.
/// </para>
/// </remarks>
public static class StreakRules
{
    /// <summary>
    /// Столько засчитанного пути за игровые сутки делает их днём серии, м. Километр — как «первый валидный километр» у
    /// бонуса за инвайт (§3.3) и подбора фишек (§3.11): «открыл приложение у подъезда» серию не держит.
    /// </summary>
    public const double MinDayMeters = 1_000;

    /// <summary>Серия на момент <paramref name="now"/>.</summary>
    /// <param name="runs">Свои живые забеги (повторы демо — не дни серии); порядок не важен.</param>
    /// <param name="freezesActivatedAt">
    /// Когда активированы «Заморозки серии». Заморозка спасает первый пропущенный день серии не раньше суток своей
    /// активации: вчерашний пропуск сегодняшней заморозкой уже не вернуть.
    /// </param>
    /// <param name="now">Сейчас.</param>
    /// <param name="minDayMeters">Порог дня серии, м.</param>
    public static StreakState Compute(
        IEnumerable<StreakRun> runs,
        IEnumerable<DateTimeOffset> freezesActivatedAt,
        DateTimeOffset now,
        double minDayMeters = MinDayMeters)
    {
        var today = GameClock.GameDayOf(now);
        var yesterday = today.AddDays(-1);
        var meters = new Dictionary<DateOnly, double>();
        var uncounted = new HashSet<DateOnly>(); // сутки, где у забега путь ещё не посчитан
        foreach (var run in runs)
        {
            var day = GameClock.GameDayOf(run.StartedAt);
            if (day > today)
            {
                continue;
            }

            if (run.AcceptedMeters is { } accepted)
            {
                meters[day] = meters.GetValueOrDefault(day) + accepted;
            }
            else
            {
                uncounted.Add(day);
            }
        }

        var counted = meters.Where(m => m.Value >= minDayMeters).Select(m => m.Key).ToHashSet();
        var freezes = freezesActivatedAt.Select(GameClock.GameDayOf).Where(d => d <= today).Order().ToList();
        if (counted.Count == 0)
        {
            return new StreakState(0, TodayCounted: false, freezes.Count, []);
        }

        // День за днём от первого засчитанного: заморозка становится доступной с суток своей активации и тратится на первый
        // пропуск при живой серии.
        var days = 0;
        var available = 0;
        var nextFreeze = 0;
        var frozen = new List<DateOnly>();
        for (var day = counted.Min(); day <= today; day = day.AddDays(1))
        {
            for (; nextFreeze < freezes.Count && freezes[nextFreeze] <= day; nextFreeze++)
            {
                available++;
            }

            if (counted.Contains(day))
            {
                days++;
            }
            else if (days == 0 || day == today || (day == yesterday && uncounted.Contains(day)))
            {
                // Серии нет — нечему сгорать; сегодня и вчера с непосчитанным забегом — ещё не пропуск.
            }
            else if (available > 0)
            {
                available--;
                frozen.Add(day);
            }
            else
            {
                days = 0;
            }
        }

        return new StreakState(days, counted.Contains(today), available, frozen);
    }
}
