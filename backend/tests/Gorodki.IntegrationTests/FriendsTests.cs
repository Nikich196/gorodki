using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Leaderboards;
using Gorodki.Api.Features.Social;
using static Gorodki.IntegrationTests.RunRequests;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Друзья по коду — <c>/friends</c> (PLAN.md, §3.8: только взаимные, QR или ссылка). Задача #TBD-E14a для Егора: тесты со
/// <c>Skip</c> снимаются вместе с реализацией.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class FriendsTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(Skip = "ЗАДАЧА #TBD-E14a")]
    public async Task Friendship_is_mutual_by_code_and_names_follow_consent()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var annaCode = (await ListAsync(anna)).MyCode;
        Assert.Equal(annaCode, (await ListAsync(anna)).MyCode); // код постоянный

        var request = await Problems.BodyAsync<FriendResponse>(await AddAsync(boris, annaCode), Cancel);
        Assert.Equal((annaId, FriendStatus.Outgoing), (request.PlayerId, request.Status));
        var incoming = Assert.Single((await ListAsync(anna)).Friends);
        Assert.Equal((borisId, FriendStatus.Incoming, LeaderboardEndpoints.Pseudonym(borisId)), (incoming.PlayerId, incoming.Status, incoming.Name));

        Assert.Equal(HttpStatusCode.OK, (await anna.PostAsync($"/friends/{borisId}/accept", null, Cancel)).StatusCode);

        Assert.Equal(FriendStatus.Friend, Assert.Single((await ListAsync(anna)).Friends).Status);
        Assert.Equal(FriendStatus.Friend, Assert.Single((await ListAsync(boris)).Friends).Status);
    }

    [Fact(Skip = "ЗАДАЧА #TBD-E14a")]
    public async Task Adding_each_other_makes_friends_at_once_and_removing_ends_it_for_both()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        await AddAsync(boris, (await ListAsync(anna)).MyCode);

        var back = await Problems.BodyAsync<FriendResponse>(await AddAsync(anna, (await ListAsync(boris)).MyCode), Cancel);
        Assert.Equal((borisId, FriendStatus.Friend), (back.PlayerId, back.Status));

        Assert.Equal(HttpStatusCode.NoContent, (await boris.DeleteAsync($"/friends/{annaId}", Cancel)).StatusCode);
        Assert.Empty((await ListAsync(anna)).Friends);
        Assert.Equal((404, "friend_not_found"), await Problems.OfAsync(await boris.DeleteAsync($"/friends/{annaId}", Cancel), Cancel));
    }

    [Fact(Skip = "ЗАДАЧА #TBD-E14a")]
    public async Task Own_or_unknown_code_is_refused_and_a_stranger_cannot_accept()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (carl, _) = await api.CreatePlayerClientAsync();
        await AddAsync(boris, (await ListAsync(anna)).MyCode); // заявка Бориса — Анне, а не Карлу

        Assert.Equal((400, "friend_self"), await Problems.OfAsync(await AddAsync(anna, (await ListAsync(anna)).MyCode), Cancel));
        Assert.Equal((404, "friend_code_invalid"), await Problems.OfAsync(await AddAsync(anna, "0000-0000"), Cancel));
        Assert.Equal((404, "friend_request_not_found"), await Problems.OfAsync(
            await carl.PostAsync($"/friends/{borisId}/accept", null, Cancel), Cancel));
        Assert.Equal((404, "friend_not_found"), await Problems.OfAsync(await carl.DeleteAsync($"/friends/{borisId}", Cancel), Cancel));
    }

    private async Task<FriendsResponse> ListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<FriendsResponse>("/friends", Json, Cancel))!;

    private Task<HttpResponseMessage> AddAsync(HttpClient client, string code) =>
        client.PostAsJsonAsync("/friends", new AddFriendRequest { Code = code }, Json, Cancel);
}
