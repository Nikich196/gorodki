namespace Gorodki.Domain.Territory;

/// <summary>
/// Состояние куска земли. Когда кусок режется, состояние наследуют все его части (PLAN.md, §3.3).
/// Записи сравниваются по значению: соседние куски с одинаковым состоянием сливаются в один.
/// </summary>
public sealed record ParcelState
{
    public required Guid OwnerId { get; init; }

    /// <summary>Уровень укрепления 1…3.</summary>
    public required int Level { get; init; }

    /// <summary>Последний визит владельца: от него считается угасание.</summary>
    public required DateTimeOffset LastVisitAt { get; init; }

    /// <summary>Когда уровень последний раз рос: +1 не чаще раза в 20 ч.</summary>
    public required DateTimeOffset LastLevelUpAt { get; init; }

    /// <summary>Щит после смены владельца: до этого времени кусок не отнять.</summary>
    public DateTimeOffset? ShieldUntil { get; init; }

    /// <summary>Осада после «трещины»: до этого времени кусок нельзя укреплять.</summary>
    public DateTimeOffset? SiegeUntil { get; init; }

    /// <summary>Начало текущего окна снятия уровней (<see cref="TerritoryRules.LevelLossWindow"/>).</summary>
    public DateTimeOffset? LossWindowSince { get; init; }

    /// <summary>Кто уже снял с куска уровень в текущем окне: каждый — не больше одного, все вместе — не больше двух.</summary>
    public AttackerSet LossAttackers { get; init; }
}

/// <summary>Кто и когда захватывает.</summary>
/// <param name="CapturerId">Игрок, замкнувший петлю.</param>
/// <param name="At">Время захвата.</param>
/// <param name="ClanMates">Соклановцы игрока: их землю не отбирают, а освежают.</param>
public sealed record CaptureContext(Guid CapturerId, DateTimeOffset At, IReadOnlySet<Guid> ClanMates);

/// <summary>Числа правил земли. Хранятся в игровом конфиге.</summary>
public sealed record TerritoryRules
{
    public int MaxLevel { get; init; } = 3;

    /// <summary>Уровень растёт не чаще этого интервала.</summary>
    public TimeSpan LevelUpInterval { get; init; } = TimeSpan.FromHours(20);

    /// <summary>Щит при смене владельца.</summary>
    public TimeSpan TransferShield { get; init; } = TimeSpan.FromHours(12);

