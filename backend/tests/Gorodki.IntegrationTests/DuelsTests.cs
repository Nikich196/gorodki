using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Duels;
using Gorodki.Api.Features.Social;
using Gorodki.Api.Features.Territory;
using Gorodki.Domain.Leagues;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Дуэли — <c>/duels</c> (PLAN.md, §3.14). Задача #TBD-E17 для Егора: тесты со <c>Skip</c> снимаются вместе с реализацией
/// (друзья — из E14a). Итог и ставки — задача Hangfire, её тесты — вместе с ней (вызвать задачу напрямую, повтор ничего не
/// меняет, фишки переходят одной транзакцией).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class DuelsTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(Skip = "ЗАДАЧА #TBD-E17")]
    public async Task Challenge_is_accepted_by_the_friend_and_a_pair_duels_once_a_week()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        await BefriendAsync(anna, boris);

        var challenge = await ChallengeAsync(anna, borisId, days: 3);
        Assert.Equal(HttpStatusCode.Created, challenge.StatusCode);
        var pending = await Problems.BodyAsync<DuelResponse>(challenge, Cancel);
        Assert.Equal((DuelStatus.Pending, (long?)null), (pending.Status, pending.StartsAtMs));

        var accepted = await Problems.BodyAsync<DuelResponse>(
            await boris.PostAsJsonAsync($"/duels/{pending.Id}/accept", new AcceptDuelRequest(), Json, Cancel), Cancel);

        var now = api.Time.GetUtcNow();
        Assert.Equal(DuelStatus.Active, accepted.Status);
        Assert.Equal((now.ToUnixTimeMilliseconds(), now.AddDays(3).ToUnixTimeMilliseconds()), (accepted.StartsAtMs, accepted.EndsAtMs));
        Assert.Equal(borisId, accepted.Me.PlayerId); // «я» — всегда спрашивающий
        Assert.Contains((await anna.GetFromJsonAsync<DuelsResponse>("/duels", Json, Cancel))!.Duels, d => d.Id == pending.Id);
        Assert.Equal((409, "duel_pair_week"), await Problems.OfAsync(await ChallengeAsync(anna, borisId, days: 1), Cancel));
    }

    [Fact(Skip = "ЗАДАЧА #TBD-E17")]
    public async Task Strangers_young_accounts_and_wrong_terms_are_refused()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (_, carlId) = await api.CreatePlayerClientAsync(); // не друг
        var (young, _) = await api.CreatePlayerClientAsync(newcomer: true);
        await BefriendAsync(anna, boris);
        await BefriendAsync(young, boris);

        Assert.Equal((404, "duel_opponent_not_found"), await Problems.OfAsync(await ChallengeAsync(anna, carlId, days: 3), Cancel));
        Assert.Equal((400, "duel_invalid"), await Problems.OfAsync(await ChallengeAsync(anna, borisId, days: 2), Cancel));
        Assert.Equal((409, "duel_account_too_new"), await Problems.OfAsync(await ChallengeAsync(young, borisId, days: 1), Cancel));
        var noSegment = new CreateDuelRequest { OpponentId = borisId, League = League.Run, Metric = DuelMetric.SegmentTime, Days = 1 };
        var segmentless = await anna.PostAsJsonAsync("/duels", noSegment, Json, Cancel);
        Assert.Equal((400, "duel_invalid"), await Problems.OfAsync(segmentless, Cancel)); // без отрезка
    }

    [Fact(Skip = "ЗАДАЧА #TBD-E17")]
    public async Task At_most_two_duels_go_at_once_and_a_stranger_sees_none_of_them()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (stranger, _) = await api.CreatePlayerClientAsync();
        Guid first = default;
        for (var i = 0; i < DuelEndpoints.MaxActive; i++)
        {
            var (friend, friendId) = await api.CreatePlayerClientAsync();
            await BefriendAsync(anna, friend);
            var duel = await Problems.BodyAsync<DuelResponse>(await ChallengeAsync(anna, friendId, days: 7), Cancel);
            await friend.PostAsJsonAsync($"/duels/{duel.Id}/accept", new AcceptDuelRequest(), Json, Cancel);
            first = first == default ? duel.Id : first;
        }

        var (third, thirdId) = await api.CreatePlayerClientAsync();
        await BefriendAsync(anna, third);

        Assert.Equal((409, "duel_limit"), await Problems.OfAsync(await ChallengeAsync(anna, thirdId, days: 1), Cancel));
        Assert.Equal((404, "duel_not_found"), await Problems.OfAsync(await stranger.GetAsync($"/duels/{first}", Cancel), Cancel));
        Assert.Equal((404, "duel_not_found"), await Problems.OfAsync(
            await stranger.PostAsJsonAsync($"/duels/{first}/accept", new AcceptDuelRequest(), Json, Cancel), Cancel));
        Assert.Equal((404, "duel_not_found"), await Problems.OfAsync(await stranger.PostAsync($"/duels/{first}/decline", null, Cancel), Cancel));
    }

    [Fact(Skip = "ЗАДАЧА #TBD-E17")]
    public async Task Live_score_shows_the_opponents_capture_only_after_the_public_boundary()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        await BefriendAsync(anna, boris);
        var duel = await Problems.BodyAsync<DuelResponse>(await ChallengeAsync(anna, borisId, days: 1, DuelMetric.CapturedArea), Cancel);
        await boris.PostAsJsonAsync($"/duels/{duel.Id}/accept", new AcceptDuelRequest(), Json, Cancel);

        // Борис взял землю — захват ещё скрыт: по счёту дуэли нельзя понять, что он бежит прямо сейчас.
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Square(NewArea(), 0, 0, 100))).RunId);
        var hidden = (await anna.GetFromJsonAsync<DuelResponse>($"/duels/{duel.Id}", Json, Cancel))!;
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        var shown = (await anna.GetFromJsonAsync<DuelResponse>($"/duels/{duel.Id}", Json, Cancel))!;

        Assert.True(hidden.Opponent.Score is null or 0, $"Счёт соперника до границы: {hidden.Opponent.Score}");
        Assert.InRange(shown.Opponent.Score ?? 0, 9_500, 10_500);
    }

    // MARK: — вспомогательное

    private Task<HttpResponseMessage> ChallengeAsync(HttpClient client, Guid opponentId, int days, DuelMetric metric = DuelMetric.Distance) =>
        client.PostAsJsonAsync(
            "/duels", new CreateDuelRequest { OpponentId = opponentId, League = League.Run, Metric = metric, Days = days }, Json, Cancel);

    /// <summary>Друзья по коду (E14a).</summary>
    private async Task BefriendAsync(HttpClient first, HttpClient second)
    {
        var firstCode = (await first.GetFromJsonAsync<FriendsResponse>("/friends", Json, Cancel))!.MyCode;
        var secondCode = (await second.GetFromJsonAsync<FriendsResponse>("/friends", Json, Cancel))!.MyCode;
        await second.PostAsJsonAsync("/friends", new AddFriendRequest { Code = firstCode }, Json, Cancel);
        var back = await first.PostAsJsonAsync("/friends", new AddFriendRequest { Code = secondCode }, Json, Cancel);
        Assert.Equal(FriendStatus.Friend, (await Problems.BodyAsync<FriendResponse>(back, Cancel)).Status);
    }
}
