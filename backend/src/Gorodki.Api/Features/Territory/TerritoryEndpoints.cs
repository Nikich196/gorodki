using System.Globalization;
using Gorodki.Api.Features.Captures;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Gorodki.Api.Features.Territory;

/// <summary>Кусок земли для карты.</summary>
/// <param name="Level">Действующий уровень с учётом угасания (§3.3); 0 — у «призрака».</param>
/// <param name="Ghost">Земля уже потеряна (угасла), но ещё 3 дня видна «призраком».</param>
/// <param name="Exterior">Внешний контур: <c>[широта, долгота, широта, долгота, …]</c>, первая точка повторяется в конце.</param>
/// <param name="Holes">Дыры (чужая земля внутри), в том же виде.</param>
public sealed record ParcelView(
    long Id,
    Guid OwnerId,
    short ColorIndex,
    short Level,
    bool Ghost,
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
            .WithName("getTerritory")
            .WithTags("Карта")
            .RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy)
            .WithSummary("Земля по тайлам: tiles=x:y или x:y@известная_версия через запятую, не больше 25")
            .ProducesProblem(StatusCodes.Status400BadRequest);
        return app;
    }

    private static async Task<Results<Ok<TerritoryResponse>, ProblemHttpResult>> GetTerritory(
        string? league, string? tiles, TerritoryReader reader, CancellationToken cancellationToken)
    {
        if (ParseLeague(league) is not { } parsedLeague || ParseTiles(tiles) is not { } requested)
        {
            return TypedResults.Problem(
                title: "Нужны лига (run или bike) и от 1 до 25 тайлов вида x:y или x:y@версия.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = "territory_query_invalid" });
        }

        return TypedResults.Ok(await reader.ReadAsync(parsedLeague, requested, cancellationToken));
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
}
