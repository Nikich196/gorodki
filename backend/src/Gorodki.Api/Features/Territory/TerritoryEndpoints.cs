using System.Globalization;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Gorodki.Api.Features.Territory;

/// <summary>Кусок земли для карты.</summary>
/// <param name="Exterior">Внешний контур: <c>[широта, долгота, широта, долгота, …]</c>, первая точка повторяется в конце.</param>
/// <param name="Holes">Дыры (чужая земля внутри), в том же виде.</param>
public sealed record ParcelView(
    long Id,
    Guid OwnerId,
    short ColorIndex,
    short Level,
    long LastVisitAtMs,
    long? ShieldUntilMs,
    long? SiegeUntilMs,
    IReadOnlyList<double> Exterior,
    IReadOnlyList<IReadOnlyList<double>> Holes);

/// <summary>Тайл и все его куски.</summary>
/// <param name="Version">Версия тайла: растёт при каждом изменении; 0 — в тайле ещё ничего не было.</param>
public sealed record TileTerritory(int X, int Y, long Version, IReadOnlyList<ParcelView> Parcels);

/// <summary>Земля по тайлам.</summary>
/// <param name="Tiles">Тайлы, которые изменились (или все запрошенные, если версии не переданы).</param>
/// <param name="Unchanged">Тайлы, версия которых совпала с известной приложению, — их не нужно перерисовывать.</param>
public sealed record TerritoryResponse(League League, IReadOnlyList<TileTerritory> Tiles, IReadOnlyList<TileRef> Unchanged);

/// <summary>
/// Карта земли для приложения (PLAN.md, §7.3, «Рендер»): заливки по тайлам UTM 1×1 км. Приложение помнит версии тайлов
/// и получает только изменившиеся.
/// </summary>
public static class TerritoryEndpoints
{
    public const int MaxTilesPerRequest = 25;

    public static IEndpointRouteBuilder MapTerritoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/territory", GetTerritory)
            .WithTags("Карта")
            .RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy)
            .WithSummary("Земля по тайлам: tiles=x:y или x:y@известная_версия через запятую, не больше 25")
            .ProducesProblem(StatusCodes.Status400BadRequest);
        return app;
    }

    private static async Task<Results<Ok<TerritoryResponse>, ProblemHttpResult>> GetTerritory(
        string? league, string? tiles, AppDbContext db, CancellationToken cancellationToken)
    {
        if (ParseLeague(league) is not { } parsedLeague || ParseTiles(tiles) is not { } requested)
        {
            return TypedResults.Problem(
                title: "Нужны лига (run или bike) и от 1 до 25 тайлов вида x:y или x:y@версия.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = "territory_query_invalid" });
        }

        int minX = requested.Min(t => t.Tile.X), maxX = requested.Max(t => t.Tile.X);
        int minY = requested.Min(t => t.Tile.Y), maxY = requested.Max(t => t.Tile.Y);
        var versions = (await db.TileVersions.AsNoTracking()
                .Where(v => v.League == parsedLeague && v.TileX >= minX && v.TileX <= maxX && v.TileY >= minY && v.TileY <= maxY)
                .ToListAsync(cancellationToken))
            .ToDictionary(v => new TileKey(v.TileX, v.TileY), v => v.Version);

        var changed = new List<(TileKey Tile, long Version)>();
        var unchanged = new List<TileRef>();
        foreach (var (tile, known) in requested)
        {
            var version = versions.GetValueOrDefault(tile);
            if (known == version)
            {
                unchanged.Add(new TileRef(tile.X, tile.Y));
            }
            else
            {
                changed.Add((tile, version));
            }
        }

        var parcels = changed.Count == 0
            ? []
            : await db.Parcels.AsNoTracking()
                .Where(p => p.League == parsedLeague && p.TileX >= minX && p.TileX <= maxX && p.TileY >= minY && p.TileY <= maxY)
                .Join(db.Users, p => p.OwnerId, u => u.Id, (p, u) => new { Parcel = p, u.ColorIndex })
                .ToListAsync(cancellationToken);

        var result = changed
            .Select(c => new TileTerritory(
                c.Tile.X,
                c.Tile.Y,
                c.Version,
                parcels
                    .Where(p => p.Parcel.TileX == c.Tile.X && p.Parcel.TileY == c.Tile.Y)
                    .OrderBy(p => p.Parcel.Id)
                    .Select(p => ToView(p.Parcel, p.ColorIndex))
                    .ToList()))
            .ToList();
        return TypedResults.Ok(new TerritoryResponse(parsedLeague, result, unchanged));
    }

    /// <summary>Тайлы из строки <c>684:5775,685:5775@3</c>; null — если формат неверный или тайлов нет либо слишком много.</summary>
    public static IReadOnlyList<(TileKey Tile, long? KnownVersion)>? ParseTiles(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is 0 or > MaxTilesPerRequest)
        {
            return null;
        }

        var result = new List<(TileKey, long?)>();
        foreach (var part in parts)
        {
            var at = part.Split('@');
            var xy = at[0].Split(':');
            if (at.Length > 2
                || xy.Length != 2
                || !int.TryParse(xy[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var x)
                || !int.TryParse(xy[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var y))
            {
                return null;
            }

            long? known = null;
            if (at.Length == 2)
            {
                if (!long.TryParse(at[1], NumberStyles.None, CultureInfo.InvariantCulture, out var version))
                {
                    return null;
                }

                known = version;
            }

            result.Add((new TileKey(x, y), known));
        }

        return result.DistinctBy(r => r.Item1).ToList();
    }

    private static League? ParseLeague(string? text) => text switch
    {
        "run" => League.Run,
        "bike" => League.Bike,
        _ => null,
    };

    private static ParcelView ToView(ParcelEntity parcel, short colorIndex) => new(
        parcel.Id,
        parcel.OwnerId,
        colorIndex,
        parcel.Level,
        parcel.LastVisitAt.ToUnixTimeMilliseconds(),
        parcel.ShieldUntil?.ToUnixTimeMilliseconds(),
        parcel.SiegeUntil?.ToUnixTimeMilliseconds(),
        LatLon(parcel.Geometry.ExteriorRing),
        [.. parcel.Geometry.InteriorRings.Select(LatLon)]);

    /// <summary>Кольцо из UTM 34N в широту и долготу; 7 знаков после запятой — около 1 см.</summary>
    private static IReadOnlyList<double> LatLon(LineString ring)
    {
        var result = new double[ring.NumPoints * 2];
        for (var i = 0; i < ring.NumPoints; i++)
        {
            var point = ring.GetCoordinateN(i);
            var (latitude, longitude) = Utm34.Inverse(point.X, point.Y);
            result[2 * i] = Math.Round(latitude, 7);
            result[(2 * i) + 1] = Math.Round(longitude, 7);
        }

        return result;
    }
}
