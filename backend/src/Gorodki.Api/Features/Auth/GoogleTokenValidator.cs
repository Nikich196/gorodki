using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Gorodki.Api.Features.Auth;

/// <summary>Кто вошёл через Google — после проверки подписи ID-токена.</summary>
/// <param name="Subject">Постоянный идентификатор пользователя Google (claim <c>sub</c>).</param>
public sealed record GoogleIdentity(string Subject, string? Email, bool EmailVerified);

/// <summary>Проверка ID-токена Google. За интерфейсом, чтобы тесты подставляли свою реализацию.</summary>
public interface IGoogleTokenValidator
{
    /// <returns>Личность или <c>null</c>, если токен не прошёл проверку.</returns>
    Task<GoogleIdentity?> ValidateAsync(string idToken, CancellationToken cancellationToken);
}

/// <summary>
/// Проверяет ID-токен, который приложение получило от Google Sign-In: подпись ключами Google (их список
/// скачивается и кэшируется по стандартному адресу OpenID), издатель <c>accounts.google.com</c>,
/// получатель — наш client ID, срок действия.
/// </summary>
public sealed class GoogleTokenValidator(IOptions<AuthOptions> options) : IGoogleTokenValidator
{
    private static readonly ConfigurationManager<OpenIdConnectConfiguration> GoogleConfiguration = new(
        "https://accounts.google.com/.well-known/openid-configuration",
        new OpenIdConnectConfigurationRetriever(),
        new HttpDocumentRetriever { RequireHttps = true });

    private readonly JsonWebTokenHandler _handler = new();

    public async Task<GoogleIdentity?> ValidateAsync(string idToken, CancellationToken cancellationToken)
    {
        var clientIds = options.Value.GoogleClientIds;
        if (clientIds.Length == 0)
        {
            return null;
        }

        var configuration = await GoogleConfiguration.GetConfigurationAsync(cancellationToken);
        var result = await _handler.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuers = ["https://accounts.google.com", "accounts.google.com"],
            ValidAudiences = clientIds,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        });

        if (!result.IsValid || !result.Claims.TryGetValue("sub", out var subject) || subject is not string sub)
        {
            return null;
        }

        var email = result.Claims.TryGetValue("email", out var e) ? e as string : null;
        var verified = result.Claims.TryGetValue("email_verified", out var v) && v is true or "true";
        return new GoogleIdentity(sub, email, verified);
    }
}
