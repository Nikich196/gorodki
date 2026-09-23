using System.Reflection;
using Gorodki.Domain.Time;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Gorodki.Api.Features.Health;

/// <summary>Служебные адреса: жив ли сервер и какая версия развёрнута.</summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // «Жив ли процесс». Его вызывают Render и пингер, поэтому в базу он не ходит.
        app.MapGet("/health", GetHealth)
            .WithName("getHealth")
            .AllowAnonymous()
            .WithTags("Служебное")
            .WithSummary("Сервер жив: версия, коммит, игровой день по Минску");

        // «Готов ли обслуживать игроков». Проверку базы данных добавим, когда появится база.
        app.MapHealthChecks("/health/ready").AllowAnonymous();

        return app;
    }

    private static Ok<HealthResponse> GetHealth(GameClock clock, IConfiguration configuration) =>
        TypedResults.Ok(new HealthResponse(
            Status: "ok",
            Version: AppVersion,
            // Render сам кладёт хэш развёрнутого коммита в RENDER_GIT_COMMIT.
            Commit: configuration["RENDER_GIT_COMMIT"],
            MinskTime: clock.MinskNow,
            GameDay: clock.Today));

    private static readonly string AppVersion =
        typeof(HealthEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";
}

/// <summary>Ответ /health.</summary>
/// <param name="Status">Всегда «ok», если сервер ответил.</param>
/// <param name="Version">Версия сборки сервера.</param>
/// <param name="Commit">Хэш коммита на Render; локально — null.</param>
/// <param name="MinskTime">Текущее время по Минску.</param>
/// <param name="GameDay">Текущий игровой день.</param>
public sealed record HealthResponse(
    string Status,
    string Version,
    string? Commit,
    DateTimeOffset MinskTime,
    DateOnly GameDay);
