using System.Security.Claims;
using System.Text.Json.Serialization;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Gorodki.Api.Features.Social;

/// <summary>Кто этот игрок для меня.</summary>
public enum FriendStatus
{
    /// <summary>Друзья — взаимно.</summary>
    Friend,

    /// <summary>Он добавил меня по коду; я могу принять.</summary>
    Incoming,

    /// <summary>Я добавил его по коду; ждём, пока примет.</summary>
    Outgoing,
}

/// <summary>Друг или заявка.</summary>
/// <param name="Name">Ник — если игрок согласился его показывать; иначе «Игрок #1234» (правило карточки игрока).</param>
public sealed record FriendResponse(Guid PlayerId, string Name, short ColorIndex, FriendStatus Status);

/// <summary>Друзья и заявки.</summary>
/// <param name="MyCode">Свой код дружбы — для QR и ссылки.</param>
/// <param name="Friends">Друзья и заявки: сначала входящие, потом друзья, потом исходящие.</param>
public sealed record FriendsResponse(string MyCode, IReadOnlyList<FriendResponse> Friends);

/// <summary>Добавить друга по его коду (из QR или ссылки).</summary>
public sealed record AddFriendRequest
{
    [JsonRequired]
    public required string Code { get; init; }
}

/// <summary>
/// Друзья (PLAN.md, §3.8): только взаимные, по QR или ссылке со своим кодом; поиска по нику нет. Живых позиций друзей нет
/// (решение Никиты) — дружба открывает ленту «друзья и клан» и дуэли, но не координаты. Задача Егора E14a
/// (docs/guides/egor-server.md, раздел 7).
/// </summary>
public static class FriendEndpoints
{
    public static IEndpointRouteBuilder MapFriendEndpoints(this IEndpointRouteBuilder app)
    {
        var friends = app.MapGroup("/friends").WithTags("Друзья").RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy);
        friends.MapGet("", ListFriends)
            .WithName("listFriends")
            .WithSummary("Друзья, входящие и исходящие заявки и свой код для QR");
        friends.MapPost("", AddFriend)
            .WithName("addFriend")
            .WithSummary("Добавить по коду: заявка; если он уже добавил меня — сразу друзья")
            .WithDescription(
                "Нет такого кода (или игрок заблокировал тебя) — 404 friend_code_invalid: блокировка так не выдаёт себя. Свой код — "
                + "400 friend_self. Ответ — игрок со статусом outgoing или friend.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        friends.MapPost("/{id:guid}/accept", AcceptFriend)
            .WithName("acceptFriend")
            .WithSummary("Принять входящую заявку")
            .WithDescription("Заявки от этого игрока нет — 404 friend_request_not_found.")
            .ProducesProblem(StatusCodes.Status404NotFound);
        friends.MapDelete("/{id:guid}", RemoveFriend)
            .WithName("removeFriend")
            .WithSummary("Удалить из друзей, отклонить или отозвать заявку")
            .WithDescription("Ни дружбы, ни заявки с этим игроком нет — 404 friend_not_found.")
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static Task<Results<Ok<FriendsResponse>, NotFound>> ListFriends(
        ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #143 (Егор): свой код дружбы (выдать при первом запросе, как InviteCodes.New()) и связи игрока. Имя —
        // правило карточки игрока (#115); удаляемый аккаунт не показывать. Новые таблицы — миграция, удаление и выгрузка
        // (egor-server.md, 2.4). Тесты — FriendsTests.
        _ = (principal, db, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #143");
    }

    private static Task<Results<Ok<FriendResponse>, ProblemHttpResult>> AddFriend(
        AddFriendRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #143 (Егор): код → игрок (нет, удаляется или заблокировал спрашивающего — 404 friend_code_invalid).
        // Встречная заявка есть — сразу друзья. Повтор — тот же ответ. Тесты — FriendsTests.
        _ = (request, principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #143");
    }

    private static Task<Results<Ok<FriendResponse>, ProblemHttpResult>> AcceptFriend(
        Guid id, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #143 (Егор): входящая заявка от id к спрашивающему — одним запросом (нет — 404
        // friend_request_not_found). Тесты — FriendsTests; строка в IdorTests.AwaitingTasks.
        _ = (id, principal, db, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #143");
    }

    private static Task<Results<NoContent, ProblemHttpResult>> RemoveFriend(
        Guid id, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #143 (Егор): связь спрашивающего с id в любую сторону (дружба или заявка) — удалить; нет — 404
        // friend_not_found. Тесты — FriendsTests; строка в IdorTests.AwaitingTasks.
        _ = (id, principal, db, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #143");
    }
}
