using Gorodki.Api.Infrastructure.Persistence;

namespace Gorodki.Api.Features.Auth;

/// <summary>Почему новый игрок не может зарегистрироваться. Код уходит в приложение и превращается в понятный текст.</summary>
public enum RegistrationProblem
{
    None,
    /// <summary>Регистрация закрытая: нужен инвайт-код (Сезоны 0–2).</summary>
    InviteRequired,
    /// <summary>Кода нет, он истёк или все приглашения по нему использованы.</summary>
    InviteInvalid,
    /// <summary>Не подтверждён возраст 16+.</summary>
    AgeConfirmationRequired,
    /// <summary>Не принято действующее соглашение.</summary>
    ConsentRequired,
}

/// <summary>Правила регистрации нового игрока (PLAN.md, D10 и §3.16) — чистая функция, проверяется обычными тестами.</summary>
public static class RegistrationRules
{
    public static RegistrationProblem Check(
        string? inviteCode,
        InviteEntity? invite,
        bool ageConfirmed,
        int? acceptedConsentVersion,
        int requiredConsentVersion,
        DateTimeOffset now)
    {
        if (!ageConfirmed)
        {
            return RegistrationProblem.AgeConfirmationRequired;
        }

        if (acceptedConsentVersion != requiredConsentVersion)
        {
            return RegistrationProblem.ConsentRequired;
        }

        if (string.IsNullOrWhiteSpace(inviteCode))
        {
            return RegistrationProblem.InviteRequired;
        }

        if (invite is null || invite.UsedCount >= invite.MaxUses || invite.ExpiresAt <= now)
        {
            return RegistrationProblem.InviteInvalid;
        }

        return RegistrationProblem.None;
    }

    /// <summary>Код ошибки для приложения: <c>invite_required</c>, <c>age_confirmation_required</c>…</summary>
    public static string Code(RegistrationProblem problem) => problem switch
    {
        RegistrationProblem.InviteRequired => "invite_required",
        RegistrationProblem.InviteInvalid => "invite_invalid",
        RegistrationProblem.AgeConfirmationRequired => "age_confirmation_required",
        RegistrationProblem.ConsentRequired => "consent_required",
        _ => "ok",
    };
}
