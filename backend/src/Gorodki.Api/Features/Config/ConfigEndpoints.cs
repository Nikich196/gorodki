using Gorodki.Domain.Config;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Gorodki.Api.Features.Config;

/// <summary>Игровой конфиг для телефона.</summary>
/// <param name="Version">Номер версии: его телефон передаёт при старте забега.</param>
/// <param name="ActiveFrom">С какого момента версия действует.</param>
/// <param name="Rules">Все числа правил.</param>
public sealed record ConfigResponse(int Version, DateTimeOffset ActiveFrom, GameConfig Rules);

public static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/config", GetCurrent).WithTags("Конфиг").WithSummary("Действующая версия игрового конфига (берётся при каждом «Старте»)");
        return app;
    }

    private static async Task<Ok<ConfigResponse>> GetCurrent(GameConfigStore store, CancellationToken cancellationToken)
    {
        var current = await store.GetCurrentAsync(cancellationToken);
        return TypedResults.Ok(new ConfigResponse(current.Version, current.ActiveFrom, current.Rules));
    }
}
