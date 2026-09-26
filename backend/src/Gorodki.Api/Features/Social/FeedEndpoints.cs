using System.Security.Claims;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Players;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Leagues;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Gorodki.Api.Features.Social;

/// <summary>О чём пост.</summary>
public enum FeedPostKind
{
    /// <summary>Захват: сколько земли взято.</summary>
    Capture,

    /// <summary>Забег: сколько пройдено.</summary>
    Run,
}

/// <summary>Пост ленты — только числа, без координат и времени.</summary>
/// <param name="Id">Номер поста: случайный (<c>Guid.NewGuid()</c>), не по времени — по нему не узнать, когда был захват.</param>
/// <param name="AuthorName">Ник — если автор согласился его показывать или это ты сам; иначе «Игрок #1234».</param>
/// <param name="Day">Игровые сутки (по Минску), <c>yyyy-MM-dd</c> — дата без времени.</param>
/// <param name="DistanceMeters">У забега — засчитанный путь, м, округлён до 1; у захвата — <c>null</c>.</param>
/// <param name="CapturedSquareMeters">У захвата — взятая площадь, м², округлена до 1; у забега — <c>null</c>.</param>
/// <param name="RespectedByMe">Я уже поставил респект.</param>
/// <param name="Mine">Мой пост.</param>
public sealed record FeedPost(
    Guid Id,
    Guid AuthorId,
    string AuthorName,
    short AuthorColorIndex,
    FeedPostKind Kind,
    League League,
    string Day,
    double? DistanceMeters,
    double? CapturedSquareMeters,
    int Respects,
    bool RespectedByMe,
    bool Mine);

/// <summary>Страница ленты, новые сверху.</summary>
/// <param name="NextCursor">Курсор следующей (более старой) страницы; <c>null</c> — дальше ничего нет.</param>
public sealed record FeedResponse(IReadOnlyList<FeedPost> Posts, string? NextCursor);

/// <summary>Жалоба на пост.</summary>
/// <param name="Reason">Причина своими словами, до 200 символов; можно без неё.</param>
public sealed record ReportPostRequest(string? Reason);

/// <summary>
/// Лента (PLAN.md, §3.8: по умолчанию «друзья и клан»; респекты, жалобы, блокировки). Комментариев нет (карточка E14b).
/// Посты — из захватов и забегов, только числа. <b>Граница публичности:</b> пост о захвате виден не раньше самого захвата на
/// карте — <c>visible_at</c> через <c>TerritoryReader.VisibleAtAsync(автор, applied_at)</c>; пост о забеге — когда конец забега
/// публичен (<c>runs.visits_processed_at</c>). До этого поста нет ни в ленте, ни для респекта и жалобы (404). Задача Егора
/// E14b (docs/guides/egor-server.md, раздел 7).
/// </summary>
public static class FeedEndpoints
{
    /// <summary>Постов на странице.</summary>
    public const int PageSize = 30;

    public static IEndpointRouteBuilder MapFeedEndpoints(this IEndpointRouteBuilder app)
    {
        var feed = app.MapGroup("/feed").WithTags("Лента").RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy);
        feed.MapGet("", GetFeed)
            .WithName("getFeed")
            .WithSummary("Лента: scope=friends (друзья и клан, по умолчанию) или all; по 30, cursor — из nextCursor")
            .WithDescription(
                "Только посты, ставшие публичными (захват — с той же границы, что на карте). Посты заблокированных и заблокировавших "
                + "не видны. Неверный scope или курсор — 400 feed_invalid.")
            .ProducesProblem(StatusCodes.Status400BadRequest);
        feed.MapPost("/{id:guid}/respect", RespectPost)
            .WithName("respectPost")
            .WithSummary("Поставить респект (повтор ничего не меняет)")
            .WithDescription("Поста нет или он тебе не виден (ещё до границы публичности, блокировка) — 404 post_not_found.")
            .ProducesProblem(StatusCodes.Status404NotFound);
        feed.MapPost("/{id:guid}/report", ReportPost)
            .WithName("reportPost")
            .WithSummary("Пожаловаться на пост — его посмотрит админ")
            .WithDescription("Поста нет или он тебе не виден — 404 post_not_found; причина длиннее 200 — 400 report_invalid.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        var blocks = app.MapGroup("/me/blocks").WithTags("Лента").RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy);
        blocks.MapGet("", ListBlocks)
            .WithName("listBlocks")
            .WithSummary("Кого я заблокировал");
        blocks.MapPut("/{id:guid}", BlockPlayer)
            .WithName("blockPlayer")
            .WithSummary("Заблокировать игрока: его посты не видны, дружба и заявки с ним снимаются")
            .WithDescription("Себя — 400 block_self. Повтор ничего не меняет.")
            .ProducesProblem(StatusCodes.Status400BadRequest);
        blocks.MapDelete("/{id:guid}", UnblockPlayer)
            .WithName("unblockPlayer")
            .WithSummary("Снять блокировку (повтор ничего не меняет)");
        return app;
    }

    private static Task<Results<Ok<FeedResponse>, ProblemHttpResult>> GetFeed(
        string? scope, string? cursor, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #144 (Егор): таблица постов (автор, вид, лига, игровые сутки, число, visible_at) — пишется после границы
        // или с visible_at от помощников границы (egor-server.md, раздел 4). Отбор: visible_at ≤ now, scope friends — свои,
        // друзей (E14a) и соклановцев (E5), all — все; без заблокированных в обе стороны. Номер поста — Guid.NewGuid().
        // Курсор — (visible_at, номер). Тесты — FeedTests.
        _ = (scope, cursor, principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #144");
    }

    private static Task<Results<NoContent, ProblemHttpResult>> RespectPost(
        Guid id, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #144 (Егор): пост ищется тем же отбором, что в ленте (visible_at ≤ now, без блокировок), — иначе 404
        // post_not_found: по респекту нельзя проверить, есть ли ещё скрытый пост. Один респект от игрока на пост. Тесты —
        // FeedTests; строка в IdorTests.AwaitingTasks.
        _ = (id, principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #144");
    }

    private static Task<Results<NoContent, ProblemHttpResult>> ReportPost(
        Guid id, ReportPostRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #144 (Егор): как RespectPost; жалоба — строка для админа (кто, на что, причина, когда). Тесты —
        // FeedTests; строка в IdorTests.AwaitingTasks.
        _ = (id, request, principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #144");
    }

    private static Task<Ok<IReadOnlyList<PlayerResponse>>> ListBlocks(
        ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #144 (Егор): заблокированные спрашивающим; карточка — как GET /players/{id} (#115). Тесты — FeedTests.
        _ = (principal, db, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #144");
    }

    private static Task<Results<NoContent, ProblemHttpResult>> BlockPlayer(
        Guid id, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #144 (Егор): запись блокировки (повтор — ничего) и снятие дружбы и заявок с этим игроком (E14a). Нет
        // такого игрока — тоже 204: блокировка не должна подтверждать, что номер существует. Тесты — FeedTests; строка в
        // IdorTests.AwaitingTasks.
        _ = (id, principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #144");
    }

    private static Task<NoContent> UnblockPlayer(
        Guid id, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #144 (Егор): снять свою блокировку этого игрока; не было — тоже 204. Тесты — FeedTests; строка в
        // IdorTests.AwaitingTasks.
        _ = (id, principal, db, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #144");
    }
}
