using System.Security.Claims;
using System.Text.Json.Serialization;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Seasons;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Me;

/// <summary>Профиль вошедшего игрока.</summary>
public sealed record MeResponse(Guid Id, string DisplayName, short ColorIndex, string Role, bool PublicProfile);

/// <summary>Согласие на показ профиля: ник, цвет и земля по нику в рейтингах и на карте; без него — «Игрок #1234».</summary>
public sealed record PublicProfileRequest
{
    /// <summary>Показывать ли ник. Обязательно: пустой запрос не должен молча выключать согласие.</summary>
    [JsonRequired]
    public required bool Enabled { get; init; }
}

/// <summary>Своя статистика для профиля — только числа.</summary>
/// <param name="Runs">Завершённые живые забеги (статус не «идёт»); повторы демо не считаются.</param>
/// <param name="DistanceMeters">Засчитанный судьёй путь этих забегов, м, округлён до 0,1.</param>
/// <param name="ExploredSquareMeters">Открыто тумана за всё время, м²: оба слоя вместе, как «Всего» в рейтинге; округлено до 0,1.</param>
/// <param name="Season">Номер текущего сезона; <c>null</c> — сезона сейчас нет.</param>
/// <param name="SeasonExploredSquareMeters">Открыто в текущем сезоне, м², округлено до 0,1; сезона нет — 0.</param>
/// <param name="ExplorationRank">Место в «Кто открыл больше» («Всего», за всё время) по последнему срезу; <c>null</c> — в срезе нет.</param>
/// <remarks>
/// Площади своей земли здесь нет намеренно: посчитанная по настоящей земле, она выдала бы ещё скрытый чужой захват
/// (граница публичности, §3.16). Безопасно её считает <c>TerritoryReader.VisibleOwnedAreaAsync</c> со зрителем «сам игрок»
/// (та же проекция, что у его карты); поле — отдельным изменением контракта после задачи #116.
/// </remarks>
public sealed record MyStatsResponse(
    int Runs,
    double DistanceMeters,
    double ExploredSquareMeters,
    int? Season,
    double SeasonExploredSquareMeters,
    int? ExplorationRank);

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
        app.MapGet("/me/stats", GetStats)
            .WithName("getMyStats")
            .WithTags("Профиль")
            .WithSummary("Своя статистика: забеги, засчитанные метры, открытая площадь за всё время и сезон, место в рейтинге")
            .WithDescription(
                "Только числа. Забеги — завершённые живые (повторы демо не считаются), метры — засчитанные судьёй. Площадь — "
                + "как в GET /fog/summary, оба слоя вместе. Место — в «Кто открыл больше» («Всего», за всё время) по последнему "
                + "срезу; null — в срезе игрока нет.");
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

    /// <summary>Своя статистика для экрана профиля.</summary>
    private static Task<Results<Ok<MyStatsResponse>, NotFound>> GetStats(
        ClaimsPrincipal principal,
        AppDbContext db,
        SeasonStore seasons,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        // ЗАДАЧА #116 (Егор): забеги игрока с Source = Live и Status != Active — их число и сумма AcceptedMeters (null — 0);
        // площадь тумана за всё время и за текущий сезон — как FogEndpoints.GetSummary, но оба слоя вместе; место — строка
        // игрока в последнем срезе «Исследования» (слой Total, Season = −1), как LeaderboardEndpoints.GetExploration.
        // Метры и площади округлить до 0,1 — Math.Round(…, 1), как площади в GetSummary (тест: 3 000,44 + 1 000 → 4 000,4).
        // Площадь своей земли не добавлять (см. MyStatsResponse). Игрока нет — 404. Тесты — StatsTests.
        _ = (principal, db, seasons, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #116");
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
