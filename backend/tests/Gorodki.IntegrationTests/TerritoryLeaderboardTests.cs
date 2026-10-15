using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Leaderboards;
using Gorodki.Api.Features.Scoring;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Рейтинг территории — <c>GET /leaderboards/territory</c> (PLAN.md, §3.5), задача #136 (E7, сделано Claude 07.10): места
/// каждые 5 минут (plan-deviations, №15) — очки попадают в них первым проходом после своей границы публичности и не раньше;
/// удержание по видимой земле — раз в сутки, сколько бы проходов ни было; итоговый проход в момент закрытия сезона (04:00).
/// Время — настоящие сезоны (С0 с 15.10, С1 с 30.11), как в <c>ScoringTests</c>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TerritoryLeaderboardTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Wrong_league_or_season_is_rejected()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();

        foreach (var query in new[] { "?league=walk", "?league=1", "?season=-1" })
        {
            var response = await anna.GetAsync($"/leaderboards/territory{query}", Cancel);
            Assert.Equal((400, "leaderboard_invalid"), await Problems.OfAsync(response, Cancel));
        }
    }

    [Fact]
    public async Task Season_without_a_snapshot_is_empty_and_preliminary()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();

        var response = await anna.GetAsync("/leaderboards/territory?league=bike&season=99", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var board = await Problems.BodyAsync<TerritoryLeaderboardResponse>(response, Cancel);
        Assert.Equal((League.Bike, (int?)99, false), (board.League, board.Season, board.Final));
        Assert.Empty(board.Entries);
        Assert.Null(board.Mine);
    }

    [Fact]
    public async Task Without_a_league_the_board_is_the_run_league()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();

        var board = (await anna.GetFromJsonAsync<TerritoryLeaderboardResponse>("/leaderboards/territory?season=0", Json, Cancel))!;

        Assert.Equal(League.Run, board.League);
    }

    private static readonly DateTimeOffset SeasonOne = new(2026, 11, 29, 21, 0, 0, TimeSpan.Zero); // 30.11 00:00 по Минску

    [Fact]
    public async Task Hold_is_paid_once_a_day_for_visible_touched_land_however_many_passes_and_places_count_only_visible_points()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync(); // клиенты — до сдвига часов
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var day = new DateOnly(2026, 11, 18); // Сезон 0
        var midnight = SeasonCalendar.MinskMidnight(day.AddDays(1));
        await ForgetAsync(day.AddDays(1), finalSeason: 0); // итог С0 мог снять тест ниже: часы у каждого теста свои

        GoTo(api, SeasonCalendar.MinskMidnight(day).AddHours(10));
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100))).RunId); // 1 га
        GoTo(api, midnight - TimeSpan.FromMinutes(10));
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Square(NewArea(), 0, 0, 100))).RunId); // скрыт до 00:10
        GoTo(api, midnight + TimeSpan.FromSeconds(30));
        var paidAt = api.Time.GetUtcNow();

        Assert.True(await RunAsync(api) > 0);

        var board = (await anna.GetFromJsonAsync<TerritoryLeaderboardResponse>("/leaderboards/territory?league=run&season=0", Json, Cancel))!;
        Assert.Equal(("2026-11-19", false), (board.Day, board.Final));
        Assert.Equal(await VisiblePointsAsync(annaId, 0, api.Time.GetUtcNow()), board.Mine!.Points); // захват и удержание
        Assert.True(board.Mine.Points > 10);
        // Очки захвата Бориса станут видны только в 00:10 — в проходе 00:00 его нет совсем.
        Assert.Null((await boris.GetFromJsonAsync<TerritoryLeaderboardResponse>("/leaderboards/territory?season=0", Json, Cancel))!.Mine);
        Assert.DoesNotContain(board.Entries, e => e.Name.Contains(borisId.ToString("N")[^10..], StringComparison.Ordinal));

        // Проходы каждые 5 минут до 00:30 и ещё один в полдень: места переснимаются, удержание за 18.11 — нет.
        for (var at = midnight + TimeSpan.FromMinutes(5); at <= midnight + TimeSpan.FromMinutes(30); at += TimeSpan.FromMinutes(5))
        {
            GoTo(api, at);
            await RunAsync(api);
        }

        GoTo(api, midnight + TimeSpan.FromHours(12));
        await RunAsync(api);

        await using (var db = database.CreateContext())
        {
            var hold = await db.ScoreEvents.AsNoTracking().SingleAsync(e => e.UserId == annaId && e.Kind == ScoreKind.Hold, Cancel);
            Assert.Equal((0, day, 10, paidAt), (hold.Season, hold.GameDay, hold.Points, hold.VisibleAt)); // 100 соток × 0,1
            // Захват Бориса в 23:50 в 00:00 был скрыт: его земли «на карте» не было — нет и удержания, и позже его не доначислить.
            Assert.False(await db.ScoreEvents.AnyAsync(e => e.UserId == borisId && e.Kind == ScoreKind.Hold, Cancel));
        }

        // А очки захвата Бориса в местах — с 00:10, как и сам захват на карте.
        var borisLater = (await boris.GetFromJsonAsync<TerritoryLeaderboardResponse>("/leaderboards/territory?season=0", Json, Cancel))!;
        Assert.Equal(await VisiblePointsAsync(borisId, 0, api.Time.GetUtcNow()), borisLater.Mine!.Points);
        Assert.Equal("2026-11-19", borisLater.Day);
    }

    [Fact]
    public async Task Points_enter_the_places_at_their_public_boundary_which_is_a_five_minute_pass_and_not_a_millisecond_before()
    {
        // Граница публичности — всегда на сетке по 5 минут (TerritoryReader.PublicAt), а проход идёт каждые 5 минут по той же
        // сетке: очки попадают в места проходом на своей границе. Захват, ещё скрытый задержкой, — нет.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var day = new DateOnly(2026, 11, 24); // Сезон 0
        await ForgetAsync(day, finalSeason: 0);
        GoTo(api, SeasonCalendar.MinskMidnight(day).AddHours(12));
        var claim = await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100));
        await ProcessAsync(api, claim.RunId);
        DateTimeOffset visibleAt;
        await using (var db = database.CreateContext())
        {
            visibleAt = (await db.ScoreEvents.AsNoTracking().SingleAsync(e => e.CaptureId == claim.CaptureId, Cancel)).VisibleAt;
        }

        Assert.Equal(0, visibleAt.UtcTicks % TerritoryReader.RevealStep.Ticks);
        GoTo(api, visibleAt - TimeSpan.FromMinutes(2)); // Борис берёт землю за 2 минуты до границы Анны — его захват ещё скрыт
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Square(NewArea(), 0, 0, 100))).RunId);

        GoTo(api, visibleAt - TimeSpan.FromMilliseconds(1));
        await RunAsync(api);
        Assert.Null((await anna.GetFromJsonAsync<TerritoryLeaderboardResponse>("/leaderboards/territory?season=0", Json, Cancel))!.Mine);

        GoTo(api, visibleAt);
        await RunAsync(api);
        var board = (await anna.GetFromJsonAsync<TerritoryLeaderboardResponse>("/leaderboards/territory?season=0", Json, Cancel))!;
        Assert.Equal(await VisiblePointsAsync(annaId, 0, visibleAt), board.Mine!.Points);
        Assert.True(board.Mine.Points > 0);
        Assert.Equal("2026-11-24", board.Day); // срез — сегодняшний
        Assert.Null((await boris.GetFromJsonAsync<TerritoryLeaderboardResponse>("/leaderboards/territory?season=0", Json, Cancel))!.Mine);
        Assert.DoesNotContain(board.Entries, e => e.Name.Contains(borisId.ToString("N")[^10..], StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_servers_at_once_pay_the_hold_once_and_leave_one_place_per_player()
    {
        // Во время деплоя задача может пойти на двух экземплярах сразу: отметка удержания проверяется ещё раз под блокировкой
        // (6), места пишутся заменой под той же блокировкой.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var day = new DateOnly(2026, 11, 21);
        await ForgetAsync(day.AddDays(1), finalSeason: 0);
        GoTo(api, SeasonCalendar.MinskMidnight(day).AddHours(10));
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100))).RunId);
        GoTo(api, SeasonCalendar.MinskMidnight(day.AddDays(1)) + TimeSpan.FromSeconds(30));

        await Task.WhenAll(RunAsync(api), RunAsync(api));

        await using var db = database.CreateContext();
        Assert.Equal(1, await db.ScoreEvents.CountAsync(e => e.UserId == annaId && e.Kind == ScoreKind.Hold, Cancel));
        Assert.Equal(1, await db.LeaderboardSnapshots.CountAsync(
            s => s.Board == LeaderboardBoard.Territory && s.UserId == annaId && s.Day == day.AddDays(1), Cancel));
    }

    [Fact]
    public async Task Last_season_is_preliminary_after_midnight_and_final_once_at_its_close()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        await ForgetAsync(GameClock.GameDayOf(SeasonOne), finalSeason: 0);

        GoTo(api, SeasonOne - TimeSpan.FromHours(2)); // Борис — в 22:00: к полуночи его очки уже видны
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Square(NewArea(), 0, 0, 100))).RunId);
        GoTo(api, SeasonOne - TimeSpan.FromMinutes(10)); // Анна — в 23:50 последнего дня С0
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100))).RunId);
        GoTo(api, SeasonOne + TimeSpan.FromSeconds(30));
        await RunAsync(api);

        var preliminary = (await anna.GetFromJsonAsync<TerritoryLeaderboardResponse>("/leaderboards/territory?season=0", Json, Cancel))!;
        Assert.Equal(("2026-11-30", false, (TerritoryLeaderboardEntry?)null), (preliminary.Day, preliminary.Final, preliminary.Mine));
        var borisPreliminary = (await boris.GetFromJsonAsync<TerritoryLeaderboardResponse>("/leaderboards/territory?season=0", Json, Cancel))!.Mine;
        Assert.Equal(await VisiblePointsAsync(borisId, 0, SeasonOne + TimeSpan.FromSeconds(30)), borisPreliminary!.Points);

        var closesAt = SeasonOne + ScoreBook.CloseGrace;
        GoTo(api, closesAt - TimeSpan.FromMilliseconds(1));
        await RunAsync(api);
        Assert.False((await anna.GetFromJsonAsync<TerritoryLeaderboardResponse>("/leaderboards/territory?season=0", Json, Cancel))!.Final);

        GoTo(api, closesAt);
        Assert.True(await RunAsync(api) > 0);
        var final = (await anna.GetFromJsonAsync<TerritoryLeaderboardResponse>("/leaderboards/territory?season=0", Json, Cancel))!;
        Assert.Equal(("2026-11-30", true), (final.Day, final.Final));
        Assert.Equal(await VisiblePointsAsync(annaId, 0, closesAt), final.Mine!.Points); // захват 23:50 вошёл в итог
        Assert.True(final.Mine.Points > 0);

        GoTo(api, closesAt + TimeSpan.FromDays(8)); // и через неделю, когда ежедневные срезы уже стёрты, итог на месте
        await RunAsync(api);
        var later = (await anna.GetFromJsonAsync<TerritoryLeaderboardResponse>("/leaderboards/territory?season=0", Json, Cancel))!;
        Assert.Equal((final.Day, true, final.Mine.Points), (later.Day, later.Final, later.Mine?.Points));
    }

    // MARK: — вспомогательное

    private static void GoTo(ApiFactory api, DateTimeOffset moment) => api.Time.Advance(moment - api.Time.GetUtcNow());

    private static async Task<int> RunAsync(ApiFactory api)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TerritorySnapshots>().RunIfDueAsync(CancellationToken.None);
    }

    /// <summary>Очки игрока в «Беге» за сезон, видимые на момент.</summary>
    private async Task<int> VisiblePointsAsync(Guid userId, int season, DateTimeOffset at)
    {
        await using var db = database.CreateContext();
        return await ScoreBook.Visible(db, at).Where(e => e.UserId == userId && e.League == League.Run && e.Season == season).SumAsync(e => e.Points, Cancel);
    }

    /// <summary>
    /// База общая: срез этих суток (и итог сезона) мог сделать другой тест — забываем, чтобы проверить свой. Срезы более
    /// поздних суток — тоже: рейтинг показывает последний срез, и чужой поздний срез подменил бы свой (порядок тестов в
    /// классе зависит от пути сборки).
    /// </summary>
    private async Task ForgetAsync(DateOnly day, int? finalSeason)
    {
        await using var db = database.CreateContext();
        await db.JobRuns.Where(j => (j.Job == TerritorySnapshots.DailyJob && j.Key == day.DayNumber)
            || (j.Job == TerritorySnapshots.FinalJob && j.Key == finalSeason)).ExecuteDeleteAsync(Cancel);
        await db.LeaderboardSnapshots.Where(s => s.Board == LeaderboardBoard.Territory && (s.Day >= day || (s.Final && s.Season == finalSeason)))
            .ExecuteDeleteAsync(Cancel);
    }
}
