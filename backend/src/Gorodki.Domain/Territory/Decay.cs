namespace Gorodki.Domain.Territory;

/// <summary>
/// Угасание земли — ленивое (PLAN.md, §3.3): хранится уровень на момент последнего визита, а действующий уровень считается
/// при чтении и при захвате: −1 за каждые <see cref="TerritoryRules.DecayInterval"/> без визита. Ноль — земля ничья;
/// ещё <see cref="TerritoryRules.GhostDuration"/> её видно «призраком».
/// </summary>
public static class Decay
{
    /// <summary>Действующий уровень в момент <paramref name="at"/>; 0 — кусок уже ничей.</summary>
    public static int EffectiveLevel(ParcelState state, DateTimeOffset at, TerritoryRules rules)
    {
        var idle = at - state.LastVisitAt;
        if (idle <= TimeSpan.Zero)
        {
            return state.Level; // петля «из прошлого» — угасание не считается назад
        }

        var lost = idle.Ticks / rules.DecayInterval.Ticks;
        return (int)Math.Max(0, state.Level - lost);
    }

    /// <summary>Когда кусок станет (или стал) ничьим, если его не посещать.</summary>
    public static DateTimeOffset LostAt(ParcelState state, TerritoryRules rules) =>
        state.LastVisitAt + (rules.DecayInterval * state.Level);

    /// <summary>Кусок уже ничей, но ещё виден «призраком» потерянной земли.</summary>
    public static bool IsGhost(ParcelState state, DateTimeOffset at, TerritoryRules rules) =>
        EffectiveLevel(state, at, rules) == 0 && at - LostAt(state, rules) < rules.GhostDuration;
}
