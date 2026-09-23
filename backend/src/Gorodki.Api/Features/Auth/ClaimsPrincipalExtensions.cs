using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Gorodki.Api.Features.Auth;

public static class ClaimsPrincipalExtensions
{
    /// <summary>Идентификатор вошедшего игрока из access-токена (claim <c>sub</c>) или <c>null</c>.</summary>
    public static Guid? UserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id) ? id : null;
}
