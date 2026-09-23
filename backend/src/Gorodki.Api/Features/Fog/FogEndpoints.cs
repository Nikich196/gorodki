using System.Security.Claims;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Fog;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Fog;

/// <summary>Тайл тумана игрока.</summary>
/// <param name="Bits">Биты тайла 256×256 (8 КБ, слова little-endian: бит <c>строка × 256 + столбец</c>), сжатые Deflate, в Base64.</param>
public sealed record FogTileView(int X, int Y, long Version, int CellCount, byte[] Bits);

/// <summary>Туман игрока по тайлам веб-меркатора уровня 14.</summary>
public sealed record FogResponse(FogLayerKind Layer, IReadOnlyList<FogTileView> Tiles, IReadOnlyList<TileRef> Unchanged);

/// <summary>Сколько открыто в слое.</summary>
public sealed record FogLayerSummary(FogLayerKind Layer, int Tiles, int CellCount, double AreaSquareMeters);

public sealed record FogSummaryResponse(IReadOnlyList<FogLayerSummary> Layers);

/// <summary>
/// Туман «Исследования» (PLAN.md, §3.10). Только свой: где человек ходит — личные данные. Точка «Дом» и её круг сервер
/// не знает вовсе — они только на телефоне.
/// </summary>
public static class FogEndpoints
{
    /// <summary>Тайлов тумана без списка — не больше: столько хватает на Брест с окрестностями.</summary>
    public const int MaxTilesWithoutList = 200;

    public static IEndpointRouteBuilder MapFogEndpoints(this IEndpointRouteBuilder app)
    {
        var fog = app.MapGroup("/fog").WithTags("Исследование").RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy);
        fog.MapGet("", GetFog)
            .WithName("getFog")
            .WithSummary("Свой туман: layer=foot|bike, tiles=x:y@версия через запятую (без tiles — все тайлы слоя)")
            .ProducesProblem(StatusCodes.Status400BadRequest);
        fog.MapGet("/summary", GetSummary)
            .WithName("getFogSummary")
            .WithSummary("Сколько открыто: клетки и площадь по слоям");
        return app;
    }

    private static async Task<Results<Ok<FogResponse>, ProblemHttpResult>> GetFog(
        string? layer, string? tiles, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        var requested = tiles is null ? null : TerritoryEndpoints.ParseTiles(tiles);
        if (ParseLayer(layer) is not { } kind || (tiles is not null && requested is null))
        {
            return TypedResults.Problem(
                title: "Нужен слой (foot или bike) и, если есть, от 1 до 25 тайлов вида x:y или x:y@версия.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = "fog_query_invalid" });
        }

        var query = db.FogTiles.AsNoTracking().Where(f => f.UserId == userId && f.Layer == kind && f.Season == 0);
        if (requested is not null)
        {
            int minX = requested.Min(t => t.Tile.X), maxX = requested.Max(t => t.Tile.X);
            int minY = requested.Min(t => t.Tile.Y), maxY = requested.Max(t => t.Tile.Y);
            query = query.Where(f => f.TileX >= minX && f.TileX <= maxX && f.TileY >= minY && f.TileY <= maxY);
        }

        var stored = await query.OrderBy(f => f.TileX).ThenBy(f => f.TileY).Take(MaxTilesWithoutList).ToListAsync(cancellationToken);
        var result = new List<FogTileView>();
        var unchanged = new List<TileRef>();
        if (requested is null)
        {
            result.AddRange(stored.Select(ToView));
        }
        else
        {
            foreach (var (tile, known) in requested)
            {
                var entity = stored.SingleOrDefault(f => f.TileX == tile.X && f.TileY == tile.Y);
                if ((entity?.Version ?? 0) == known)
                {
                    unchanged.Add(new TileRef(tile.X, tile.Y));
                }
                else if (entity is not null)
                {
                    result.Add(ToView(entity));
                }
            }
        }

        return TypedResults.Ok(new FogResponse(kind, result, unchanged));
    }

    private static async Task<Ok<FogSummaryResponse>> GetSummary(
        ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        var tiles = await db.FogTiles.AsNoTracking()
            .Where(f => f.UserId == userId && f.Season == 0)
            .Select(f => new { f.Layer, f.TileX, f.TileY, f.CellCount })
            .ToListAsync(cancellationToken);
        var layers = tiles
            .GroupBy(t => t.Layer)
            .OrderBy(g => g.Key)
            .Select(g => new FogLayerSummary(
                g.Key,
                g.Count(),
                g.Sum(t => t.CellCount),
                Math.Round(g.Sum(t => t.CellCount * FogTileCodec.CellAreaSquareMeters(new FogTileKey(t.TileX, t.TileY))), 1)))
            .ToList();
        return TypedResults.Ok(new FogSummaryResponse(layers));
    }

    private static FogTileView ToView(FogTileEntity tile) => new(tile.TileX, tile.TileY, tile.Version, tile.CellCount, tile.Bits);

    private static FogLayerKind? ParseLayer(string? text) => text switch
    {
        "foot" => FogLayerKind.Foot,
        "bike" => FogLayerKind.Bike,
        _ => null,
    };
}
