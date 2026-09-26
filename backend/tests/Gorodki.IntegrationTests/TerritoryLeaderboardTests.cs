using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Leaderboards;
using Gorodki.Domain.Leagues;
using static Gorodki.IntegrationTests.RunRequests;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Рейтинг территории — <c>GET /leaderboards/territory</c> (PLAN.md, §3.5). Задача #TBD-E7 для Егора: тесты со <c>Skip</c>
/// снимаются вместе с реализацией. Тесты самого среза (удержание по видимой земле, повтор не начисляет дважды, захват до
/// границы публичности не даёт очков в срезе, итоговый проход в 04:00) пишутся вместе с задачей Hangfire — по образцу
/// <c>ScoringTests</c> и <c>LeaderboardTests</c>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TerritoryLeaderboardTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(Skip = "ЗАДАЧА #TBD-E7")]
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

    [Fact(Skip = "ЗАДАЧА #TBD-E7")]
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

    [Fact(Skip = "ЗАДАЧА #TBD-E7")]
    public async Task Without_a_league_the_board_is_the_run_league()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();

        var board = (await anna.GetFromJsonAsync<TerritoryLeaderboardResponse>("/leaderboards/territory?season=0", Json, Cancel))!;

        Assert.Equal(League.Run, board.League);
    }
}
