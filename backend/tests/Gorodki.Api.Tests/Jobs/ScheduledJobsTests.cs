using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Seasons;
using Gorodki.Api.Infrastructure.Jobs;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Gorodki.Api.Tests.Jobs;

/// <summary>
/// Каркас Hangfire без базы: что стоит в расписании и сколько соединений он берёт (на настоящей базе — одноимённые тесты
/// в <c>Gorodki.IntegrationTests</c>).
/// </summary>
public sealed class ScheduledJobsTests
{
    [Fact]
    public void Token_purge_is_scheduled_hourly_by_Minsk_time()
    {
        var jobs = new RecordedSchedule();

        ScheduledJobs.Register(jobs);

        var (_, job, cron, options) = Assert.Single(jobs.Added, j => j.Id == ScheduledJobs.RefreshTokensJob);
        Assert.Equal(typeof(RefreshTokenRetention), job.Type);
        Assert.Equal(nameof(RefreshTokenRetention.PurgeExpiredAsync), job.Method.Name);
        Assert.Equal("0 * * * *", cron);
        Assert.Equal("Europe/Minsk", options.TimeZone.Id);
    }

    [Fact]
    public void Season_rollover_is_checked_every_hour_by_Minsk_time_so_it_runs_at_the_seasons_midnight()
    {
        var jobs = new RecordedSchedule();

        ScheduledJobs.Register(jobs);

        var (_, job, cron, options) = Assert.Single(jobs.Added, j => j.Id == ScheduledJobs.SeasonRolloverJob);
        Assert.Equal(typeof(SeasonRollover), job.Type);
        Assert.Equal(nameof(SeasonRollover.RunIfDueAsync), job.Method.Name);
        Assert.Equal("0 * * * *", cron); // в том числе 00:00 — сезоны начинаются в полночь по Минску
        Assert.Equal("Europe/Minsk", options.TimeZone.Id);
    }

    [Fact]
    public void Hangfire_has_its_own_small_pool_in_the_same_database()
    {
        var builder = new NpgsqlConnectionStringBuilder(ScheduledJobs.StorageConnectionString(
            "postgresql://postgres.ref:secret@aws-0-eu-central-1.pooler.supabase.com:5432/postgres?sslmode=require"));

        Assert.Equal("aws-0-eu-central-1.pooler.supabase.com", builder.Host);
        Assert.Equal("postgres", builder.Database);
        Assert.Equal(SslMode.Require, builder.SslMode);
        Assert.Equal(3, builder.MaxPoolSize); // PLAN.md, §7.3: «приложение 8, Hangfire 3»
        Assert.Equal("gorodki-hangfire", builder.ApplicationName);
    }

    [Fact]
    public async Task Without_the_switch_hangfire_is_not_even_registered()
    {
        // Тестовые серверы выключают Hangfire: иначе он при старте пошёл бы в базу (здесь её нет вовсе).
        await using var app = UnreachableDatabase.Server();
        _ = app.CreateClient();

        Assert.Null(app.Services.GetService<JobStorage>());
        Assert.Null(app.Services.GetService<IRecurringJobManager>());
    }

    /// <summary>Расписание, которое только записывает, что в него поставили.</summary>
    private sealed class RecordedSchedule : IRecurringJobManager
    {
        public List<(string Id, Job Job, string Cron, RecurringJobOptions Options)> Added { get; } = [];

        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options) =>
            Added.Add((recurringJobId, job, cronExpression, options));

        public void Trigger(string recurringJobId) => throw new NotSupportedException();

        public void RemoveIfExists(string recurringJobId) => throw new NotSupportedException();
    }
}
