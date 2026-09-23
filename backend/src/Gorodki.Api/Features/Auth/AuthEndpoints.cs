using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Gorodki.Api.Features.Auth;

/// <summary>Вход через Google.</summary>
/// <param name="IdToken">ID-токен от Google Sign-In на телефоне.</param>
/// <param name="InviteCode">Инвайт-код — нужен только новому игроку.</param>
/// <param name="AgeConfirmed">Игрок подтвердил, что ему 16 или больше.</param>
/// <param name="ConsentVersion">Версия соглашения, которую игрок принял.</param>
public sealed record GoogleSignInRequest(string IdToken, string? InviteCode, bool AgeConfirmed, int? ConsentVersion);

public sealed record RefreshRequest(string RefreshToken);

/// <summary>Ответ на вход и обновление.</summary>
public sealed record SessionResponse(string AccessToken, string RefreshToken, int ExpiresIn, bool IsNewUser);

/// <summary>
/// Вход, обновление и выход (PLAN.md, D10): Google → свой JWT на 15 минут + одноразовый refresh-токен.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/auth").WithTags("Вход").AllowAnonymous();
        auth.MapPost("/google", SignInWithGoogle).WithName("signInWithGoogle").WithSummary("Вход через Google; новый игрок — по инвайту, 16+ и согласию");
        auth.MapPost("/refresh", Refresh).WithName("refreshSession").WithSummary("Новая пара токенов взамен refresh-токена");
        auth.MapPost("/logout", Logout).WithName("logout").WithSummary("Выход: отзывает все токены этого входа");
        return app;
    }

    private static async Task<Results<Ok<SessionResponse>, ProblemHttpResult>> SignInWithGoogle(
        GoogleSignInRequest request,
        IGoogleTokenValidator google,
        AppDbContext db,
        TokenService tokens,
        IOptions<AuthOptions> options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (options.Value.GoogleClientIds.Length == 0)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "google_not_configured", "Вход через Google ещё не настроен.");
        }

        var identity = await google.ValidateAsync(request.IdToken, cancellationToken);
        if (identity is null)
        {
            return Problem(StatusCodes.Status401Unauthorized, "google_token_invalid", "Google не подтвердил вход.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await db.Users.SingleOrDefaultAsync(u => u.GoogleSubject == identity.Subject, cancellationToken);
        var isNew = user is null;
        if (user is null)
        {
            var now = time.GetUtcNow();
            var invite = string.IsNullOrWhiteSpace(request.InviteCode)
                ? null
                : await db.Invites.AsNoTracking().SingleOrDefaultAsync(i => i.Code == request.InviteCode, cancellationToken);
            var problem = RegistrationRules.Check(
                request.InviteCode, invite, request.AgeConfirmed, request.ConsentVersion, options.Value.ConsentVersion, now);
            if (problem != RegistrationProblem.None || invite is null)
            {
                return Problem(StatusCodes.Status403Forbidden, RegistrationRules.Code(problem), "Регистрация пока невозможна.");
            }

            // Приглашение забираем одним UPDATE с условием: база проверяет остаток сама и блокирует строку, поэтому
            // одновременные регистрации не израсходуют больше приглашений, чем выдано, и не откажут зря.
            var taken = await db.Invites
                .Where(i => i.Code == invite.Code && i.UsedCount < i.MaxUses && (i.ExpiresAt == null || i.ExpiresAt > now))
                .ExecuteUpdateAsync(set => set.SetProperty(i => i.UsedCount, i => i.UsedCount + 1), cancellationToken);
            if (taken == 0)
            {
                return Problem(StatusCodes.Status403Forbidden, "invite_invalid", "Приглашения по этому коду закончились.");
            }

            user = new UserEntity
            {
                Id = Guid.CreateVersion7(),
                GoogleSubject = identity.Subject,
                DisplayName = "",
                NormalizedName = "",
                ColorIndex = (short)Random.Shared.Next(0, 12),
                Role = UserRole.Player,
                AgeConfirmedAt = now,
                ConsentVersion = request.ConsentVersion,
                ConsentedAt = now,
                InviteCode = invite.Code,
                CreatedAt = now,
            };
            await AssignNicknameAsync(db, user, cancellationToken);
            db.Users.Add(user);
        }

        var (refreshToken, entity) = tokens.CreateRefreshToken(user.Id, familyId: Guid.CreateVersion7());
        db.RefreshTokens.Add(entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Два первых входа одного игрока одновременно или совпавший ник. Транзакция откатится вместе с
            // приглашением, повтор запроса пройдёт.
            return Problem(StatusCodes.Status409Conflict, "sign_in_conflict", "Не получилось с первого раза — попробуйте ещё раз.");
        }

        return TypedResults.Ok(new SessionResponse(tokens.CreateAccessToken(user), refreshToken, tokens.AccessTokenSeconds, isNew));
    }

    /// <summary>
    /// Обновление с «льготным окном»: заменённый токен, пришедший снова в течение 60 секунд, получает ещё одну пару
    /// (телефон не дождался ответа). Позже — это похоже на кражу: отзывается вся семья токенов.
    /// Строка токена блокируется (FOR UPDATE), поэтому два одновременных обновления не выдадут две «первые» замены.
    /// </summary>
    private static async Task<Results<Ok<SessionResponse>, ProblemHttpResult>> Refresh(
        RefreshRequest request,
        AppDbContext db,
        TokenService tokens,
        IOptions<AuthOptions> options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var hash = TokenService.Hash(request.RefreshToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var current = await db.RefreshTokens
            .FromSql($"SELECT * FROM app.refresh_tokens WHERE token_hash = {hash} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        var now = time.GetUtcNow();

        if (current is null || current.ExpiresAt <= now)
        {
            return Problem(StatusCodes.Status401Unauthorized, "refresh_invalid", "Нужно войти заново.");
        }

        if (current.RevokedAt is { } revokedAt)
        {
            // Отозван без замены — это выход или отзыв всей семьи: просто войти заново.
            if (current.ReplacedById is null)
            {
                return Problem(StatusCodes.Status401Unauthorized, "refresh_invalid", "Нужно войти заново.");
            }

            // Льготное окно действует, только пока у этого входа есть живой токен. Иначе отзыв семьи можно было бы
            // обойти токеном, заменённым меньше минуты назад.
            var withinGrace = now - revokedAt <= TimeSpan.FromSeconds(options.Value.RefreshReuseGraceSeconds);
            var familyAlive = await db.RefreshTokens.AnyAsync(
                t => t.FamilyId == current.FamilyId && t.RevokedAt == null && t.ExpiresAt > now, cancellationToken);
            if (!withinGrace || !familyAlive)
            {
                await db.RefreshTokens
                    .Where(t => t.FamilyId == current.FamilyId && t.RevokedAt == null)
                    .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedAt, now), cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Problem(StatusCodes.Status401Unauthorized, "refresh_reused", "Вход отменён из соображений безопасности.");
            }
        }

        var user = await db.Users.SingleAsync(u => u.Id == current.UserId, cancellationToken);
        var (refreshToken, next) = tokens.CreateRefreshToken(user.Id, current.FamilyId);
        db.RefreshTokens.Add(next);
        if (current.RevokedAt is null)
        {
            current.RevokedAt = now;
            current.ReplacedById = next.Id;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok(new SessionResponse(tokens.CreateAccessToken(user), refreshToken, tokens.AccessTokenSeconds, false));
    }

    private static async Task<NoContent> Logout(
        RefreshRequest request,
        AppDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var hash = TokenService.Hash(request.RefreshToken);
        var current = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (current is not null)
        {
            var now = time.GetUtcNow();
            await db.RefreshTokens
                .Where(t => t.FamilyId == current.FamilyId && t.RevokedAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedAt, now), cancellationToken);
        }

        return TypedResults.NoContent();
    }

    /// <summary>Ник назначается сам (PLAN.md, §3.18): «Бегун-1234», сменить можно позже.</summary>
    private static async Task AssignNicknameAsync(AppDbContext db, UserEntity user, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var nickname = $"Бегун-{Random.Shared.Next(1000, 10000)}";
            var normalized = nickname.ToLowerInvariant();
            if (!await db.Users.AnyAsync(u => u.NormalizedName == normalized, cancellationToken))
            {
                user.DisplayName = nickname;
                user.NormalizedName = normalized;
                return;
            }
        }

        var fallback = $"Бегун-{user.Id.ToString("N")[..8]}";
        user.DisplayName = fallback;
        user.NormalizedName = fallback.ToLowerInvariant();
    }

    private static ProblemHttpResult Problem(int status, string code, string title) =>
        TypedResults.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
