using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Gorodki.Api.Features.Health;

/// <summary>
/// Бюджет хранения (PLAN.md, §7: «/health/ready: размер БД, число соединений, число участков и вершин; оповещение на
/// 350 МБ»). Бесплатная база — 500 МБ, при переполнении она только для чтения и встаёт вся игра — узнать надо заранее.
/// </summary>
/// <remarks>
/// С 350 МБ проверка «деградирует»: <c>/health/ready</c> отвечает 503, и пингер присылает письмо о сбое. Render смотрит
/// на <c>/health</c> (в базу не ходит), поэтому из-за этого сервер не перезапускается.
/// </remarks>
public sealed class StorageHealthCheck(AppDbContext db) : IHealthCheck
{
    public const long WarnBytes = 350L * 1024 * 1024;

    public const long LimitBytes = 500L * 1024 * 1024;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT pg_database_size(current_database()),
                       (SELECT count(*) FROM pg_stat_activity WHERE datname = current_database()),
                       (SELECT count(*) FROM app.parcels),
                       (SELECT coalesce(sum(ST_NPoints(geometry)), 0) FROM app.parcels)
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            var bytes = reader.GetInt64(0);
            var data = new Dictionary<string, object>
            {
                ["databaseBytes"] = bytes,
                ["databaseMegabytes"] = Math.Round(bytes / 1024.0 / 1024.0, 1),
                ["warnMegabytes"] = WarnBytes / 1024 / 1024,
                ["limitMegabytes"] = LimitBytes / 1024 / 1024,
                ["connections"] = reader.GetInt64(1),
                ["parcels"] = reader.GetInt64(2),
                ["vertices"] = Convert.ToInt64(reader.GetValue(3)),
            };
            return new HealthCheckResult(Evaluate(bytes), Describe(bytes), data: data);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>До 350 МБ — всё хорошо; дальше — «деградация»: пора чистить или сокращать хранение.</summary>
    public static HealthStatus Evaluate(long databaseBytes) =>
        databaseBytes >= WarnBytes ? HealthStatus.Degraded : HealthStatus.Healthy;

    private static string Describe(long bytes) =>
        bytes >= WarnBytes
            ? $"База {bytes / 1024 / 1024} МБ из {LimitBytes / 1024 / 1024}: при 500 МБ она станет только для чтения — пора чистить"
            : $"База {bytes / 1024 / 1024} МБ из {LimitBytes / 1024 / 1024}";
}
