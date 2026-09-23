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
}

/// <summary>
/// Решение по одному куску земли внутри петли — чистая функция без геометрии (PLAN.md, §3.3).
/// Поэтому правила проверяются обычными тестами, а геометрический движок не знает правил игры.
/// </summary>
/// <remarks>
/// Пока реализованы: ничья земля, своя земля (+1 уровень не чаще 20 ч, не во время осады),
/// соклановцы, щит, переход L1, «трещина» L2/L3 с осадой.
/// Лимиты снятия уровней, ослабление большой петли и защита новичков — следующий шаг (этап 2).
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
                LastVisitAt = now,
                Level = canLevelUp ? current.Level + 1 : current.Level,
                LastLevelUpAt = canLevelUp ? now : current.LastLevelUpAt,
            };
            return (refreshed, PieceOutcome.Refreshed);
        }

        if (context.ClanMates.Contains(current.OwnerId))
        {
            return (current with { LastVisitAt = now }, PieceOutcome.RefreshedForClanMate);
        }

        if (current.ShieldUntil > now)
        {
            return (current, PieceOutcome.Shielded);
        }

        if (current.Level <= 1)
        {
            var taken = NewLand(context.CapturerId, now) with { ShieldUntil = now + rules.TransferShield };
            return (taken, PieceOutcome.Transferred);
        }

        var cracked = current with { Level = current.Level - 1, SiegeUntil = now + rules.Siege };
        return (cracked, PieceOutcome.Cracked);
    }

    private static ParcelState NewLand(Guid owner, DateTimeOffset now) => new()
    {
        OwnerId = owner,
        Level = 1,
        LastVisitAt = now,
        LastLevelUpAt = now,
    };
}
