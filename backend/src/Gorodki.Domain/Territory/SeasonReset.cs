namespace Gorodki.Domain.Territory;

/// <summary>
/// Мягкий сброс земли при смене сезона (PLAN.md, §3.4; решено 25.09 в #30, п. 1–2) — чистая функция состояния куска.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>уровень → 1;</item>
/// <item>«последний визит» сдвигается так, чтобы куску осталось меньшее из двух: срок угасания одного уровня (6 дней) или
/// сколько оставалось до исчезновения. Живая земля в момент сброса не исчезает, давно брошенная уходит раньше; уже
/// угасшая («призрак») исчезает тогда же, когда исчезла бы без сброса;</item>
/// <item>щиты снимаются, а с ними осада и окно снятия уровней;</item>
/// <item>владелец, контур, время последнего повышения и касание владельца (<see cref="ParcelState.TouchedAt"/>) не меняются:
/// «касались в этом сезоне» — это касание не раньше начала нового сезона, и сброс его не даёт.</item>
/// </list>
/// Сервер применяет ту же формулу одним SQL-запросом ко всей земле лиги и к журналу захватов (<c>SeasonRollover</c>);
/// интеграционный тест сверяет запрос с этой функцией.
/// </remarks>
public static class SeasonReset
{
    /// <summary>Состояние куска после мягкого сброса в момент <paramref name="seasonStart"/> (начало нового сезона).</summary>
    /// <remarks>
    /// До исчезновения оставалось <c>LastVisitAt + уровень × D − T</c> (<see cref="Decay.LostAt"/>); после сброса уровень 1,
    /// и остаётся <c>LastVisitAt' + D − T</c>. Приравняв это к меньшему из <c>D</c> и прежнего остатка, получаем
    /// <c>LastVisitAt' = min(T, LastVisitAt + (уровень − 1) × D)</c>. Визит уже после начала сезона (задача сброса
    /// опоздала) назад не сдвигается: берётся большее из этого и прежнего визита. Повторный сброс с тем же
    /// <paramref name="seasonStart"/> ничего не меняет.
    /// </remarks>
    public static ParcelState Soft(ParcelState state, DateTimeOffset seasonStart, TerritoryRules rules)
    {
        var shifted = state.LastVisitAt + (rules.DecayInterval * (state.Level - 1));
        var lastVisit = Max(state.LastVisitAt, Min(seasonStart, shifted));
        return state with
        {
            Level = 1,
            LastVisitAt = lastVisit,
            ShieldUntil = null,
            SiegeUntil = null,
            LossWindowSince = null,
            LossAttackers = AttackerSet.Empty,
        };
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
