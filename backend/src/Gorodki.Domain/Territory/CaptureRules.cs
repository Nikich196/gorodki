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

    /// <summary>
    /// Когда владелец последний раз взял кусок или освежил его <b>своим</b> забегом (захват, повторная петля, визит) — время
    /// петли или визита. По нему удержание решает, «касались ли участка в этом сезоне» (PLAN.md, §3.4): касание — не раньше
    /// начала сезона. Освежение соклановцем, сдвиг «последнего визита» при смене сезона (<see cref="SeasonReset"/>) и
    /// будущая поправка угасания при откате его не меняют — поэтому это отдельное поле, а не <see cref="LastVisitAt"/>.
    /// </summary>
    public DateTimeOffset TouchedAt { get; init; }
}

/// <summary>Кто и когда захватывает.</summary>
/// <param name="CapturerId">Игрок, замкнувший петлю.</param>
/// <param name="At">Время захвата.</param>
/// <param name="ClanMates">Соклановцы игрока: их землю не отбирают, а освежают.</param>
/// <param name="CanRemoveLevels">
/// Может ли игрок снимать чужие уровни. Нет — у нового аккаунта (моложе 48 ч или с пробегом меньше 3 км, §3.3,
/// защита от мультиаккаунтов): ничью землю он берёт, чужую не трогает.
/// </param>
/// <param name="BigLoop">
/// Большая петля (§3.3, issue #48): площадь P после масок больше порога лиги (<c>territory.bigLoopSquareMeters</c>).
/// Ничью землю она берёт, свою и соклановцев освежает, а чужую не трогает вовсе: пометка «спорная» — отдельный слой
/// карты (<see cref="CaptureResult.Contested"/>), не состояние куска.
/// </param>
public sealed record CaptureContext(
    Guid CapturerId, DateTimeOffset At, IReadOnlySet<Guid> ClanMates, bool CanRemoveLevels = true, bool BigLoop = false);

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

    /// <summary>Угасание: −1 уровень за столько времени без визита (на семестр — 6 дней).</summary>
    public TimeSpan DecayInterval { get; init; } = TimeSpan.FromDays(6);

    /// <summary>Сколько потерянную землю ещё видно «призраком».</summary>
    public TimeSpan GhostDuration { get; init; } = TimeSpan.FromDays(3);
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
    /// <summary>
    /// Чужой кусок не тронут: аккаунт нападающего моложе 48 ч или с пробегом меньше 3 км (§3.3). Иначе второй аккаунт,
    /// заведённый на минуту, снимал бы уровни «вторым нападающим».
    /// </summary>
    NewAccountLimited,
    /// <summary>
    /// Чужой кусок внутри большой петли (§3.3, #48): не тронут вовсе — не перешёл, не треснул, без осады и счётчиков снятия
    /// уровней. Эта земля попадает в зону «спорная» на 24 ч (<see cref="CaptureResult.Contested"/>) — отдельный слой карты.
    /// </summary>
    Contested,
}

/// <summary>
/// Решение по одному куску земли внутри петли — чистая функция без геометрии (PLAN.md, §3.3).
/// Поэтому правила проверяются обычными тестами, а геометрический движок не знает правил игры.
/// </summary>
/// <remarks>
/// Реализованы: ничья земля, своя земля (+1 уровень не чаще 20 ч, не во время осады), соклановцы, щит, переход L1,
/// «трещина» L2/L3 с осадой, лимиты снятия уровней, опоздавшая петля, угасание (по действующему уровню), ослабление большой
/// петли (issue #48, <see cref="CaptureContext.BigLoop"/>). Защита от мультиаккаунтов — <see cref="CaptureContext.CanRemoveLevels"/>.
/// Рейды — следующий шаг.
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

        // Угасание — ленивое: решаем по действующему уровню, а храним уровень на момент визита. Поэтому там, где кусок
        // не меняется, возвращается исходное состояние, «трещина» снимает уровень с хранимого, а визит закрепляет
        // действующий уровень (часы угасания начинаются заново). Угасшая до нуля земля — ничья, даже для бывшего владельца.
        var effective = current is null ? 0 : Decay.EffectiveLevel(current, now, rules);
        if (current is null || effective == 0)
        {
            return (NewLand(context.CapturerId, now), PieceOutcome.ClaimedNeutral);
        }

        if (current.OwnerId == context.CapturerId)
        {
            return (Visit(current, now, rules)!, PieceOutcome.Refreshed);
        }

        if (context.ClanMates.Contains(current.OwnerId))
        {
            // Освежение соклановцем угасание сбрасывает, но «касанием в этом сезоне» не считается (§3.4): TouchedAt прежний.
            var visited = current with { LastVisitAt = Max(current.LastVisitAt, now), Level = effective };
            return (visited, PieceOutcome.RefreshedForClanMate);
        }

        if (current.LastVisitAt > now)
        {
            return (current, PieceOutcome.Superseded);
        }

        if (current.ShieldUntil > now)
        {
            return (current, PieceOutcome.Shielded);
        }

        if (!context.CanRemoveLevels)
        {
            return (current, PieceOutcome.NewAccountLimited);
        }

        // Большая петля ослаблена (§3.3, решено 25.09 в #48): чужая земля внутри — и L1, и L2/L3 — не переходит и не
        // трескается, окно и счётчики снятия уровней не трогаются. Кусок остаётся прежним; пометка «спорная» — отдельный слой.
        if (context.BigLoop)
        {
            return (current, PieceOutcome.Contested);
        }

        // Окно снятия уровней: прошло — считаем заново. Петля «из прошлого» (now раньше начала окна) — в том же окне.
        var windowOpen = current.LossWindowSince is { } since && now - since < rules.LevelLossWindow;
        var attackers = windowOpen ? current.LossAttackers : AttackerSet.Empty;
        if (attackers.Contains(context.CapturerId) || attackers.Count >= rules.MaxLevelsLostPerWindow)
        {
            return (current, PieceOutcome.LossLimited);
        }

        if (effective <= 1)
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

    /// <summary>
    /// Визит владельца на свой кусок (PLAN.md, §3.3: ≥50 м следа внутри или повторный захват): угасание начинается заново,
    /// +1 уровень не чаще раза в 20 ч и не во время осады. Действующий уровень закрепляется. <c>null</c> — кусок уже угас
    /// до нуля: визит его не возвращает (вернуть можно только захватом).
    /// </summary>
    public static ParcelState? Visit(ParcelState current, DateTimeOffset at, TerritoryRules rules)
    {
        var effective = Decay.EffectiveLevel(current, at, rules);
        if (effective == 0)
        {
            return null;
        }

        var besieged = current.SiegeUntil > at;
        var canLevelUp = !besieged
            && effective < rules.MaxLevel
            && at - current.LastLevelUpAt >= rules.LevelUpInterval;
        return current with
        {
            LastVisitAt = Max(current.LastVisitAt, at),
            Level = canLevelUp ? effective + 1 : effective,
            LastLevelUpAt = canLevelUp ? at : current.LastLevelUpAt,
            TouchedAt = Max(current.TouchedAt, at),
        };
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static ParcelState NewLand(Guid owner, DateTimeOffset now) => new()
    {
        OwnerId = owner,
        Level = 1,
        LastVisitAt = now,
        LastLevelUpAt = now,
        TouchedAt = now,
    };
}
