using System.Security.Claims;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Seasons;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Fog;
using Gorodki.Domain.Time;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Fog;

/// <summary>Тайл тумана игрока.</summary>
/// <param name="Version">Только растёт: у тайла, открытого заново после очистки истории, она больше прежней.</param>
/// <param name="CellCount">Открытых клеток; 0 — тайл стёрт очисткой истории исследований.</param>
/// <param name="Bits">Биты тайла 256×256 (8 КБ, слова little-endian: бит <c>строка × 256 + столбец</c>), сжатые Deflate, в Base64.</param>
public sealed record FogTileView(int X, int Y, long Version, int CellCount, byte[] Bits);

/// <summary>Туман игрока по тайлам веб-меркатора уровня 14.</summary>
/// <param name="Season">Номер сезона; <c>null</c> — за всё время.</param>
public sealed record FogResponse(FogLayerKind Layer, int? Season, IReadOnlyList<FogTileView> Tiles, IReadOnlyList<TileRef> Unchanged);

/// <summary>Сколько открыто в слое.</summary>
/// <param name="Season">Номер сезона; <c>null</c> — за всё время.</param>
public sealed record FogLayerSummary(FogLayerKind Layer, int? Season, int Tiles, int CellCount, double AreaSquareMeters);

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
            .WithSummary("Свой туман: layer=foot|bike, season=номер (без него — за всё время), tiles=x:y@версия через запятую (без tiles — все тайлы слоя)")
            .ProducesProblem(StatusCodes.Status400BadRequest);
        fog.MapGet("/summary", GetSummary)
            .WithName("getFogSummary")
            .WithSummary("Сколько открыто: клетки и площадь по слоям — за всё время и за текущий сезон");
        fog.MapDelete("", ClearFog)
            .WithName("clearFog")
            .WithSummary("Очистить историю исследований: весь свой туман — оба слоя, за всё время и по сезонам (необратимо)")
            .WithDescription(
                "Только свой туман. Забеги, которые ещё не открывали туман (в том числе идущий сейчас), его уже не откроют — "
                + "«+N га» у них 0; следующие забеги открывают заново. Свои места в рейтинге «Кто открыл больше» стираются сразу. "
                + "Подсказка FogChanged; тайлы, которые телефон спросит со своей версией, придут пустыми с версией новее. "
                + "Точка «Дом» и её круг живут только на телефоне — их сервер не знает.");
        return app;
    }

    /// <summary>Пустой тайл — ответ на тайл, которого больше нет (история очищена).</summary>
    private static readonly byte[] EmptyTileBits = FogTileCodec.Compress(new FogTileBits());

    private static async Task<Results<Ok<FogResponse>, ProblemHttpResult>> GetFog(
        string? layer, string? tiles, int? season, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        var requested = tiles is null ? null : TerritoryEndpoints.ParseTiles(tiles);
        if (ParseLayer(layer) is not { } kind || (tiles is not null && requested is null) || season < 0)
        {
            return TypedResults.Problem(
                title: "Нужен слой (foot или bike), сезон — номер от 0 и, если есть, от 1 до 25 тайлов вида x:y или x:y@версия.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = "fog_query_invalid" });
        }

        var seasonNumber = season ?? SeasonCalendar.AllTime;
        var query = db.FogTiles.AsNoTracking().Where(f => f.UserId == userId && f.Layer == kind && f.Season == seasonNumber);
        var result = new List<FogTileView>();
        var unchanged = new List<TileRef>();
        if (requested is null)
        {
            var all = await query.OrderBy(f => f.TileX).ThenBy(f => f.TileY).Take(MaxTilesWithoutList).ToListAsync(cancellationToken);
            result.AddRange(all.Select(ToView));
        }
        else
        {
            // Строки спрошенных тайлов — все, без предела: ответ «не изменился» и «пуст» должен быть правдой о каждом тайле.
            // Рамка вокруг далёких тайлов с пределом строк отрезала бы настоящий тайл, и телефон закэшировал бы его пустым.
            var xs = requested.Select(t => t.Tile.X).Distinct().ToList();
            var ys = requested.Select(t => t.Tile.Y).Distinct().ToList();
            var stored = await query.Where(f => xs.Contains(f.TileX) && ys.Contains(f.TileY)).ToListAsync(cancellationToken);
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
                else if (known is > 0 and < long.MaxValue)
                {
                    // Тайл был, а теперь его нет — история очищена (FogHistory). Пустой тайл с версией новее спрошенной
                    // телефон примет и перестанет показывать стёртое; открытый заново тайл получит версию-время, ещё новее.
                    result.Add(new FogTileView(tile.X, tile.Y, known.Value + 1, 0, EmptyTileBits));
                }
            }
        }

        return TypedResults.Ok(new FogResponse(kind, season, result, unchanged));
    }

    private static async Task<Ok<FogSummaryResponse>> GetSummary(
        ClaimsPrincipal principal, AppDbContext db, SeasonStore seasons, TimeProvider time, CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        var current = (await seasons.CalendarAsync(cancellationToken)).At(time.GetUtcNow())?.Number;
        var tiles = await db.FogTiles.AsNoTracking()
            .Where(f => f.UserId == userId && (f.Season == SeasonCalendar.AllTime || f.Season == current))
            .Select(f => new { f.Layer, f.Season, f.TileX, f.TileY, f.CellCount })
            .ToListAsync(cancellationToken);
        var layers = tiles
            .GroupBy(t => (t.Layer, t.Season))
            .OrderBy(g => g.Key.Season)
            .ThenBy(g => g.Key.Layer)
            .Select(g => new FogLayerSummary(
                g.Key.Layer,
                g.Key.Season == SeasonCalendar.AllTime ? null : g.Key.Season,
                g.Count(),
                g.Sum(t => t.CellCount),
                Math.Round(g.Sum(t => t.CellCount * FogTileCodec.CellAreaSquareMeters(new FogTileKey(t.TileX, t.TileY))), 1)))
            .ToList();
        return TypedResults.Ok(new FogSummaryResponse(layers));
    }

    private static async Task<Results<NoContent, UnauthorizedHttpResult>> ClearFog(
        ClaimsPrincipal principal, FogHistory history, CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        await history.ClearAsync(userId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static FogTileView ToView(FogTileEntity tile) => new(tile.TileX, tile.TileY, tile.Version, tile.CellCount, tile.Bits);

    private static FogLayerKind? ParseLayer(string? text) => text switch
    {
        "foot" => FogLayerKind.Foot,
        "bike" => FogLayerKind.Bike,
        _ => null,
    };
}
