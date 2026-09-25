using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gorodki.Api.Features.Leaderboards;
using Gorodki.Api.Features.Players;
using Microsoft.EntityFrameworkCore;
using static Gorodki.IntegrationTests.RunRequests;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Карточка игрока — <c>GET /players/{id}</c> (PLAN.md, §3.16: без согласия — «Игрок #1234»). Задача #115 для Егора: тесты
/// со <c>Skip</c> снимаются вместе с реализацией (CONTRIBUTING.md, «Задачи для друга»).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PlayersTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(Skip = "ЗАДАЧА #115")]
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

    [Fact(Skip = "ЗАДАЧА #115")]
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

    [Fact(Skip = "ЗАДАЧА #115")]
    public async Task Player_sees_his_own_nickname_even_without_consent()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();

        var card = await CardAsync(anna, annaId);

        Assert.Equal((await DisplayNameAsync(annaId), true), (card.Name, card.IsMe));
    }

    [Fact(Skip = "ЗАДАЧА #115")]
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
