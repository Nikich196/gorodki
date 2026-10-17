using System.Net.Http.Json;
using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Streaks;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Серия — <c>GET /me/streak</c> (PLAN.md, §3.7): какие забеги из базы становятся днями серии. Само правило (пропуск,
/// «Заморозка серии», граница суток по Минску, километр за сутки) — чистая функция, её тесты без Docker:
/// <c>StreakRulesTests</c>. Заморозки выдаёт Рюкзак (#140): активация — <c>InventoryTests</c>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class StreakTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task New_player_has_no_streak()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync(newcomer: true);

        var streak = (await anna.GetFromJsonAsync<StreakResponse>("/me/streak", Json, Cancel))!;

        Assert.Equal(new StreakResponse(0, TodayCounted: false, FreezeActive: false), streak);
    }

    [Fact]
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

    [Fact]
    public async Task Only_own_live_runs_with_a_kilometre_a_day_make_days()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync(newcomer: true);
        var (_, borisId) = await api.CreatePlayerClientAsync(newcomer: true);
        var today = GameClock.GameDayOf(api.Time.GetUtcNow());
        await AddRunAsync(api, borisId, today.AddDays(-1), RunSource.Live, 5_000); // чужой забег — не день Анны
        await AddRunAsync(api, annaId, today.AddDays(-1), RunSource.Live, 600, League.Bike);
        await AddRunAsync(api, annaId, today.AddDays(-2), RunSource.Live, 2_000);

        var shortDay = (await anna.GetFromJsonAsync<StreakResponse>("/me/streak", Json, Cancel))!;
        await AddRunAsync(api, annaId, today.AddDays(-1), RunSource.Live, 400); // вчера вместе с «Вело» — километр
        var fullDay = (await anna.GetFromJsonAsync<StreakResponse>("/me/streak", Json, Cancel))!;

        Assert.Equal(new StreakResponse(0, TodayCounted: false, FreezeActive: false), shortDay); // 600 м вчера — пропуск
        Assert.Equal(new StreakResponse(2, TodayCounted: false, FreezeActive: false), fullDay);
    }

    [Fact]
    public async Task A_run_from_last_night_still_being_counted_does_not_burn_the_streak()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync(newcomer: true);
        var today = GameClock.GameDayOf(api.Time.GetUtcNow());
        await AddRunAsync(api, annaId, today.AddDays(-3), RunSource.Live, 2_000);
        await AddRunAsync(api, annaId, today.AddDays(-2), RunSource.Live, 2_000);
        var lastNight = await AddRunAsync(api, annaId, today.AddDays(-1), RunSource.Live, meters: null); // путь ещё не посчитан

        var waiting = (await anna.GetFromJsonAsync<StreakResponse>("/me/streak", Json, Cancel))!;
        await using (var db = database.CreateContext())
        {
            await db.Runs.Where(r => r.Id == lastNight).ExecuteUpdateAsync(set => set.SetProperty(r => r.AcceptedMeters, 2_000), Cancel);
        }

        var counted = (await anna.GetFromJsonAsync<StreakResponse>("/me/streak", Json, Cancel))!;

        Assert.Equal((2, false), (waiting.Days, waiting.TodayCounted)); // не сгорела
        Assert.Equal((3, false), (counted.Days, counted.TodayCounted));
    }

    [Fact]
    public async Task Day_of_a_run_is_by_the_server_clock()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync(newcomer: true);
        var today = GameClock.GameDayOf(api.Time.GetUtcNow());

        // Часы телефона спешили на 30 минут: по ним забег начат сегодня в 00:10, а по часам сервера — вчера в 23:40.
        var skew = TimeSpan.FromMinutes(30);
        await AddRunAsync(
            api, annaId, today, RunSource.Live, 2_000, startedAt: SeasonCalendar.MinskMidnight(today).AddMinutes(10), clockSkew: skew);

        var streak = (await anna.GetFromJsonAsync<StreakResponse>("/me/streak", Json, Cancel))!;

        Assert.Equal(new StreakResponse(1, TodayCounted: false, FreezeActive: false), streak);
    }

    /// <summary>
    /// Завершённый забег игровых суток <paramref name="day"/> — прямо в базе: по умолчанию в полдень, часы телефона точные.
    /// <paramref name="startedAt"/> — по часам телефона.
    /// </summary>
    private async Task<Guid> AddRunAsync(
        ApiFactory api,
        Guid userId,
        DateOnly day,
        RunSource source,
        double? meters,
        League league = League.Run,
        DateTimeOffset? startedAt = null,
        TimeSpan clockSkew = default)
    {
        await using (var scope = api.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<GameConfigStore>().GetCurrentAsync(Cancel);
        }

        var start = startedAt ?? SeasonCalendar.MinskMidnight(day).AddHours(12);
        var id = Guid.CreateVersion7();
        await using var db = database.CreateContext();
        db.Runs.Add(new RunEntity
        {
            Id = id,
            UserId = userId,
            League = league,
            Source = source,
            ConfigVersion = 1,
            StartedAt = start,
            EndedAt = start.AddMinutes(30),
            Status = RunStatus.Finished,
            CreatedAt = start,
            ClockSkewMs = (long)clockSkew.TotalMilliseconds,
            DeviceId = Guid.NewGuid(),
            AppVersion = "0.1.0 (1)",
            MotionAuthorized = true,
            AcceptedMeters = meters,
        });
        await db.SaveChangesAsync(Cancel);
        return id;
    }
}
