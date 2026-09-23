namespace Gorodki.Domain.Runs;

/// <summary>Чего ждёт заявка петли, прежде чем сервер её обработает.</summary>
public enum ClaimWait
{
    /// <summary>Всё есть — заявка в очереди на обработку.</summary>
    Nothing,

    /// <summary>Не хватает точек до конца петли: сервер обрабатывает только непрерывное начало следа.</summary>
    Points,

    /// <summary>Датчики (движение, шагомер) до конца петли ещё не переданы полностью: без них не проверить слой 2 античита.</summary>
    Sensors,

    /// <summary>Предыдущая заявка этого забега ещё не обработана: заявки забега применяются по порядку.</summary>
    PreviousClaim,
}

/// <summary>
/// Готовность заявки к обработке (PLAN.md, §7.3). Никаких «подождали — и хватит»: датчики либо переданы до конца петли,
/// либо забег завершён и все его точки на месте — иначе телефон мог бы обойти проверки, просто не присылая датчики.
/// </summary>
public static class ClaimReadiness
{
    /// <summary>Сколько ждать предыдущую заявку: если она застряла (её точки не пришли), следующие не должны стоять вечно.</summary>
    public static readonly TimeSpan PreviousClaimPatience = TimeSpan.FromMinutes(10);

    /// <param name="endSeq">Номер последней точки петли.</param>
    /// <param name="prefixEndSeq">Конец непрерывного начала следа (−1 — точки 0 ещё нет).</param>
    /// <param name="prefixSensorsMs">До какого момента переданы все данные датчиков — по кускам непрерывного начала.</param>
    /// <param name="endChunkLastPointMs">Время последней точки куска, в котором лежит конец петли (если он уже пришёл).</param>
    /// <param name="runComplete">Забег завершён, и все его точки до последней на месте — больше ничего не придёт.</param>
    /// <param name="earlierClaimWaiting">Предыдущая заявка ещё ждёт, а эта пришла недавно (см. <see cref="PreviousClaimPatience"/>).</param>
    public static ClaimWait Check(
        int endSeq, int prefixEndSeq, long prefixSensorsMs, long? endChunkLastPointMs, bool runComplete, bool earlierClaimWaiting)
    {
        if (endSeq > prefixEndSeq || endChunkLastPointMs is not { } endTime)
        {
            return ClaimWait.Points;
        }

        if (prefixSensorsMs < endTime && !runComplete)
        {
            return ClaimWait.Sensors;
        }

        return earlierClaimWaiting ? ClaimWait.PreviousClaim : ClaimWait.Nothing;
    }

    /// <summary>Код для приложения: <c>points</c>, <c>sensors</c>, <c>previous_claim</c>, <c>queue</c>.</summary>
    public static string Code(ClaimWait wait) => wait switch
    {
        ClaimWait.Points => "points",
        ClaimWait.Sensors => "sensors",
        ClaimWait.PreviousClaim => "previous_claim",
        _ => "queue",
    };
}
