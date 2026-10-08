namespace Gorodki.Api.Features.Auth;

/// <summary>
/// Вход тестового игрока для локальной проверки (<see cref="AuthOptions.DevSignIn"/>, только Development): вместо
/// ID-токена Google приложение присылает <c>dev:&lt;имя&gt;</c>, и сервер считает, что Google подтвердил игрока
/// <c>dev:&lt;имя&gt;</c>. Подписи нет — поэтому на любом сервере, кроме локального, этот класс не регистрируется.
/// </summary>
public sealed class DevGoogleTokenValidator : IGoogleTokenValidator
{
    public const string Prefix = "dev:";

    public Task<GoogleIdentity?> ValidateAsync(string idToken, CancellationToken cancellationToken)
    {
        var name = idToken.StartsWith(Prefix, StringComparison.Ordinal) ? idToken[Prefix.Length..].Trim() : "";
        if (name.Length is 0 or > 40 || name.Any(char.IsControl))
        {
            return Task.FromResult<GoogleIdentity?>(null);
        }

        return Task.FromResult<GoogleIdentity?>(new GoogleIdentity(Prefix + name, null, false));
    }
}
