using System.Security.Claims;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Leagues;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Gorodki.Api.Features.HallOfFame;

/// <summary>Что отмечено в Зале славы (PLAN.md, §3.4: «топ-3, топ-клан»).</summary>
public enum HallOfFameKind
{
    /// <summary>Игрок: места 1–3 по очкам сезона в лиге.</summary>
    Player,

    /// <summary>Клан: место 1.</summary>
    Clan,
}

/// <summary>Строка Зала славы.</summary>
/// <param name="Place">Место: 1–3 у игроков, 1 у клана.</param>
/// <param name="PlayerId">Игрок; <c>null</c> — у клана или игрок удалил аккаунт (строка обезличена).</param>
/// <param name="ClanId">Клан; <c>null</c> — у игрока или клана больше нет.</param>
/// <param name="Name">
/// Игрок — ник с его согласия (или свой), иначе «Игрок #1234»; клан — название. <c>null</c> — игрок удалил аккаунт.
/// </param>
/// <param name="Value">Очки сезона — итог на момент закрытия сезона.</param>
/// <param name="Me">Это сам спрашивающий (или его клан).</param>
public sealed record HallOfFameEntry(
    HallOfFameKind Kind, League League, int Place, Guid? PlayerId, Guid? ClanId, string? Name, int Value, bool Me);

/// <summary>Зал славы одного сезона.</summary>
/// <param name="Name">Название сезона, как в <c>GET /seasons</c>.</param>
public sealed record HallOfFameSeason(int Season, string Name, IReadOnlyList<HallOfFameEntry> Entries);

/// <summary>Зал славы: закрытые сезоны, новые сверху.</summary>
public sealed record HallOfFameResponse(IReadOnlyList<HallOfFameSeason> Seasons);

/// <summary>
/// Зал славы (PLAN.md, §3.4: «снимок карты, топ-3, топ-клан; при удалении аккаунта игрок обезличивается»). Снимок —
/// один раз на сезон по его итогу (<c>ScoreBook.FinalTotalsAsync</c>, начисления, видимые на момент закрытия — 04:00 первого
/// дня следующего сезона). Задача Егора E8 (docs/guides/egor-server.md, раздел 7).
/// </summary>
public static class HallOfFameEndpoints
{
    public static IEndpointRouteBuilder MapHallOfFameEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/hall-of-fame", GetHallOfFame)
            .WithName("getHallOfFame")
            .WithTags("Рейтинги")
            .WithSummary("Зал славы: закрытые сезоны — топ-3 игроков по лигам и топ-клан")
            .WithDescription(
                "Только закрытые сезоны: снимок делается один раз по итогу сезона (очки, видимые на момент закрытия — 04:00 первого "
                + "дня следующего). Ник — с согласия игрока, иначе «Игрок #1234»; удалил аккаунт — строка остаётся без имени "
                + "(playerId и name — null).")
            .RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy);
        return app;
    }

    private static Task<Ok<HallOfFameResponse>> GetHallOfFame(
        ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #TBD-E8 (Егор): таблица Зала славы (сезон, лига, вид, место 1–3, игрок — может быть пустым, клан, значение) и
        // её снимок: задача Hangfire, один раз на сезон, когда ScoreBook.FinalTotalsAsync не null (сезон закрыт) и смена на
        // следующий сезон выполнена (seasons.reset_at); повтор ничего не меняет. Не по reset_at и не по «сейчас» (карточка E8).
        // Удаление аккаунта — обезличить (игрок → null), строку не удалять. Имя — читать при запросе по правилу карточки
        // игрока (#115): согласие меняется. Значение топ-клана (например, сумма очков участников) — уточнит Никита.
        // Тесты — HallOfFameTests.
        _ = (principal, db, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #TBD-E8");
    }
}
