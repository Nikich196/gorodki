namespace Gorodki.Api.Features.Auth;

/// <summary>Настройки входа (раздел <c>Auth</c> конфигурации; секреты — только в переменных окружения Render).</summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>Кто выпускает токены («iss»).</summary>
    public string Issuer { get; set; } = "gorodki";

    /// <summary>Для кого токены («aud»): приложение «Городки».</summary>
    public string Audience { get; set; } = "gorodki-app";

    /// <summary>
    /// Ключ подписи access-токенов, Base64, не короче 32 байт. Секрет: переменная <c>Auth__SigningKey</c> на Render.
    /// </summary>
    public string SigningKey { get; set; } = "";

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>
    /// «Льготное окно»: если уже заменённый refresh-токен пришёл снова в течение этих секунд, это, скорее всего,
    /// тот же телефон, не получивший ответ (плохая сеть), а не кража (PLAN.md, D10).
    /// </summary>
    public int RefreshReuseGraceSeconds { get; set; } = 60;

    /// <summary>Действующая версия соглашения: новый игрок должен принять именно её (закон 99-З).</summary>
    public int ConsentVersion { get; set; } = 1;

    /// <summary>OAuth client ID приложения в Google Cloud (сборки Free и Paid). Пусто — вход через Google выключен.</summary>
    public string[] GoogleClientIds { get; set; } = [];

    public byte[] SigningKeyBytes()
    {
        var bytes = Decode(SigningKey.Trim());
        if (bytes.Length < 32)
        {
            throw new InvalidOperationException("Auth:SigningKey должен быть Base64-строкой не короче 32 байт.");
        }

        return bytes;
    }

    /// <summary>Base64 или Base64Url (без «=» в конце) — как бы ключ ни сгенерировали; не разобрать — пусто.</summary>
    private static byte[] Decode(string key)
    {
        if (key.Length == 0)
        {
            return [];
        }

        var standard = key.Replace('-', '+').Replace('_', '/');
        standard = standard.PadRight(standard.Length + (4 - standard.Length % 4) % 4, '=');
        try
        {
            return Convert.FromBase64String(standard);
        }
        catch (FormatException)
        {
            return [];
        }
    }
}