    /// <summary>Осада треснувшего куска: нельзя укреплять.</summary>
    public TimeSpan Siege { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Окно, в котором ограничено снятие уровней с куска.</summary>
    public TimeSpan LevelLossWindow { get; init; } = TimeSpan.FromHours(20);

    /// <summary>Сколько уровней все нападающие вместе могут снять с куска за окно (в рейде — 3, рейды позже).</summary>
    public int MaxLevelsLostPerWindow { get; init; } = 2;
}

/// <summary>Что захват сделал с куском земли.</summary>
public enum PieceOutcome
{
    /// <summary>Кусок вне петли — не менялся.</summary>
    Untouched,
    /// <summary>Ничья земля стала землёй игрока.</summary>
    ClaimedNeutral,
    /// <summary>Своя земля: визит, возможно +1 уровень.</summary>
    Refreshed,
    /// <summary>Земля соклановца: только освежена.</summary>
    RefreshedForClanMate,
    /// <summary>Под щитом — не тронута.</summary>
    Shielded,
    /// <summary>Чужой уровень 1 перешёл к игроку.</summary>
    Transferred,
    /// <summary>Чужой уровень 2–3 остался у владельца, но потерял уровень («трещина») и в осаде.</summary>
    Cracked,
    /// <summary>
    /// Чужой кусок не тронут: за окно с него уже сняли предельное число уровней, или этот игрок уже снял свой уровень.
    /// Без этого один забег из трёх кругов вокруг квартала забирал бы L3.
    /// </summary>
    LossLimited,
    /// <summary>
    /// Чужой кусок не тронут: владелец побывал на нём позже этой петли (петля пришла с опозданием, например из офлайна).
    /// Задержка отправки не должна давать преимуществ.
    /// </summary>
    Superseded,
}

/// <summary>
/// Решение по одному куску земли внутри петли — чистая функция без геометрии (PLAN.md, §3.3).
/// Поэтому правила проверяются обычными тестами, а геометрический движок не знает правил игры.
/// </summary>
/// <remarks>
/// Реализованы: ничья земля, своя земля (+1 уровень не чаще 20 ч, не во время осады), соклановцы, щит, переход L1,
/// «трещина» L2/L3 с осадой, лимиты снятия уровней, опоздавшая петля.
/// Ослабление большой петли, рейды и защита от мультиаккаунтов — следующий шаг (этап 2).
/// <para>
/// Петли могут обрабатываться не в том порядке, в каком их пробежали (телефон был без сети). Правила устроены так,
/// чтобы задержка отправки не помогала: визит не отодвигает время назад, а чужой кусок, которого владелец касался
/// позже петли, не трогается. Полной независимости от порядка нет (щиты зависят от того, кто успел первым),
/// поэтому сервер применяет готовые петли по времени.
/// </para>
/// </remarks>
public static class CaptureRules
{
    public static (ParcelState? State, PieceOutcome Outcome) Decide(
        ParcelState? current,
        CaptureContext context,
        TerritoryRules rules)
    {
        var now = context.At;

        if (current is null)
        {
            return (NewLand(context.CapturerId, now), PieceOutcome.ClaimedNeutral);
        }

        if (current.OwnerId == context.CapturerId)
        {
            var besieged = current.SiegeUntil > now;
            var canLevelUp = !besieged
                && current.Level < rules.MaxLevel
                && now - current.LastLevelUpAt >= rules.LevelUpInterval;
            var refreshed = current with
            {
                LastVisitAt = Max(current.LastVisitAt, now),
                Level = canLevelUp ? current.Level + 1 : current.Level,
                LastLevelUpAt = canLevelUp ? now : current.LastLevelUpAt,
            };
            return (refreshed, PieceOutcome.Refreshed);
        }

        if (context.ClanMates.Contains(current.OwnerId))
        {
            return (current with { LastVisitAt = Max(current.LastVisitAt, now) }, PieceOutcome.RefreshedForClanMate);
        }

        if (current.LastVisitAt > now)
        {
            return (current, PieceOutcome.Superseded);
        }

        if (current.ShieldUntil > now)
        {
            return (current, PieceOutcome.Shielded);
        }

        // Окно снятия уровней: прошло — считаем заново. Петля «из прошлого» (now раньше начала окна) — в том же окне.
        var windowOpen = current.LossWindowSince is { } since && now - since < rules.LevelLossWindow;
        var attackers = windowOpen ? current.LossAttackers : AttackerSet.Empty;
        if (attackers.Contains(context.CapturerId) || attackers.Count >= rules.MaxLevelsLostPerWindow)
        {
            return (current, PieceOutcome.LossLimited);
        }

        if (current.Level <= 1)
        {
            var taken = NewLand(context.CapturerId, now) with { ShieldUntil = now + rules.TransferShield };
            return (taken, PieceOutcome.Transferred);
        }

        var cracked = current with
        {
            Level = current.Level - 1,
            SiegeUntil = now + rules.Siege,
            LossWindowSince = windowOpen ? current.LossWindowSince : now,
            LossAttackers = attackers.With(context.CapturerId),
        };
        return (cracked, PieceOutcome.Cracked);
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static ParcelState NewLand(Guid owner, DateTimeOffset now) => new()
    {
        OwnerId = owner,
        Level = 1,
        LastVisitAt = now,
        LastLevelUpAt = now,
    };
}
