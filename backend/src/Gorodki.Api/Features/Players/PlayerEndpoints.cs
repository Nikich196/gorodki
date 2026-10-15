using System.Security.Claims;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Leaderboards;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

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

    /// <summary>
    /// Карточка по номеру: один запрос — игрок с этим номером, чей аккаунт не удаляется (человек попросил его забыть — уже
    /// «нет такого», хотя строка ещё есть). В ответе только ник по правилу согласия и цвет: ни координат, ни времени, ни
    /// площади и очков — значит, и нечему опередить границу публичности (§3.16). Задача #115 — сделано Claude 15.10.
    /// </summary>
    private static async Task<Results<Ok<PlayerResponse>, ProblemHttpResult>> GetPlayer(
        Guid id,
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var player = await db.Users.AsNoTracking()
            .Where(u => u.Id == id && u.DeletionRequestedAt == null)
            .Select(u => new { u.DisplayName, u.ColorIndex, u.PublicProfile })
            .SingleOrDefaultAsync(cancellationToken);
        if (player is null)
        {
            return TypedResults.Problem(
                title: "Такого игрока нет.",
                statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?> { ["code"] = "player_not_found" });
        }

        // Ник — как в рейтинге: свой — всегда, чужой — только с согласия (PUT /me/public-profile), иначе «Игрок #1234».
        var isMe = principal.UserId() == id;
        var name = isMe || player.PublicProfile ? player.DisplayName : LeaderboardEndpoints.Pseudonym(id);
        return TypedResults.Ok(new PlayerResponse(id, name, player.ColorIndex, isMe));
    }
}
