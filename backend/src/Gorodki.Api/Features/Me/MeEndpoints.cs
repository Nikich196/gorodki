using System.Security.Claims;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Me;

/// <summary>Профиль вошедшего игрока.</summary>
public sealed record MeResponse(Guid Id, string DisplayName, short ColorIndex, string Role, bool PublicProfile);

/// <summary>Запрос на удаление аккаунта принят.</summary>
/// <param name="RequestedAtMs">Когда запрошено (мс Unix); повторный запрос возвращает то же время.</param>
/// <param name="DeleteByMs">Не позже этого момента все данные будут стёрты (закон 99-З: 15 дней); обычно — в течение часа.</param>
public sealed record AccountDeletionResponse(long RequestedAtMs, long DeleteByMs);

public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/me", GetMe).WithName("getMe").WithTags("Профиль").WithSummary("Кто я: ник, цвет, роль");
        app.MapDelete("/me", RequestDeletion)
            .WithName("deleteMe")
            .WithTags("Профиль")
            .WithSummary("Удалить аккаунт и все данные (необратимо)")
            .WithDescription(
                "С этого момента забеги, куски и заявки не принимаются, вход и обновление токенов закрыты. Все данные — земля, "
                + "забеги с точками, захваты, туман — стираются фоновым обработчиком, обычно в течение часа, по закону — "
                + "не позже 15 дней. Повторный запрос ничего не меняет.");
        return app;
    }

    private static async Task<Results<Accepted<AccountDeletionResponse>, NotFound>> RequestDeletion(
        ClaimsPrincipal principal,
        AppDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } userId)
        {
            return TypedResults.NotFound();
        }

        // Отметка ставится один раз: повтор (ответ потерялся) не сдвигает срок.
        var now = time.GetUtcNow();
        await db.Users
            .Where(u => u.Id == userId && u.DeletionRequestedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.DeletionRequestedAt, now), cancellationToken);
        var requestedAt = await db.Users.Where(u => u.Id == userId).Select(u => u.DeletionRequestedAt).SingleOrDefaultAsync(cancellationToken);
        if (requestedAt is not { } at)
        {
            return TypedResults.NotFound(); // аккаунт уже стёрт
        }

        // Выход на всех устройствах: обновить токен больше нельзя, текущий доступ истечёт сам (15 минут).
        await db.RefreshTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        return TypedResults.Accepted(
            "/me",
            new AccountDeletionResponse(at.ToUnixTimeMilliseconds(), (at + AccountDeletion.Deadline).ToUnixTimeMilliseconds()));
    }

    private static async Task<Results<Ok<MeResponse>, NotFound>> GetMe(
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } userId)
        {
            return TypedResults.NotFound();
        }

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return user is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new MeResponse(
                user.Id, user.DisplayName, user.ColorIndex, user.Role.ToString().ToLowerInvariant(), user.PublicProfile));
    }
}
