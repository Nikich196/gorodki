using System.Globalization;
using System.Text;

namespace Gorodki.Domain.Clans;

/// <summary>Роль в клане (PLAN.md, §3.6: «лидер и до 2 офицеров»).</summary>
public enum ClanRole
{
    Member,
    Officer,
    Leader,
}

/// <summary>Участник клана для правил: роль и когда вступил.</summary>
public sealed record ClanMemberInfo(Guid UserId, ClanRole Role, DateTimeOffset JoinedAt);

/// <summary>Почему название не подходит.</summary>
public enum ClanNameProblem
{
    None,

    /// <summary>Короче 3 или длиннее 24 символов (после обрезки пробелов).</summary>
    Length,

    /// <summary>Не только буквы, цифры, пробел и дефис.</summary>
    Characters,

    /// <summary>Простой фильтр мата.</summary>
    Profanity,
}

/// <summary>
/// Правила кланов (PLAN.md, §3.3, §3.6; решения по #64 от 25.09) — чистые функции без базы: название, кто кого может
/// исключить, кто становится лидером, когда лидер ушёл. Числа — из плана: 3–12 человек, до 2 офицеров, 72 ч после выхода.
/// </summary>
public static class ClanRules
{
    /// <summary>Потолок участников (§3.6).</summary>
    public const int MaxMembers = 12;

    /// <summary>С этого числа участников клан «полный» (§3.6); меньше — «неполный»: остаётся, но без рейдов и кварталов.</summary>
    public const int FullFrom = 3;

    /// <summary>Офицеров — не больше (§3.6).</summary>
    public const int MaxOfficers = 2;

    /// <summary>Оттенков в палитре кланов (§3.6).</summary>
    public const int Hues = 12;

    public const int NameMinLength = 3;

    public const int NameMaxLength = 24;

    /// <summary>После выхода или исключения — столько без вступления в другой клан (§3.3).</summary>
    public static readonly TimeSpan JoinCooldown = TimeSpan.FromHours(72);

    /// <summary>
    /// Название для хранения и показа: без пробелов по краям, подряд идущие пробелы — один, Unicode в форме NFC. Его и
    /// проверяет <see cref="Check"/>.
    /// </summary>
    public static string Clean(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormC).Trim())
        {
            if (c == ' ' && builder.Length > 0 && builder[^1] == ' ')
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>Ключ уникальности «без учёта регистра» (§3.6): очищенное название в нижнем регистре.</summary>
    public static string Normalize(string name) => Clean(name).ToLowerInvariant();

    /// <summary>
    /// Проверка названия (§3.6): 3–24 символа — буквы, цифры, пробел, дефис; простой фильтр мата. Уникальность проверяет
    /// база (по <see cref="Normalize"/>).
    /// </summary>
    public static ClanNameProblem Check(string name)
    {
        var clean = Clean(name);
        var length = new StringInfo(clean).LengthInTextElements;
        if (length < NameMinLength || length > NameMaxLength)
        {
            return ClanNameProblem.Length;
        }

        if (!clean.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-'))
        {
            return ClanNameProblem.Characters;
        }

        return IsProfane(clean) ? ClanNameProblem.Profanity : ClanNameProblem.None;
    }

    /// <summary>
    /// Простой фильтр мата (§3.6). Слова сравниваются в нижнем регистре, «ё» — как «е», латиница, похожая на кириллицу, —
    /// как кириллица («xyй»). Корень ловится в начале слова (так «рубля» и «барсука» не попадают), однозначные — где угодно.
    /// Это не стена: обходы ловит жалоба, спорное название переименовывает админ (§3.6). Список корней решён 07.10 по
    /// делегированию (план числом и списком его не задаёт).
    /// </summary>
    public static bool IsProfane(string name)
    {
        var lower = name.ToLowerInvariant().Replace('ё', 'е');
        foreach (var variant in new[] { lower, Cyrillic(lower) })
        {
            var words = variant.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries);
            var joined = string.Concat(words); // «пи зда», «ху-й»
            if (Anywhere.Any(joined.Contains) || words.Any(word => WordStart.Any(root => word.StartsWith(root, StringComparison.Ordinal))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Может ли <paramref name="actor"/> исключить <paramref name="target"/> (§3.6; карточка E5b): лидер — любого, офицер —
    /// рядового участника, рядовой — никого. Себя не исключают — для этого есть выход.
    /// </summary>
    public static bool CanRemove(ClanRole actor, ClanRole target) => actor switch
    {
        ClanRole.Leader => target != ClanRole.Leader,
        ClanRole.Officer => target == ClanRole.Member,
        _ => false,
    };

    /// <summary>
    /// Кто становится лидером, когда лидер ушёл (§3.6): самый давний из офицеров, иначе самый давний участник; при равной
    /// дате — по номеру игрока (чтобы ответ не зависел от порядка строк). <c>null</c> — никого не осталось, клана больше нет.
    /// </summary>
    public static Guid? Successor(IEnumerable<ClanMemberInfo> remaining)
    {
        var next = remaining
            .Where(m => m.Role != ClanRole.Leader)
            .OrderByDescending(m => m.Role == ClanRole.Officer)
            .ThenBy(m => m.JoinedAt)
            .ThenBy(m => m.UserId)
            .FirstOrDefault();
        return next?.UserId;
    }

    /// <summary>
    /// Оттенок нового клана (§3.6): свободный из палитры; кланов больше 12 — оттенок повторяется: берётся тот, что встречается
    /// реже всех (при равенстве — меньший номер).
    /// </summary>
    public static int PickHue(IEnumerable<int> used)
    {
        var counts = new int[Hues];
        foreach (var hue in used.Where(h => h is >= 0 and < Hues))
        {
            counts[hue]++;
        }

        var best = 0;
        for (var hue = 1; hue < Hues; hue++)
        {
            if (counts[hue] < counts[best])
            {
                best = hue;
            }
        }

        return best;
    }

    /// <summary>Свободные оттенки палитры: 0–11 без занятых. Пусто — заняты все, можно любой (повторится).</summary>
    public static IReadOnlyList<int> FreeHues(IEnumerable<int> used)
    {
        var taken = used.ToHashSet();
        return [.. Enumerable.Range(0, Hues).Where(h => !taken.Contains(h))];
    }

    // Корни: в начале слова — то, что встречается внутри обычных слов («рубля», «барсука», «страхуем»); где угодно — то,
    // чего в обычных словах нет.
    private static readonly string[] WordStart =
    [
        "бля", "еб", "сука", "суки", "сучк", "сучар", "хуе", "хуя", "муда", "мудил", "пидор", "пидар", "педик", "гандон",
        "залуп", "шлюх", "манда", "fuck", "shit", "cunt", "bitch", "whore", "nigg",
    ];

    private static readonly string[] Anywhere = ["хуй", "пизд", "fuck"];

    /// <summary>Латиница, похожая на кириллицу, — как кириллица: «xyй», «cyka».</summary>
    private static string Cyrillic(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = chars[i] switch
            {
                'a' => 'а',
                'c' => 'с',
                'e' => 'е',
                'k' => 'к',
                'm' => 'м',
                'o' => 'о',
                'p' => 'р',
                't' => 'т',
                'x' => 'х',
                'y' => 'у',
                'h' => 'н',
                _ => chars[i],
            };
        }

        return new string(chars);
    }
}
