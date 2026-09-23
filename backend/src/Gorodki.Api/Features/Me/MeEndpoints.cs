using System.Security.Claims;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Gorodki.Api.Features.Me;

/// <summary>Профиль вошедшего игрока.</summary>
public sealed record MeResponse(Guid Id, string DisplayName, short ColorIndex, string Role, bool PublicProfile);

public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/me", GetMe).WithTags("Профиль").WithSummary("Кто я: ник, цвет, роль");
        return app;
    }

    private static async Task<Results<Ok<MeResponse>, NotFound>> GetMe(
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId))
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
