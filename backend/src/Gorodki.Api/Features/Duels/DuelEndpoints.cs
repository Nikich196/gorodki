using System.Security.Claims;
using System.Text.Json.Serialization;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Leagues;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Gorodki.Api.Features.Duels;

/// <summary>Чем меряется дуэль (PLAN.md, §3.14).</summary>
public enum DuelMetric
{
    /// <summary>Захваченная площадь, м².</summary>
    CapturedArea,

    /// <summary>Открытая площадь тумана, м².</summary>
    ExploredArea,

    /// <summary>Дистанция, м.</summary>
    Distance,

    /// <summary>Лучшее время на выбранном отрезке, мс (меньше — лучше).</summary>
    SegmentTime,
}

/// <summary>Состояние дуэли.</summary>
public enum DuelStatus
{
    /// <summary>Вызов отправлен, соперник ещё не ответил.</summary>
    Pending,

    /// <summary>Идёт.</summary>
    Active,

    /// <summary>Закончилась, итог подведён.</summary>
    Finished,

    /// <summary>Соперник отказался.</summary>
    Declined,
}

/// <summary>Сторона дуэли.</summary>
/// <param name="Name">Ник — если игрок согласился его показывать или это ты сам; иначе «Игрок #1234».</param>
/// <param name="Score">Счёт в единицах метрики; у <c>segmentTime</c> — лучшее время, мс; <c>null</c> — ещё нет результата.</param>
/// <param name="Staked">Поставил фишку из Рюкзака.</param>
public sealed record DuelSide(Guid PlayerId, string Name, short ColorIndex, double? Score, bool Staked);

/// <summary>Дуэль глазами спрашивающего.</summary>
/// <param name="SegmentId">Отрезок — у метрики <c>segmentTime</c>, иначе <c>null</c>.</param>
/// <param name="Days">Срок: 1, 3 или 7 дней.</param>
/// <param name="StartsAtMs">Начало (мс Unix) — когда соперник принял вызов; <c>null</c> — ещё не принят.</param>
/// <param name="EndsAtMs">Конец (мс Unix); <c>null</c> — ещё не принят.</param>
/// <param name="WinnerId">Победитель; <c>null</c> — дуэль не закончена или ничья.</param>
public sealed record DuelResponse(
    Guid Id,
    DuelStatus Status,
    League League,
    DuelMetric Metric,
    Guid? SegmentId,
    int Days,
    DuelSide Me,
    DuelSide Opponent,
    long? StartsAtMs,
    long? EndsAtMs,
    Guid? WinnerId);

/// <summary>Свои дуэли: вызовы, идущие и недавно законченные.</summary>
public sealed record DuelsResponse(IReadOnlyList<DuelResponse> Duels);

/// <summary>Вызвать друга на дуэль.</summary>
public sealed record CreateDuelRequest
{
    /// <summary>Соперник — взаимный друг в этой же лиге.</summary>
    [JsonRequired]
    public required Guid OpponentId { get; init; }

    [JsonRequired]
    public required League League { get; init; }

    [JsonRequired]
    public required DuelMetric Metric { get; init; }

    /// <summary>1, 3 или 7.</summary>
    [JsonRequired]
    public required int Days { get; init; }

    /// <summary>Отрезок — обязателен у метрики <c>segmentTime</c>, иначе не нужен.</summary>
    public Guid? SegmentId { get; init; }

    /// <summary>Фишка из своего Рюкзака на кон (по желанию); победитель забирает обе.</summary>
    public Guid? StakeItemId { get; init; }
}

/// <summary>Принять вызов.</summary>
public sealed record AcceptDuelRequest
{
    /// <summary>Своя фишка на кон (по желанию).</summary>
    public Guid? StakeItemId { get; init; }
}

/// <summary>
/// Дуэли 1×1 (PLAN.md, §3.14): только взаимные друзья одной лиги, срок 1, 3 или 7 дней, метрика — захваченная площадь,
/// открытая площадь, дистанция или лучшее время на отрезке; ставка — по фишке из Рюкзака (значки «Коллекции» на кон не
/// ставятся). Защита: аккаунт старше 48 ч, не больше 2 активных дуэлей, одна пара — не чаще раза в неделю. <b>Живой счёт — по
/// границе публичности:</b> счёт обеих сторон считается так, как их видит посторонний (захват — с его границы, дистанция и
/// туман — когда конец забега публичен), иначе смена лидера выдавала бы, что соперник бежит прямо сейчас. Итог — задача
/// Hangfire, ставки — одной транзакцией. Задача Егора E17 (docs/guides/egor-server.md, раздел 7).
/// </summary>
public static class DuelEndpoints
{
    /// <summary>Активных дуэлей у игрока — не больше (§3.14).</summary>
    public const int MaxActive = 2;

