using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Me;
using static Gorodki.IntegrationTests.RunRequests;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Согласие на показ профиля — <c>PUT /me/public-profile</c> (PLAN.md, §3.16). Задача #71 для Егора: тесты со <c>Skip</c>
/// снимаются вместе с реализацией (CONTRIBUTING.md, «Задачи для друга»).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PublicProfileTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(Skip = "ЗАДАЧА #71")]
    public async Task Consent_is_turned_on_and_off()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, userId) = await api.CreatePlayerClientAsync();

        var on = await SetAsync(client, enabled: true);
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        var profile = (await on.Content.ReadFromJsonAsync<MeResponse>(Json, Cancel))!;
        Assert.Equal((userId, true), (profile.Id, profile.PublicProfile));
        Assert.True((await MeAsync(client)).PublicProfile);

        var off = await SetAsync(client, enabled: false);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await off.Content.ReadFromJsonAsync<MeResponse>(Json, Cancel))!.PublicProfile);
        Assert.False((await MeAsync(client)).PublicProfile);
    }

    [Fact(Skip = "ЗАДАЧА #71")]
    public async Task Repeating_the_same_answer_changes_nothing()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();

        Assert.Equal(HttpStatusCode.OK, (await SetAsync(client, enabled: true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(client, enabled: true)).StatusCode);
        Assert.True((await MeAsync(client)).PublicProfile);
    }

    [Fact(Skip = "ЗАДАЧА #71")]
    public async Task Only_the_players_own_consent_changes()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();

        await SetAsync(anna, enabled: true);

        Assert.True((await MeAsync(anna)).PublicProfile);
        Assert.False((await MeAsync(boris)).PublicProfile);
    }

    [Fact]
    public async Task Without_sign_in_or_answer_the_request_is_refused()
    {
        // Это проверяет уже контракт (вход и обязательное поле), а не реализацию, — поэтому без Skip.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();
        using var anonymous = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await SetAsync(anonymous, enabled: true)).StatusCode);
        var empty = await client.PutAsJsonAsync("/me/public-profile", new { }, Json, Cancel);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    private Task<HttpResponseMessage> SetAsync(HttpClient client, bool enabled) =>
        client.PutAsJsonAsync("/me/public-profile", new PublicProfileRequest(enabled), Json, Cancel);

    private async Task<MeResponse> MeAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<MeResponse>("/me", Json, Cancel))!;
}
