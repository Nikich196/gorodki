namespace Gorodki.Domain.Territory;

/// <summary>
/// Визиты владельца, легшие на землю после захвата: какие они были и как перенести их на землю «до» захвата
/// (PLAN.md, §3.16; docs/architecture/territory-map.md).
/// </summary>
/// <remarks>
/// Пока чужой захват скрыт, публичная проекция возвращает на его место землю, какой она была до него (откат по журналу).
/// Визит (<see cref="CaptureRules.Visit"/>) — единственное, что меняет записанный кусок на месте: жертва пробежала по
/// треснувшей части, автор — по взятой земле, и земля «сейчас» уже не та, какой её оставил захват. Без переноса визитов
/// проекция оставляла бы такую землю как есть — и скрытый захват был бы виден сразу: треснувшая часть с уровнем −1
/// и осадой. Визиты публичны (засчитываются после публичной задержки), поэтому в проекции они должны остаться — но лечь
/// на прежнюю землю так, как легли бы на неё в мире без захвата. Точный откат (<see cref="ExactUndo"/>) кладёт времена
/// визитов со всех вставленных кусков владельца над прежним на весь прежний кусок; откат по граням следа (его запасной
/// путь и откат нарушителя) — по граням, и шов по линии петли там остаётся, если части куска получили разное время
/// визита (docs/architecture/territory-map.md).
/// </remarks>
public static class VisitReplay
{
    /// <summary>
    /// Времена визитов, которые переводят состояние <paramref name="written"/> (каким его записал захват) в
    /// <paramref name="current"/> (какое оно сейчас). Пусто — состояние не менялось; <c>null</c> — визитами этого
    /// не объяснить (другой захват, трещина, смена владельца, удаление аккаунта…).
    /// </summary>
    /// <remarks>
    /// Проверка сама себя подтверждает: времена принимаются, только если те же визиты дают <paramref name="current"/>
    /// в точности. Чего она не может воспроизвести (например, два визита по разные стороны шага угасания), то не визит, и
    /// откат оставит такую землю как есть — как раньше. Освежение своей земли захватом владельца
    /// (<see cref="PieceOutcome.Refreshed"/>) — тот же визит, и его время тоже попадает в след.
    /// </remarks>
    public static IReadOnlyList<DateTimeOffset>? Trace(ParcelState written, ParcelState current, TerritoryRules rules)
    {
        if (current == written)
        {
            return [];
        }

        // Визит не меняет ни владельца, ни щит, ни осаду, ни окно снятия уровней: такое изменение — не визит.
        if (current.OwnerId != written.OwnerId
            || current.ShieldUntil != written.ShieldUntil
            || current.SiegeUntil != written.SiegeUntil
            || current.LossWindowSince != written.LossWindowSince
            || current.LossAttackers != written.LossAttackers)
        {
            return null;
        }

        var times = new List<DateTimeOffset>(2);
        var state = written;

        // Время повышения уровня визит ставит своим временем: уровень рос — значит, был визит ровно тогда.
        if (current.LastLevelUpAt != written.LastLevelUpAt)
        {
            if (CaptureRules.Visit(state, current.LastLevelUpAt, rules) is not { } levelled)
            {
                return null;
            }

            state = levelled;
            times.Add(current.LastLevelUpAt);
        }

        // Остальное объясняет последний визит (без повышения уровня или тот же, что и поднял уровень).
        if (state != current)
        {
            if (CaptureRules.Visit(state, current.LastVisitAt, rules) is not { } visited)
            {
                return null;
            }

            state = visited;
            times.Add(current.LastVisitAt);
        }

        return state == current ? times : null;
    }

    /// <summary>
    /// Те же визиты на другом состоянии (на земле «до» захвата) — по возрастанию времени, как их засчитал бы
    /// <c>VisitProcessor</c>. Визит считается заново от этого состояния, а не копирует итог: треснувшая часть в осаде
    /// не растёт в уровне, а та же земля без захвата выросла бы. Визит на угасшую до нуля землю ничего не меняет, как и на
    /// сервере.
    /// </summary>
    public static ParcelState Apply(ParcelState state, IEnumerable<DateTimeOffset> times, TerritoryRules rules)
    {
        foreach (var at in times.Order())
        {
            state = CaptureRules.Visit(state, at, rules) ?? state;
        }

        return state;
    }
}