    public static IEndpointRouteBuilder MapDuelEndpoints(this IEndpointRouteBuilder app)
    {
        var duels = app.MapGroup("/duels").WithTags("Дуэли").RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy);
        duels.MapGet("", ListDuels)
            .WithName("listDuels")
            .WithSummary("Свои дуэли: вызовы, идущие и законченные за 30 дней");
        duels.MapPost("", CreateDuel)
            .WithName("createDuel")
            .WithSummary("Вызвать друга на дуэль")
            .WithDescription(
                "Не взаимный друг — 404 duel_opponent_not_found; неверные срок, лига или метрика, нет отрезка у segmentTime — 400 "
                + "duel_invalid; аккаунт моложе 48 ч — 409 duel_account_too_new; уже 2 активных — 409 duel_limit; с этим "
                + "соперником уже была дуэль на этой неделе — 409 duel_pair_week; фишки на кон нет или она активирована — 409 "
                + "duel_stake_invalid.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        duels.MapGet("/{id:guid}", GetDuel)
            .WithName("getDuel")
            .WithSummary("Дуэль с живым счётом (счёт — по границе публичности, как видит посторонний)")
            .WithDescription("Не своя или нет такой — 404 duel_not_found.")
            .ProducesProblem(StatusCodes.Status404NotFound);
        duels.MapPost("/{id:guid}/accept", AcceptDuel)
            .WithName("acceptDuel")
            .WithSummary("Принять вызов (по желанию — со своей фишкой на кон)")
            .WithDescription(
                "Вызов не тебе или уже не ждёт ответа — 404 duel_not_found; лимиты — как при вызове (409 duel_limit, "
                + "duel_account_too_new, duel_stake_invalid).")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        duels.MapPost("/{id:guid}/decline", DeclineDuel)
            .WithName("declineDuel")
            .WithSummary("Отказаться от вызова")
            .WithDescription("Вызов не тебе или уже не ждёт ответа — 404 duel_not_found.")
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static Task<Ok<DuelsResponse>> ListDuels(
        ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #TBD-E17 (Егор): дуэли, где спрашивающий — одна из сторон; Me — всегда он. Тесты — DuelsTests.
        _ = (principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #TBD-E17");
    }

    private static Task<Results<Created<DuelResponse>, ProblemHttpResult>> CreateDuel(
        CreateDuelRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #TBD-E17 (Егор): таблица дуэлей; соперник — взаимный друг (E14a); лимиты — под блокировкой обоих игроков
        // (по порядку номеров — без взаимной блокировки). Фишка на кон — своя неактивированная (E11). Тесты — DuelsTests.
        _ = (request, principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #TBD-E17");
    }

    private static Task<Results<Ok<DuelResponse>, ProblemHttpResult>> GetDuel(
        Guid id, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #TBD-E17 (Егор): дуэль ищется с условием «спрашивающий — сторона» в том же запросе (чужая — 404). Счёт —
        // только видимое постороннему: площадь захватов — по заявкам, чья граница публичности прошла (как очки,
        // ScoreBook.Visible), дистанция и туман — по забегам с visits_processed_at. Тесты — DuelsTests; строка в
        // IdorTests.AwaitingTasks.
        _ = (id, principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #TBD-E17");
    }

    private static Task<Results<Ok<DuelResponse>, ProblemHttpResult>> AcceptDuel(
        Guid id, AcceptDuelRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #TBD-E17 (Егор): вызов Pending, где спрашивающий — соперник (иначе 404); начало — сейчас, конец — через Days;
        // ставки — одной транзакцией. Тесты — DuelsTests; строка в IdorTests.AwaitingTasks.
        _ = (id, request, principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #TBD-E17");
    }

    private static Task<Results<NoContent, ProblemHttpResult>> DeclineDuel(
        Guid id, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #TBD-E17 (Егор): вызов Pending, где спрашивающий — соперник (иначе 404) → Declined. Тесты — DuelsTests; строка
        // в IdorTests.AwaitingTasks.
        _ = (id, principal, db, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #TBD-E17");
    }
}
