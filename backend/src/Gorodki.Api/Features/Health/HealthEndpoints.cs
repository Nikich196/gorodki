using System.Reflection;
using System.Text.Json;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Time;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Gorodki.Api.Features.Health;

/// <summary>Служебные адреса: жив ли сервер и какая версия развёрнута.</summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // «Жив ли процесс». Его вызывает Render (healthCheckPath), поэтому в базу он не ходит; пингер ходит в /health/ready.
        app.MapGet("/health", GetHealth)
            .WithName("getHealth")
            .AllowAnonymous()
            .WithTags("Служебное")
            .WithSummary("Сервер жив: версия, коммит, игровой день по Минску");

        // «Готов ли обслуживать игроков»: база отвечает и в ней есть место. Его вызывает пингер (реальный запрос к базе
        // не даёт Supabase уснуть); 503 — повод посмотреть тело ответа: какая проверка не прошла — база недоступна
        // (database) или заполнена больше чем на 350 МБ (storage).
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
            ResponseWriter = WriteReport,
        }).AllowAnonymous();

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

    /// <summary>
    /// Итог проверок в JSON: общий статус и статус каждой проверки. Описание и числа — только администратору (роль из
    /// токена, а не из базы: база как раз может быть недоступна). В описании упавшей проверки — текст исключения Npgsql:
    /// адрес пула или имя пользователя вида <c>postgres.&lt;ref проекта&gt;</c>, а размер базы и число соединений
    /// посторонним тоже ни к чему. Адрес открыт всем — пингеру хватает кода 200/503.
    /// </summary>
    private static Task WriteReport(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        object body = context.User.HasClaim(TokenService.RoleClaim, AdminRole)
            ? new
            {
                status = Name(report.Status),
                durationMs = Math.Round(report.TotalDuration.TotalMilliseconds),
                checks = report.Entries.ToDictionary(
                    e => e.Key,
                    e => new { status = Name(e.Value.Status), description = e.Value.Description, data = e.Value.Data }),
            }
            : new
            {
                status = Name(report.Status),
                checks = report.Entries.ToDictionary(e => e.Key, e => new { status = Name(e.Value.Status) }),
            };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, ReportJson), context.RequestAborted);
    }

    private static string Name(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => "healthy",
        HealthStatus.Degraded => "degraded",
        _ => "unhealthy",
    };

    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web);

    /// <summary>Значение роли администратора в access-токене (<see cref="TokenService.CreateAccessToken"/>).</summary>
    private static readonly string AdminRole = UserRole.Admin.ToString().ToLowerInvariant();

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
