using System.Security.Claims;
using System.Text.Json.Serialization;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Me;

/// <summary>Профиль вошедшего игрока.</summary>
public sealed record MeResponse(Guid Id, string DisplayName, short ColorIndex, string Role, bool PublicProfile);

/// <summary>Согласие на показ профиля: ник, цвет и земля по нику в рейтингах и на карте; без него — «Игрок #1234».</summary>
/// <param name="Enabled">Показывать ли ник.</param>
public sealed record PublicProfileRequest([property: JsonRequired] bool Enabled);

/// <summary>Запрос на удаление аккаунта принят.</summary>
/// <param name="RequestedAtMs">Когда запрошено (мс Unix); повторный запрос возвращает то же время.</param>
/// <param name="DeleteByMs">Не позже этого момента все данные будут стёрты (закон 99-З: 15 дней); обычно — в течение часа.</param>
public sealed record AccountDeletionResponse(long RequestedAtMs, long DeleteByMs);

public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/me", GetMe).WithName("getMe").WithTags("Профиль").WithSummary("Кто я: ник, цвет, роль");
        app.MapPut("/me/public-profile", SetPublicProfile)
            .WithName("setPublicProfile")
            .WithTags("Профиль")
            .WithSummary("Согласие на показ ника (без него в рейтингах — «Игрок #1234»)")
            .WithDescription(
                "Отдельное согласие на показ ника, цвета и земли по нику (PLAN.md, §3.16; закон 99-З). Действует сразу: рейтинги "
                + "читают его при каждом запросе. Ответ — профиль, как GET /me.");
        app.MapGet("/me/export", Export)
            .WithName("exportMyData")
            .WithTags("Профиль")
            .WithSummary("Мои данные: всё, что сервер хранит об игроке, одним JSON")
            .WithDescription(
                "Профиль и согласия, входы, забеги (точки и датчики — пока хранятся, 14 дней), заявки петель, земля, туман. "
                + "Закон 99-З: право на выгрузку своих данных.");
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

    /// <summary>Включить или выключить согласие на показ профиля.</summary>
    private static Task<Results<Ok<MeResponse>, NotFound>> SetPublicProfile(
        PublicProfileRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        // ЗАДАЧА #71 (Егор): записать request.Enabled в PublicProfile вошедшего игрока и вернуть профиль, как GET /me.
        // Игрока нет (аккаунт уже стёрт) — 404. Тесты — PublicProfileTests.
        _ = (request, principal, db, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #71");
    }

    private static async Task<Results<Ok<AccountExportResponse>, NotFound>> Export(
        ClaimsPrincipal principal,
        AccountExport export,
        HttpContext context,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } userId || await export.BuildAsync(userId, cancellationToken) is not { } data)
        {
            return TypedResults.NotFound();
        }

        var day = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers.ContentDisposition = $"attachment; filename=\"gorodki-my-data-{day}.json\"";
        return TypedResults.Ok(data);
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
