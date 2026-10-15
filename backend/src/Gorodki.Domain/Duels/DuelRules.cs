namespace Gorodki.Domain.Duels;

/// <summary>Чем кончилась дуэль (PLAN.md, §3.14).</summary>
public enum DuelOutcome : short
{
    /// <summary>Ничья: счёт равный (в том числе 0 : 0) — победителя нет, ставки возвращаются.</summary>
    Draw = 0,

    /// <summary>Победил вызвавший.</summary>
    ChallengerWon = 1,

    /// <summary>Победил вызванный.</summary>
    OpponentWon = 2,

    /// <summary>
    /// Итога нет: дуэль снята — оба аккаунта бегали с одного телефона (правило «одно устройство — один аккаунт») или
    /// соперник удалил аккаунт. Победителя нет, ставки возвращаются, в серию побед не идёт.
    /// </summary>
    Cancelled = 3,
}

/// <summary>Сторона дуэли.</summary>
public enum DuelSideKind : short
{
    Challenger = 1,
    Opponent = 2,
}

/// <summary>Дуэльный значок (§3.7, §3.14): редкость растёт с серией побед — бронза → серебро → золото.</summary>
public enum DuelBadge
{
    None,
    Bronze,
    Silver,
    Gold,
}

/// <summary>
/// Правила дуэлей 1×1 (PLAN.md, §3.14) — чистые функции без базы: условия вызова, кто впереди и кто победил, серия побед и
/// редкость дуэльного значка. Из плана: только взаимные друзья одной лиги, срок 1, 3 или 7 дней, аккаунт старше 48 ч, не
/// больше 2 активных дуэлей, одна пара — не чаще раза в неделю. Остальные числа план не называет — они помечены «решено
/// 15.10» (docs/architecture/duels.md, «Решено без плана»).
/// </summary>
/// <remarks>
/// Числа — константами, как <c>ClanRules</c>, а не в игровом конфиге: конфиг целиком входит в описание API, и новый раздел
/// поменял бы контракт с телефоном. Перенести их в конфиг (раздел <c>duels</c>) — отдельным шагом вместе с телефоном.
/// </remarks>
public static class DuelRules
{
    /// <summary>Активных дуэлей у игрока — не больше (§3.14). Свой ещё не отвеченный вызов тоже занимает место.</summary>
    public const int MaxActive = 2;

    /// <summary>Сроки дуэли в днях (§3.14).</summary>
    public static readonly IReadOnlyList<int> AllowedDays = [1, 3, 7];

    /// <summary>Аккаунт моложе — ни вызвать, ни принять вызов (§3.14).</summary>
    public static readonly TimeSpan MinAccountAge = TimeSpan.FromHours(48);

    /// <summary>Одна пара — не чаще раза в неделю (§3.14): между началами двух дуэлей пары — не меньше 7 дней.</summary>
    public static readonly TimeSpan PairCooldown = TimeSpan.FromDays(7);

    /// <summary>
    /// Сколько вызов ждёт ответа; потом он считается отклонённым и освобождает место вызвавшего. Решено 15.10: сутки — как
    /// самый короткий срок дуэли (план числа не называет).
    /// </summary>
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromHours(24);

    /// <summary>С такой серии побед значок серебряный (решено 15.10).</summary>
    public const int SilverStreak = 3;

    /// <summary>С такой серии побед значок золотой (решено 15.10).</summary>
    public const int GoldStreak = 5;

    public static bool IsAllowedDays(int days) => AllowedDays.Contains(days);

    /// <summary>Аккаунт достаточно старый, чтобы вызывать и принимать вызовы.</summary>
    public static bool IsOldEnough(DateTimeOffset createdAt, DateTimeOffset now) => now - createdAt >= MinAccountAge;

    /// <summary>
    /// Кто впереди по счёту: <c>null</c> — поровну. Счёт сравнивается округлённым до единиц показа (0,1 м, 0,1 м², мс) —
    /// тем, что видит телефон, — иначе «впереди» по невидимым сотым спорило бы с экраном. <c>null</c>-счёт (у
    /// <c>segmentTime</c> — ни одного прохода) хуже любого; у времени меньше — лучше.
    /// </summary>
    public static DuelSideKind? Leader(double? challenger, double? opponent, bool lowerIsBetter)
    {
        var (a, b) = (Shown(challenger), Shown(opponent));
        if (a == b)
        {
            return null;
        }

        if (a is null || b is null)
        {
            return a is null ? DuelSideKind.Opponent : DuelSideKind.Challenger;
        }

        return (a < b) == lowerIsBetter ? DuelSideKind.Challenger : DuelSideKind.Opponent;
    }

    /// <summary>Итог по окончательному счёту: впереди — победил, поровну — ничья.</summary>
    public static DuelOutcome Decide(double? challenger, double? opponent, bool lowerIsBetter) =>
        Leader(challenger, opponent, lowerIsBetter) switch
        {
            DuelSideKind.Challenger => DuelOutcome.ChallengerWon,
            DuelSideKind.Opponent => DuelOutcome.OpponentWon,
            _ => DuelOutcome.Draw,
        };

    /// <summary>Счёт так, как его показывает ответ: 0,1 единицы (время в мс — целое и так).</summary>
    public static double? Shown(double? score) => score is { } value ? Math.Round(value, 1) : null;

    /// <summary>
    /// Серия побед: сколько последних законченных дуэлей подряд выиграно. <paramref name="latestFirst"/> — исходы игрока от
    /// новых к старым: <c>true</c> — победа, <c>false</c> — поражение, <c>null</c> — ничья или снятая дуэль. Ничья и снятая
    /// серию не продолжают и не обрывают (решено 15.10: 0 : 0 у двух занятых людей — не повод терять серебро); обрывает
    /// только поражение.
    /// </summary>
    public static int WinStreak(IEnumerable<bool?> latestFirst)
    {
        var streak = 0;
        foreach (var won in latestFirst)
        {
            if (won == false)
            {
                break;
            }

            streak += won == true ? 1 : 0;
        }

        return streak;
    }

    /// <summary>Редкость значка за победу с такой серией (вместе с этой победой).</summary>
    public static DuelBadge BadgeFor(int streak) => streak switch
    {
        <= 0 => DuelBadge.None,
        < SilverStreak => DuelBadge.Bronze,
        < GoldStreak => DuelBadge.Silver,
        _ => DuelBadge.Gold,
    };
}
