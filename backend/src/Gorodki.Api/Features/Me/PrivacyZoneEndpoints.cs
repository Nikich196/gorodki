using System.Security.Claims;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Config;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Me;

/// <summary>Приватная зона игрока.</summary>
/// <param name="RadiusMeters">Радиус — из игрового конфига (<c>privacy.zoneRadiusMeters</c>), общий для всех зон.</param>
public sealed record PrivacyZoneResponse(Guid Id, double Lat, double Lon, double RadiusMeters, long CreatedAtMs);

/// <summary>Новая приватная зона: центр круга.</summary>
public sealed record PrivacyZoneRequest(double Lat, double Lon);

/// <summary>
/// Приватные зоны (PLAN.md, §3.16): «при первом „Старте“ — предложение сделать это место приватной зоной (400 м)».
/// В зоне не засчитываются визиты (§3.3). Зоны видит только сам игрок; удаляются вместе с аккаунтом.
/// </summary>
public static class PrivacyZoneEndpoints
{
    public static IEndpointRouteBuilder MapPrivacyZoneEndpoints(this IEndpointRouteBuilder app)
    {
        var zones = app.MapGroup("/me/privacy-zones").WithTags("Профиль").RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy);
        zones.MapGet("", List).WithName("listPrivacyZones").WithSummary("Мои приватные зоны");
        zones.MapPost("", Create)
            .WithName("createPrivacyZone")
            .WithSummary("Сделать место приватной зоной")
            .WithDescription("Круг радиусом privacy.zoneRadiusMeters вокруг точки; зон не больше privacy.maxZones (409 zone_limit).")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);
        zones.MapDelete("/{id:guid}", Delete).WithName("deletePrivacyZone").WithSummary("Убрать приватную зону");
        return app;
    }

    public static PrivacyZoneResponse ToResponse(PrivacyZoneEntity zone, double radiusMeters) =>
        new(zone.Id, zone.Latitude, zone.Longitude, radiusMeters, zone.CreatedAt.ToUnixTimeMilliseconds());

    private static async Task<Results<Ok<List<PrivacyZoneResponse>>, UnauthorizedHttpResult>> List(
        ClaimsPrincipal principal, AppDbContext db, GameConfigStore configs, CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        var radius = (await configs.GetCurrentAsync(cancellationToken)).Rules.Privacy.ZoneRadiusMeters;
        var zones = await db.PrivacyZones.AsNoTracking().Where(z => z.UserId == userId).OrderBy(z => z.CreatedAt).ToListAsync(cancellationToken);
        return TypedResults.Ok(zones.Select(z => ToResponse(z, radius)).ToList());
    }

    private static async Task<Results<Created<PrivacyZoneResponse>, ProblemHttpResult, UnauthorizedHttpResult>> Create(
        PrivacyZoneRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        GameConfigStore configs,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        if (!double.IsFinite(request.Lat) || !double.IsFinite(request.Lon) || Math.Abs(request.Lat) > 90 || Math.Abs(request.Lon) > 180)
        {
            return Problem(StatusCodes.Status400BadRequest, "zone_invalid", "Точка зоны вне допустимых координат.");
        }

        var privacy = (await configs.GetCurrentAsync(cancellationToken)).Rules.Privacy;

        // Строка игрока блокируется: две зоны, созданные одновременно, не обойдут предел.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await db.Users.FromSql($"SELECT * FROM app.users WHERE id = {userId} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        if (await db.PrivacyZones.CountAsync(z => z.UserId == userId, cancellationToken) >= privacy.MaxZones)
        {
            return Problem(StatusCodes.Status409Conflict, "zone_limit", $"Приватных зон может быть не больше {privacy.MaxZones}.");
        }

        var zone = new PrivacyZoneEntity
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Latitude = Math.Round(request.Lat, 7),
            Longitude = Math.Round(request.Lon, 7),
            CreatedAt = time.GetUtcNow(),
        };
        db.PrivacyZones.Add(zone);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Created($"/me/privacy-zones/{zone.Id}", ToResponse(zone, privacy.ZoneRadiusMeters));
    }

    private static async Task<Results<NoContent, NotFound, UnauthorizedHttpResult>> Delete(
        Guid id, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        // Чужую зону не найти: условие по владельцу — в том же запросе.
        var deleted = await db.PrivacyZones.Where(z => z.Id == id && z.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        return deleted == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }

    private static ProblemHttpResult Problem(int status, string code, string title) =>
        TypedResults.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
