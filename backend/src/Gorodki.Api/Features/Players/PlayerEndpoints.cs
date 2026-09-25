using System.Security.Claims;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Gorodki.Api.Features.Players;

/// <summary>Карточка игрока — то, что о нём видят другие.</summary>
/// <param name="Name">Ник — если игрок согласился его показывать или это ты сам; иначе «Игрок #1234», как в рейтинге.</param>
/// <param name="ColorIndex">Цвет земли на карте — номер в палитре из 12.</param>
/// <param name="IsMe">Это сам спрашивающий.</param>
public sealed record PlayerResponse(Guid Id, string Name, short ColorIndex, bool IsMe);

/// <summary>
/// Игроки глазами других (PLAN.md, §3.16: «при отказе — „Игрок #1234“»). Номер владельца есть у каждого куска земли на карте
/// (<c>GET /territory</c>), по нему телефон спрашивает, чей это участок.
/// </summary>
public static class PlayerEndpoints
{
    public static IEndpointRouteBuilder MapPlayerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/players/{id:guid}", GetPlayer)
            .WithName("getPlayer")
            .WithTags("Игроки")
            .WithSummary("Карточка игрока по номеру: ник (с его согласия) или «Игрок #1234», цвет")
            .WithDescription(
                "Номер владельца есть у каждого куска земли (GET /territory). Ник — только если игрок согласился его показывать "
                + "(PUT /me/public-profile) или это ты сам; иначе тот же псевдоним, что в рейтинге. Нет игрока или его аккаунт "
                + "удаляется — 404 player_not_found.")
            .RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static Task<Results<Ok<PlayerResponse>, ProblemHttpResult>> GetPlayer(
        Guid id,
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        // ЗАДАЧА #115 (Егор): найти игрока id. Нет его или аккаунт удаляется (DeletionRequestedAt != null) — 404
        // player_not_found. Имя — DisplayName, если PublicProfile или это сам спрашивающий (principal.UserId()), иначе
        // LeaderboardEndpoints.Pseudonym(id). Тесты — PlayersTests; строка в IdorTests.AwaitingTasks.
        _ = (id, principal, db, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #115");
    }
}
