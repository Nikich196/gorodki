using System.Security.Claims;
using System.Security.Cryptography;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Gorodki.Api.Features.Auth;

/// <summary>Пара токенов для приложения.</summary>
/// <param name="AccessToken">JWT на 15 минут: им подписываются все запросы.</param>
/// <param name="RefreshToken">Одноразовый ключ на 30 дней: меняется на новую пару при каждом обновлении.</param>
/// <param name="ExpiresInSeconds">Через сколько секунд истечёт access-токен.</param>
public sealed record TokenPair(string AccessToken, string RefreshToken, int ExpiresInSeconds);

/// <summary>
/// Выпуск токенов. Access — свой JWT (HS256), refresh — случайные 256 бит; в базе хранится только SHA-256 хэш
/// refresh-токена, так что утечка базы не даёт войти ни в чей аккаунт.
/// </summary>
public sealed class TokenService(IOptions<AuthOptions> options, TimeProvider time)
{
    public const string RoleClaim = "role";

    private readonly AuthOptions _options = options.Value;
    private readonly JsonWebTokenHandler _handler = new();

    public static SymmetricSecurityKey SigningKey(AuthOptions options) => new(options.SigningKeyBytes());

    public string CreateAccessToken(UserEntity user)
    {
        var now = time.GetUtcNow();
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(RoleClaim, user.Role.ToString().ToLowerInvariant()),
            ]),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.AddMinutes(_options.AccessTokenMinutes).UtcDateTime,
            SigningCredentials = new SigningCredentials(SigningKey(_options), SecurityAlgorithms.HmacSha256),
        });
    }

    /// <summary>Новый refresh-токен: строка для телефона и запись для базы (с хэшем, не с самим токеном).</summary>
    public (string Token, RefreshTokenEntity Entity) CreateRefreshToken(Guid userId, Guid familyId)
    {
        var token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow();
        return (token, new RefreshTokenEntity
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            FamilyId = familyId,
            TokenHash = Hash(token),
            CreatedAt = now,
            ExpiresAt = now.AddDays(_options.RefreshTokenDays),
        });
    }

    public int AccessTokenSeconds => _options.AccessTokenMinutes * 60;

    public static byte[] Hash(string refreshToken) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(refreshToken));
}
