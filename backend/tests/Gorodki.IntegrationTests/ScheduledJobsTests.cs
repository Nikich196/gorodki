using System.Net;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Infrastructure.Jobs;
using Gorodki.Api.Infrastructure.Persistence;
using Hangfire;
using Hangfire.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Каркас Hangfire (ADR 0005) на настоящей базе: расписание — в схеме <c>hangfire</c>, задача-образец выполняется самим
/// Hangfire и идемпотентна, дашборд — только администратору и только на чтение. В остальных тестах Hangfire выключен.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ScheduledJobsTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Hangfire_is_off_in_tests_unless_a_test_asks_for_it()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        _ = api.CreateClient();

        Assert.Null(api.Services.GetService<JobStorage>());
        Assert.Null(api.Services.GetService<IRecurringJobManager>());
    }

    [Fact]
    public async Task Token_purge_is_scheduled_hourly_by_Minsk_time_runs_in_hangfire_and_is_idempotent()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database, scheduledJobs: true);
        var (_, userId) = await api.CreatePlayerClientAsync();
        var storage = api.Services.GetRequiredService<JobStorage>();

        using (var connection = storage.GetConnection())
        {
            var job = Assert.Single(connection.GetRecurringJobs(), j => j.Id == ScheduledJobs.RefreshTokensJob);
            Assert.Equal("0 * * * *", job.Cron);
            Assert.Equal("Europe/Minsk", job.TimeZoneId);
            Assert.Equal(typeof(RefreshTokenRetention), job.Job.Type);
            Assert.Null(job.Error);
        }

        await using (var db = database.CreateContext())
        {
            // Таблицы Hangfire — в своей схеме, не в app (SchemaTests: все таблицы игры — в app).
            var tables = await db.Database
                .SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'hangfire'")
                .ToListAsync(Cancel);
            Assert.Contains("job", tables);
        }

        var tokens = api.Services.GetRequiredService<TokenService>();
        var live = tokens.CreateRefreshToken(userId, Guid.CreateVersion7()).Entity;
        var expired = tokens.CreateRefreshToken(userId, Guid.CreateVersion7()).Entity;
        expired.CreatedAt = api.Time.GetUtcNow().AddDays(-31);
        expired.ExpiresAt = api.Time.GetUtcNow().AddSeconds(-1);
        await using (var db = database.CreateContext())
        {
            db.RefreshTokens.AddRange(live, expired);
            await db.SaveChangesAsync(Cancel);
        }

        // Запуск «по кнопке» — тем же путём, что по расписанию: сервис из DI в своей области, токен отмены от Hangfire.
        var jobId = ((IRecurringJobManagerV2)api.Services.GetRequiredService<IRecurringJobManager>())
            .TriggerJob(ScheduledJobs.RefreshTokensJob);
        Assert.Equal("Succeeded", await FinalStateAsync(storage, jobId));
        Assert.Equal([live.Id], await TokensOfAsync(userId));

        // Повтор (Hangfire повторяет упавшие и догоняет пропущенный запуск после сна сервера) ничего лишнего не стирает.
        await using (var scope = api.Services.CreateAsyncScope())
        {
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<RefreshTokenRetention>().PurgeExpiredAsync(Cancel));
        }

        Assert.Equal([live.Id], await TokensOfAsync(userId));
    }

    [Fact]
    public async Task Dashboard_is_for_the_admin_only_and_read_only()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database, scheduledJobs: true);
        var (player, _) = await api.CreatePlayerClientAsync();
        var (admin, _) = await api.CreatePlayerClientAsync(UserRole.Admin);

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync(ScheduledJobs.DashboardPath, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await player.GetAsync(ScheduledJobs.DashboardPath, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await player.GetAsync($"{ScheduledJobs.DashboardPath}/recurring", Cancel)).StatusCode);

        var recurring = await admin.GetAsync($"{ScheduledJobs.DashboardPath}/recurring", Cancel);
        Assert.Equal(HttpStatusCode.OK, recurring.StatusCode);
        var page = await recurring.Content.ReadAsStringAsync(Cancel);
        Assert.Contains(ScheduledJobs.RefreshTokensJob, page);
        Assert.DoesNotContain("PostgreSQL Server", page); // хранилище (хост пула, имя базы) на странице не показывается

        // Только чтение: кнопки «запустить» нет, а запрос в обход неё не выполняется.
        using var trigger = new FormUrlEncodedContent([new("jobs[]", ScheduledJobs.RefreshTokensJob)]);
        var triggered = await admin.PostAsync($"{ScheduledJobs.DashboardPath}/recurring/trigger", trigger, Cancel);
        Assert.False(triggered.IsSuccessStatusCode, $"read-only дашборд выполнил команду: {(int)triggered.StatusCode}");
    }

    [Fact]
    public async Task Browser_opens_the_dashboard_with_the_token_in_the_address_once()
    {
        // Браузер не шлёт заголовок Authorization: токен приходит в адресе, сервер кладёт его в куку и убирает из адреса.
        database.RequireDatabase();
        await using var api = new ApiFactory(database, scheduledJobs: true);
        var (admin, _) = await api.CreatePlayerClientAsync(UserRole.Admin);
        var (player, _) = await api.CreatePlayerClientAsync();
        var browser = api.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });

        var first = await browser.GetAsync($"{ScheduledJobs.DashboardPath}?access_token={Token(admin)}", Cancel);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(ScheduledJobs.DashboardPath, first.Headers.Location?.OriginalString);
        var cookie = Assert.Single(first.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(ScheduledJobs.DashboardPath, Cancel)).StatusCode);

        // Токен игрока в адресе — та же проверка роли по базе: 403.
        var stranger = api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var denied = await stranger.GetAsync($"{ScheduledJobs.DashboardPath}?access_token={Token(player)}", Cancel);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    private static string Token(HttpClient client) => client.DefaultRequestHeaders.Authorization!.Parameter!;

    private async Task<List<Guid>> TokensOfAsync(Guid userId)
    {
        await using var db = database.CreateContext();
        return await db.RefreshTokens.Where(t => t.UserId == userId).Select(t => t.Id).ToListAsync(Cancel);
    }

    /// <summary>Ждёт, пока задача выйдет из очереди и выполнится (исполнитель у Hangfire один — обычно это секунды).</summary>
    private async Task<string?> FinalStateAsync(JobStorage storage, string jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        string? state = null;
        while (DateTime.UtcNow < deadline)
        {
            using (var connection = storage.GetConnection())
            {
                state = connection.GetStateData(jobId)?.Name;
            }

            if (state is "Succeeded" or "Failed" or "Deleted" or "Scheduled") // Scheduled — упала и ждёт повтора
            {
                return state;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), Cancel);
        }

        return state;
    }
}
