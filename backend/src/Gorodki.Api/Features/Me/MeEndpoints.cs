using System.Security.Claims;
using System.Text.Json.Serialization;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Fog;
using Gorodki.Domain.Time;
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
    private static async Task<Results<Ok<MeResponse>, NotFound>> SetPublicProfile(
        PublicProfileRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } userId)
        {
            return TypedResults.NotFound();
        }

        // Одна команда в базу: согласие меняет только сам игрок, повтор того же ответа ничего не меняет. Игрока нет
        // (аккаунт уже стёрт) — ни одной строки, 404.
        var updated = await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.PublicProfile, request.Enabled), cancellationToken);
        return updated == 0 ? TypedResults.NotFound() : await GetMe(principal, db, cancellationToken);
    }

    /// <summary>Своя статистика для экрана профиля.</summary>
    private static async Task<Results<Ok<MyStatsResponse>, NotFound>> GetStats(
        ClaimsPrincipal principal,
        AppDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } userId)
        {
            return TypedResults.NotFound();
        }

        // Первый запрос — всё, что считается в базе: есть ли игрок, его завершённые живые забеги и их засчитанный путь,
        // текущий сезон и место в последнем срезе «Кто открыл больше». Сезон — как SeasonCalendar.At: последний начавшийся
        // (номера идут подряд, начало растёт с номером). Место — как в GET /leaderboards/exploration: день среза — последний
        // по доске «Исследование», строка игрока «Всего» за всё время.
        var now = time.GetUtcNow();
        var totals = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                Runs = db.Runs.Count(r => r.UserId == u.Id && r.Source == RunSource.Live && r.Status != RunStatus.Active),
                Meters = db.Runs
                    .Where(r => r.UserId == u.Id && r.Source == RunSource.Live && r.Status != RunStatus.Active)
                    .Sum(r => r.AcceptedMeters ?? 0),
                Season = db.Seasons.Where(s => s.StartsAt <= now).Max(s => (int?)s.Number),
                Rank = db.LeaderboardSnapshots
                    .Where(s => s.UserId == u.Id
                        && s.Board == LeaderboardBoard.Exploration
                        && s.Layer == LeaderboardLayer.Total
                        && s.Season == SeasonCalendar.AllTime
                        && s.Day == db.LeaderboardSnapshots.Where(l => l.Board == LeaderboardBoard.Exploration).Max(l => (DateOnly?)l.Day))
                    .Select(s => (int?)s.Rank)
                    .FirstOrDefault(),
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (totals is null)
        {
            return TypedResults.NotFound();
        }

        // Второй — туман за всё время и за текущий сезон, оба слоя: площадь клетки зависит от широты тайла, поэтому
        // складывается здесь, как в GET /fog/summary. Битов тайлов (до 8 КБ каждый) не нужно — только число клеток.
        var season = totals.Season ?? SeasonCalendar.AllTime;
        var tiles = await db.FogTiles.AsNoTracking()
            .Where(f => f.UserId == userId && (f.Season == SeasonCalendar.AllTime || f.Season == season))
            .Select(f => new { f.Season, f.TileX, f.TileY, f.CellCount })
            .ToListAsync(cancellationToken);
        double Explored(int fogSeason) => Math.Round(
            tiles.Where(t => t.Season == fogSeason).Sum(t => t.CellCount * FogTileCodec.CellAreaSquareMeters(new FogTileKey(t.TileX, t.TileY))),
            1);

        return TypedResults.Ok(new MyStatsResponse(
            totals.Runs,
            Math.Round(totals.Meters, 1),
            Explored(SeasonCalendar.AllTime),
            totals.Season,
            totals.Season is { } current ? Explored(current) : 0,
            totals.Rank));
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
