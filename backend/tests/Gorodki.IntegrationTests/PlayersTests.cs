using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gorodki.Api.Features.Leaderboards;
using Gorodki.Api.Features.Me;
using Gorodki.Api.Features.Players;
using Microsoft.EntityFrameworkCore;
using static Gorodki.IntegrationTests.RunRequests;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Карточка игрока — <c>GET /players/{id}</c> (PLAN.md, §3.16: без согласия — «Игрок #1234»). Задача #115 — сделано Claude
/// 15.10.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PlayersTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Player_who_agreed_is_shown_by_nickname_and_color()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (_, annaId) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        var (name, color) = await AgreeAsync(annaId);

        var card = await CardAsync(boris, annaId);

        Assert.Equal(new PlayerResponse(annaId, name, color, IsMe: false), card);
    }

    [Fact]
    public async Task Without_consent_the_name_is_the_same_pseudonym_as_in_the_rankings()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (_, annaId) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();

        var response = await boris.GetAsync($"/players/{annaId}", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var card = (await response.Content.ReadFromJsonAsync<PlayerResponse>(Json, Cancel))!;
        Assert.Equal((LeaderboardEndpoints.Pseudonym(annaId), false), (card.Name, card.IsMe));
        Assert.DoesNotContain(await DisplayNameAsync(annaId), await response.Content.ReadAsStringAsync(Cancel)); // ник не утёк
    }

    [Fact]
    public async Task Player_sees_his_own_nickname_even_without_consent()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();

        var card = await CardAsync(anna, annaId);

        Assert.Equal((await DisplayNameAsync(annaId), true), (card.Name, card.IsMe));
    }

    [Fact]
    public async Task Unknown_or_deleting_player_is_not_found()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();

        var unknown = await boris.GetAsync($"/players/{Guid.CreateVersion7()}", Cancel);
        Assert.Equal(HttpStatusCode.Accepted, (await anna.DeleteAsync("/me", Cancel)).StatusCode); // Анна удаляет аккаунт
        var deleting = await boris.GetAsync($"/players/{annaId}", Cancel);

        Assert.Equal((404, "player_not_found"), await ProblemOf(unknown));
        Assert.Equal((404, "player_not_found"), await ProblemOf(deleting));
    }

    [Fact]
    public async Task Consent_given_and_withdrawn_changes_the_card_at_once()
    {
        // Согласие — через сам адрес #71: карточка читает его при каждом запросе, как рейтинги.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        var nickname = await DisplayNameAsync(annaId);

        Assert.Equal(HttpStatusCode.OK, (await ConsentAsync(anna, enabled: true)).StatusCode);
        Assert.Equal(nickname, (await CardAsync(boris, annaId)).Name);

        Assert.Equal(HttpStatusCode.OK, (await ConsentAsync(anna, enabled: false)).StatusCode);
        Assert.Equal(LeaderboardEndpoints.Pseudonym(annaId), (await CardAsync(boris, annaId)).Name);
    }

    [Fact]
    public async Task The_card_holds_only_name_and_color_even_while_the_players_capture_is_hidden()
    {
        // Граница публичности (§3.16): у Анны свежий захват, другим он ещё не виден. Карточка ничего о нём не говорит — в ней
        // нет ни координат, ни времени, ни площади и очков: ровно четыре поля контракта.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        var claim = await Walks.WalkAndClaimAsync(Cancel, api, anna, Walks.Square(Walks.NewArea(), 0, 0, 100));
        await Walks.ProcessAsync(api, claim.RunId);

        var response = await boris.GetAsync($"/players/{annaId}", Cancel);

        var json = await response.Content.ReadAsStringAsync(Cancel);
        var fields = JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal);
        Assert.Equal(["colorIndex", "id", "isMe", "name"], fields);
        Problems.HasNoCoordinates(json);
    }

    [Fact]
    public async Task Without_sign_in_the_card_is_closed()
    {
        // Это проверяет уже контракт (вход), а не реализацию, — поэтому без Skip.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (_, annaId) = await api.CreatePlayerClientAsync();
        using var anonymous = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/players/{annaId}", Cancel)).StatusCode);
    }

    // MARK: — вспомогательное

    private Task<HttpResponseMessage> ConsentAsync(HttpClient client, bool enabled) =>
        client.PutAsJsonAsync("/me/public-profile", new PublicProfileRequest { Enabled = enabled }, Json, Cancel);

    private async Task<PlayerResponse> CardAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<PlayerResponse>($"/players/{id}", Json, Cancel))!;

    /// <summary>Игрок согласился показывать ник — прямо в базе: задача #71 (<c>PUT /me/public-profile</c>) может быть ещё не сделана.</summary>
    private async Task<(string Name, short Color)> AgreeAsync(Guid userId)
    {
        await using var db = database.CreateContext();
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(set => set.SetProperty(u => u.PublicProfile, true), Cancel);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, Cancel);
        return (user.DisplayName, user.ColorIndex);
    }

    private async Task<string> DisplayNameAsync(Guid userId)
    {
        await using var db = database.CreateContext();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).SingleAsync(Cancel);
    }

    private async Task<(int Status, string? Code)> ProblemOf(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        return ((int)response.StatusCode, problem.TryGetProperty("code", out var code) ? code.GetString() : null);
    }
}
