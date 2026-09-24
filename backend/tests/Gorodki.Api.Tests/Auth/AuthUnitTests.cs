using Gorodki.Api.Features.Auth;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Gorodki.Api.Tests.Auth;

public sealed class RegistrationRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static InviteEntity Invite(int used = 0, int max = 5, DateTimeOffset? expires = null) =>
        new() { Code = "BRSTU-2026", UsedCount = used, MaxUses = max, ExpiresAt = expires, CreatedAt = Now };

    [Fact]
    public void Everything_in_order_lets_the_player_in()
    {
        Assert.Equal(RegistrationProblem.None, RegistrationRules.Check("BRSTU-2026", Invite(), true, 1, 1, Now));
    }

    [Fact]
    public void Age_must_be_confirmed_first()
    {
        Assert.Equal(RegistrationProblem.AgeConfirmationRequired, RegistrationRules.Check("BRSTU-2026", Invite(), false, 1, 1, Now));
    }

    [Fact]
    public void Current_consent_version_is_required()
    {
        Assert.Equal(RegistrationProblem.ConsentRequired, RegistrationRules.Check("BRSTU-2026", Invite(), true, null, 1, Now));
        Assert.Equal(RegistrationProblem.ConsentRequired, RegistrationRules.Check("BRSTU-2026", Invite(), true, 1, 2, Now));
    }

    [Fact]
    public void Registration_is_closed_without_an_invite()
    {
        Assert.Equal(RegistrationProblem.InviteRequired, RegistrationRules.Check(null, null, true, 1, 1, Now));
        Assert.Equal(RegistrationProblem.InviteInvalid, RegistrationRules.Check("NOPE", null, true, 1, 1, Now));
    }

    [Fact]
    public void Used_up_or_expired_invite_does_not_work()
    {
        Assert.Equal(RegistrationProblem.InviteInvalid, RegistrationRules.Check("BRSTU-2026", Invite(used: 5, max: 5), true, 1, 1, Now));
        Assert.Equal(
            RegistrationProblem.InviteInvalid,
            RegistrationRules.Check("BRSTU-2026", Invite(expires: Now.AddMinutes(-1)), true, 1, 1, Now));
    }
}

public sealed class TokenServiceTests
{
    private static readonly AuthOptions Settings = new() { SigningKey = Convert.ToBase64String(new byte[32]) };

    [Fact]
    public async Task Access_token_is_valid_for_15_minutes_and_carries_the_user_and_role()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var service = new TokenService(Options.Create(Settings), time);
        var user = new UserEntity { Id = Guid.CreateVersion7(), DisplayName = "Бегун-1", NormalizedName = "бегун-1", Role = UserRole.Demo };

        var token = service.CreateAccessToken(user);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = Settings.Issuer,
            ValidAudience = Settings.Audience,
            IssuerSigningKey = TokenService.SigningKey(Settings),
        });

        Assert.True(result.IsValid);
        Assert.Equal(user.Id.ToString(), result.Claims["sub"]);
        Assert.Equal("demo", result.Claims["role"]);
        var expires = ((JsonWebToken)result.SecurityToken).ValidTo;
        Assert.Equal(time.GetUtcNow().AddMinutes(15).UtcDateTime, expires, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Refresh_tokens_are_random_and_only_their_hash_is_stored()
    {
        var service = new TokenService(Options.Create(Settings), TimeProvider.System);
        var family = Guid.CreateVersion7();

        var (first, firstEntity) = service.CreateRefreshToken(Guid.CreateVersion7(), family);
        var (second, _) = service.CreateRefreshToken(Guid.CreateVersion7(), family);

        Assert.NotEqual(first, second);
        Assert.Equal(43, first.Length); // 256 случайных бит в Base64Url
        Assert.Equal(TokenService.Hash(first), firstEntity.TokenHash);
        Assert.Equal(family, firstEntity.FamilyId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Signing_key_is_read_as_base64_or_base64url(bool urlSafe, bool withoutPadding)
    {
        var raw = Enumerable.Range(0, 32).Select(i => (byte)(i * 8 + 3)).ToArray(); // есть и «+», и «/» в Base64
        var text = Convert.ToBase64String(raw);
        if (urlSafe)
        {
            text = text.Replace('+', '-').Replace('/', '_');
        }

        if (withoutPadding)
        {
            text = text.TrimEnd('=');
        }

        Assert.Equal(raw, new AuthOptions { SigningKey = text }.SigningKeyBytes());
    }

    [Fact]
    public void Garbage_signing_key_gives_a_clear_error()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new AuthOptions { SigningKey = "это не ключ!" }.SigningKeyBytes());
        Assert.Contains("Auth:SigningKey", exception.Message);
    }

    [Fact]
    public void Short_signing_key_is_refused()
    {
        var weak = new AuthOptions { SigningKey = Convert.ToBase64String(new byte[16]) };

        Assert.Throws<InvalidOperationException>(() => weak.SigningKeyBytes());
    }
}
