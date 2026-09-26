using System.Net.Http.Json;
using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Streaks;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Time;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Серия — <c>GET /me/streak</c> (PLAN.md, §3.7). Задача #TBD-E22 для Егора: тесты со <c>Skip</c> снимаются вместе с
/// реализацией. Само правило (пропуск, «Заморозка серии», граница суток по Минску) — чистая функция, её тесты без Docker.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class StreakTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(Skip = "ЗАДАЧА #TBD-E22")]
    public async Task New_player_has_no_streak()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync(newcomer: true);

        var streak = (await anna.GetFromJsonAsync<StreakResponse>("/me/streak", Json, Cancel))!;

        Assert.Equal(new StreakResponse(0, TodayCounted: false, FreezeActive: false), streak);
    }

    [Fact(Skip = "ЗАДАЧА #TBD-E22")]
    public async Task Days_in_a_row_with_a_finished_live_run_make_a_streak_and_a_gap_breaks_it()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync(newcomer: true);
        var today = GameClock.GameDayOf(api.Time.GetUtcNow());
        foreach (var daysAgo in new[] { 1, 2, 3, 5 }) // четыре дня назад — пропуск: серия — 3 дня до сегодняшнего
        {
            await AddRunAsync(api, annaId, today.AddDays(-daysAgo), RunSource.Live, 2_000);
        }

        await AddRunAsync(api, annaId, today, RunSource.Replay, 2_000); // повтор демо сегодня — не день серии

        var before = (await anna.GetFromJsonAsync<StreakResponse>("/me/streak", Json, Cancel))!;
        await AddRunAsync(api, annaId, today, RunSource.Live, 2_000);
        var after = (await anna.GetFromJsonAsync<StreakResponse>("/me/streak", Json, Cancel))!;

        Assert.Equal((3, false), (before.Days, before.TodayCounted)); // сегодняшний день ещё не пропущен
        Assert.Equal((4, true), (after.Days, after.TodayCounted));
    }

    /// <summary>Завершённый забег в полдень игровых суток <paramref name="day"/> — прямо в базе.</summary>
    private async Task AddRunAsync(ApiFactory api, Guid userId, DateOnly day, RunSource source, double meters)
    {
        await using (var scope = api.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<GameConfigStore>().GetCurrentAsync(Cancel);
        }

        var startedAt = SeasonCalendar.MinskMidnight(day).AddHours(12);
        await using var db = database.CreateContext();
        db.Runs.Add(new RunEntity
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            League = League.Run,
            Source = source,
            ConfigVersion = 1,
            StartedAt = startedAt,
            EndedAt = startedAt.AddMinutes(30),
            Status = RunStatus.Finished,
            CreatedAt = startedAt,
            DeviceId = Guid.NewGuid(),
            AppVersion = "0.1.0 (1)",
            MotionAuthorized = true,
            AcceptedMeters = meters,
        });
        await db.SaveChangesAsync(Cancel);
    }
}
