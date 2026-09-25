using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Fog;
using Gorodki.Api.Features.Leaderboards;
using Gorodki.Api.Features.Me;
using Gorodki.Api.Features.Seasons;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Своя статистика — <c>GET /me/stats</c>. Задача #116 для Егора: тесты со <c>Skip</c> снимаются вместе с реализацией
/// (CONTRIBUTING.md, «Задачи для друга»). Числа сверяются с тем, что игрок и так видит: <c>GET /fog/summary</c>,
/// <c>GET /seasons</c>, <c>GET /leaderboards/exploration</c>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class StatsTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(Skip = "ЗАДАЧА #116")]
    public async Task New_player_has_zeros_and_only_these_numbers()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync(newcomer: true);

        var response = await client.GetAsync("/me/stats", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Cancel);
        var stats = JsonSerializer.Deserialize<MyStatsResponse>(body, Json)!;
        Assert.Equal((0, 0.0, 0.0, 0.0, (int?)null), (stats.Runs, stats.DistanceMeters, stats.ExploredSquareMeters, stats.SeasonExploredSquareMeters, stats.ExplorationRank));
        Assert.Equal((await SeasonsAsync(client)).Current, stats.Season);

        // Ровно эти поля. Площади своей земли нет намеренно: она выдала бы ещё скрытый чужой захват (§3.16).
        using var json = JsonDocument.Parse(body);
        Assert.Equal(
            ["distanceMeters", "explorationRank", "exploredSquareMeters", "runs", "season", "seasonExploredSquareMeters"],
            json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact(Skip = "ЗАДАЧА #116")]
    public async Task Only_own_finished_live_runs_count()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync(newcomer: true);
        var (_, borisId) = await api.CreatePlayerClientAsync(newcomer: true);
        await AddRunAsync(api, annaId, RunSource.Live, RunStatus.Finished, 3_000.44);
        await AddRunAsync(api, annaId, RunSource.Live, RunStatus.Abandoned, 1_000); // закрыт сервером — тоже пробежан
        await AddRunAsync(api, annaId, RunSource.Live, RunStatus.Finished, null); // метры ещё не посчитаны — 0
        await AddRunAsync(api, annaId, RunSource.Replay, RunStatus.Finished, 5_000); // повтор демо — не считается
        await AddRunAsync(api, annaId, RunSource.Live, RunStatus.Active, null); // идёт сейчас — не считается
        await AddRunAsync(api, borisId, RunSource.Live, RunStatus.Finished, 7_000); // чужой

        var stats = await StatsAsync(anna);

        Assert.Equal((3, 4_000.4), (stats.Runs, stats.DistanceMeters));
    }

    [Fact(Skip = "ЗАДАЧА #116")]
    public async Task Explored_area_is_the_fog_summary_of_both_layers_for_all_time_and_this_season()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync(); // клиент — до сдвига часов: токен «из будущего» не прошёл бы проверку
        await IntoSeasonAsync(api, anna);

        var run = await WalkAndFinishAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100));
        await using (var scope = api.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<FogProcessor>().StampRunAsync(run.Id, Cancel) > 0);
        }

        var stats = await StatsAsync(anna);
        var summary = (await anna.GetFromJsonAsync<FogSummaryResponse>("/fog/summary", Json, Cancel))!;
        var season = (await SeasonsAsync(anna)).Current;

        Assert.NotNull(season);
        Assert.Equal(season, stats.Season);
        var allTime = summary.Layers.Where(l => l.Season is null).Sum(l => l.AreaSquareMeters);
        var thisSeason = summary.Layers.Where(l => l.Season == season).Sum(l => l.AreaSquareMeters);
        Assert.True(allTime > 10_000, $"Туман не открылся: {allTime} м²");
        Assert.InRange(stats.ExploredSquareMeters, allTime - 0.2, allTime + 0.2); // в сводке каждый слой округлён до 0,1
        Assert.InRange(stats.SeasonExploredSquareMeters, thisSeason - 0.2, thisSeason + 0.2);
    }

    [Fact(Skip = "ЗАДАЧА #116")]
    public async Task Rank_is_the_players_place_in_the_latest_snapshot_like_in_the_rankings()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();

        // Срез за последние сутки, что есть в базе (их пишет ежедневный срез), и старый — за сутки до них.
        await using (var db = database.CreateContext())
        {
            var latest = await db.LeaderboardSnapshots.Where(s => s.Board == LeaderboardBoard.Exploration).MaxAsync(s => (DateOnly?)s.Day, Cancel)
                ?? GameClock.GameDayOf(api.Time.GetUtcNow());
            db.LeaderboardSnapshots.AddRange(
                Row(latest, LeaderboardLayer.Total, SeasonCalendar.AllTime, rank: 3),
                Row(latest.AddDays(-1), LeaderboardLayer.Total, SeasonCalendar.AllTime, rank: 7), // старый срез
                Row(latest, LeaderboardLayer.Foot, SeasonCalendar.AllTime, rank: 9), // другой слой
                Row(latest, LeaderboardLayer.Total, season: 0, rank: 1)); // сезон, а не «за всё время»
            await db.SaveChangesAsync(Cancel);
        }

        var board = (await anna.GetFromJsonAsync<ExplorationLeaderboardResponse>("/leaderboards/exploration?layer=total", Json, Cancel))!;

        Assert.Equal(3, board.Mine?.Rank); // то же место, что на экране рейтинга
        Assert.Equal(3, (await StatsAsync(anna)).ExplorationRank);
        Assert.Null((await StatsAsync(boris)).ExplorationRank); // Бориса в срезе нет

        LeaderboardSnapshotEntity Row(DateOnly day, LeaderboardLayer layer, int season, int rank) =>
            new() { Day = day, Board = LeaderboardBoard.Exploration, Layer = layer, Season = season, UserId = annaId, Value = 12_345, Rank = rank };
    }

    [Fact]
    public async Task Without_sign_in_stats_are_closed()
    {
        // Это проверяет уже контракт (вход), а не реализацию, — поэтому без Skip.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        using var anonymous = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/me/stats", Cancel)).StatusCode);
    }

    // MARK: — вспомогательное

    private async Task<MyStatsResponse> StatsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<MyStatsResponse>("/me/stats", Json, Cancel))!;

    private async Task<SeasonsResponse> SeasonsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<SeasonsResponse>("/seasons", Json, Cancel))!;

    /// <summary>Часы — в идущий сезон: если сейчас межсезонье, на полдень после начала ближайшего.</summary>
    private async Task IntoSeasonAsync(ApiFactory api, HttpClient client)
    {
        var calendar = await SeasonsAsync(client);
        if (calendar.Current is null)
        {
            var now = api.Time.GetUtcNow().ToUnixTimeMilliseconds();
            var next = calendar.Seasons.First(s => s.StartsAtMs > now);
            api.Time.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(next.StartsAtMs).AddHours(12));
        }
    }

    /// <summary>Забег прямо в базе: для счёта забегов и метров точки не нужны.</summary>
    private async Task AddRunAsync(ApiFactory api, Guid userId, RunSource source, RunStatus status, double? meters)
    {
        await using (var scope = api.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<GameConfigStore>().GetCurrentAsync(Cancel); // версия 1 конфига в новой базе
        }

        var startedAt = api.Time.GetUtcNow().AddDays(-2);
        await using var db = database.CreateContext();
        db.Runs.Add(new RunEntity
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            League = League.Run,
            Source = source,
            ConfigVersion = 1,
            StartedAt = startedAt,
            EndedAt = status == RunStatus.Active ? null : startedAt.AddHours(1),
            Status = status,
            CreatedAt = startedAt,
            DeviceId = Guid.NewGuid(),
            AppVersion = "0.1.0 (1)",
            MotionAuthorized = true,
            AcceptedMeters = meters,
        });
        await db.SaveChangesAsync(Cancel);
    }
}
