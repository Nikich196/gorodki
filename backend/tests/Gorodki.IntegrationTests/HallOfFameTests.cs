using System.Net.Http.Json;
using Gorodki.Api.Features.HallOfFame;
using Gorodki.Api.Features.Seasons;
using Gorodki.Api.Features.Territory;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Зал славы — <c>GET /hall-of-fame</c> (PLAN.md, §3.4). Задача #TBD-E8 для Егора: тесты со <c>Skip</c> снимаются вместе с
/// реализацией. Тесты снимка (один раз по итогу закрытого сезона, повтор ничего не меняет, удаление аккаунта обезличивает
/// строку) пишутся вместе с задачей Hangfire — опора: <c>ScoreBook.FinalTotalsAsync</c> и сезон с закрытием в
/// <c>ScoringTests</c>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class HallOfFameTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(Skip = "ЗАДАЧА #TBD-E8")]
    public async Task A_season_that_is_still_going_is_not_in_the_hall()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var seasons = (await anna.GetFromJsonAsync<SeasonsResponse>("/seasons", Json, Cancel))!;

        // Очки Анны уже видны всем — но сезон идёт: снимок делается только по итогу закрытого сезона.
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100))).RunId);
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        var hall = (await anna.GetFromJsonAsync<HallOfFameResponse>("/hall-of-fame", Json, Cancel))!;

        Assert.DoesNotContain(hall.Seasons, s => s.Season == seasons.Current);
        Assert.DoesNotContain(hall.Seasons.SelectMany(s => s.Entries), e => e.PlayerId == annaId);
        Assert.Equal(hall.Seasons.Select(s => s.Season).OrderDescending(), hall.Seasons.Select(s => s.Season)); // новые сверху
    }
}
