using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Leaderboards;
using Gorodki.Api.Features.Me;
using Gorodki.Api.Features.Social;
using Microsoft.EntityFrameworkCore;
using static Gorodki.IntegrationTests.RunRequests;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Друзья по коду — <c>/friends</c> (PLAN.md, §3.8: только взаимные, QR или ссылка). Задача #143 — сделано Claude 15.10.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class FriendsTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task The_code_is_issued_once_in_the_invite_format_and_found_however_it_is_typed()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();

        // Первые запросы одновременно — код один: второй не перезапишет первый.
        var codes = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ListAsync(anna)));
        var code = Assert.Single(codes.Select(c => c.MyCode).Distinct());
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$", code); // Крокфорд: без I, L, O, U

        // Набрали строчными и с пробелами — тот же код.
        var added = await Problems.BodyAsync<FriendResponse>(await AddAsync(boris, $"  {code.ToLowerInvariant()} "), Cancel);
        Assert.Equal((annaId, FriendStatus.Outgoing), (added.PlayerId, added.Status));
    }

    [Fact]
    public async Task Repeats_change_nothing_and_the_list_goes_incoming_friends_outgoing()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (carl, carlId) = await api.CreatePlayerClientAsync();
        var (dina, dinaId) = await api.CreatePlayerClientAsync();
        var annaCode = (await ListAsync(anna)).MyCode;

        // Борис добавил Анну дважды (ответ потерялся) — одна заявка.
        Assert.Equal(FriendStatus.Outgoing, await StatusAsync(AddAsync(boris, annaCode)));
        Assert.Equal(FriendStatus.Outgoing, await StatusAsync(AddAsync(boris, annaCode)));
        Assert.Single((await ListAsync(boris)).Friends);

        // Принять дважды — тот же ответ; добавить уже друга — тоже «друзья».
        await AddAsync(carl, annaCode);
        Assert.Equal(FriendStatus.Friend, await StatusAsync(AcceptAsync(anna, carlId)));
        Assert.Equal(FriendStatus.Friend, await StatusAsync(AcceptAsync(anna, carlId)));
        Assert.Equal(FriendStatus.Friend, await StatusAsync(AddAsync(carl, annaCode)));

        // Анна сама добавила Дину: у Анны — входящая Бориса, друг Карл, исходящая Дине — в этом порядке.
        await AddAsync(anna, (await ListAsync(dina)).MyCode);
        var list = (await ListAsync(anna)).Friends;
        Assert.Equal(
            [(borisId, FriendStatus.Incoming), (carlId, FriendStatus.Friend), (dinaId, FriendStatus.Outgoing)],
            list.Select(f => (f.PlayerId, f.Status)));
    }

    [Fact]
    public async Task Declining_or_withdrawing_a_request_removes_it_for_both()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var annaCode = (await ListAsync(anna)).MyCode;

        await AddAsync(boris, annaCode);
        Assert.Equal(HttpStatusCode.NoContent, (await anna.DeleteAsync($"/friends/{borisId}", Cancel)).StatusCode); // отклонила
        Assert.Empty((await ListAsync(boris)).Friends);
        Assert.Equal((404, "friend_request_not_found"), await Problems.OfAsync(await AcceptAsync(anna, borisId), Cancel));

        await AddAsync(boris, annaCode); // снова — снова заявка
        Assert.Equal(HttpStatusCode.NoContent, (await boris.DeleteAsync($"/friends/{annaId}", Cancel)).StatusCode); // отозвал
        Assert.Empty((await ListAsync(anna)).Friends);
    }

    [Fact]
    public async Task Names_follow_consent_and_friendship_does_not_replace_it()
    {
        // Правило карточки игрока (#115): дружба — не согласие на показ ника; цвет — тот же, что игрок видит у себя в /me.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        await AddAsync(boris, (await ListAsync(anna)).MyCode);
        await AddAsync(anna, (await ListAsync(boris)).MyCode);
        var me = (await boris.GetFromJsonAsync<MeResponse>("/me", Json, Cancel))!;

        var hidden = Assert.Single((await ListAsync(anna)).Friends);
        Assert.Equal((LeaderboardEndpoints.Pseudonym(borisId), me.ColorIndex), (hidden.Name, hidden.ColorIndex));

        var consent = await boris.PutAsJsonAsync("/me/public-profile", new PublicProfileRequest { Enabled = true }, Json, Cancel);
        Assert.Equal(HttpStatusCode.OK, consent.StatusCode);
        var response = await anna.GetAsync("/friends", Cancel);
        Assert.Equal(me.DisplayName, Assert.Single((await Problems.BodyAsync<FriendsResponse>(response, Cancel)).Friends).Name);
        Problems.HasNoCoordinates(await response.Content.ReadAsStringAsync(Cancel)); // дружба не открывает координаты
    }

    [Fact]
    public async Task Blocking_ends_friendship_and_requests_and_keeps_new_ones_out()
    {
        // Обещание приложения: «дружба и заявки с ним снимутся». Пока блокировка стоит, заявку не отправить ни в одну
        // сторону; снятая блокировка дружбу не возвращает.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (carl, carlId) = await api.CreatePlayerClientAsync();
        var annaCode = (await ListAsync(anna)).MyCode;
        var borisCode = (await ListAsync(boris)).MyCode;
        await AddAsync(boris, annaCode);
        await AddAsync(anna, borisCode); // друзья
        await AddAsync(carl, annaCode); // и заявка Карла — Анне
        // Карл передумал и заблокировал Анну.
        Assert.Equal(HttpStatusCode.NoContent, (await carl.PutAsync($"/me/blocks/{annaId}", null, Cancel)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await anna.PutAsync($"/me/blocks/{borisId}", null, Cancel)).StatusCode);

        Assert.Empty((await ListAsync(anna)).Friends); // ни Бориса, ни заявки Карла
        Assert.Empty((await ListAsync(boris)).Friends);
        // Кого заблокировали, тому — «нет такого кода»: блокировка себя не выдаёт. Кто заблокировал — знает об этом сам.
        Assert.Equal((404, "friend_code_invalid"), await Problems.OfAsync(await AddAsync(boris, annaCode), Cancel));
        Assert.Equal((400, "friend_blocked"), await Problems.OfAsync(await AddAsync(anna, borisCode), Cancel));
        var carlCode = (await ListAsync(carl)).MyCode;
        Assert.Equal((404, "friend_code_invalid"), await Problems.OfAsync(await AddAsync(anna, carlCode), Cancel));
        Assert.Equal((404, "friend_request_not_found"), await Problems.OfAsync(await AcceptAsync(anna, carlId), Cancel));
        await using (var db = database.CreateContext())
        {
            Assert.False(await db.FriendLinks.AnyAsync(f => f.UserId == annaId || f.FriendId == annaId, Cancel)); // в базе — тоже
        }

        Assert.Equal(HttpStatusCode.NoContent, (await anna.DeleteAsync($"/me/blocks/{borisId}", Cancel)).StatusCode);
        Assert.Empty((await ListAsync(anna)).Friends);
        Assert.Equal(FriendStatus.Outgoing, await StatusAsync(AddAsync(boris, annaCode)));
    }

    [Fact]
    public async Task Adding_each_other_at_the_same_moment_makes_friends()
    {
        // Встречные заявки в одну секунду идут по очереди (блокировка пары): вторая видит первую и отвечает «друзья».
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        var (annaCode, borisCode) = ((await ListAsync(anna)).MyCode, (await ListAsync(boris)).MyCode);

        var answers = await Task.WhenAll(AddAsync(anna, borisCode), AddAsync(boris, annaCode));

        var statuses = await Task.WhenAll(answers.Select(a => Problems.BodyAsync<FriendResponse>(a, Cancel)));
        Assert.Contains(FriendStatus.Friend, statuses.Select(s => s.Status));
        Assert.Equal(FriendStatus.Friend, Assert.Single((await ListAsync(anna)).Friends).Status);
        Assert.Equal(FriendStatus.Friend, Assert.Single((await ListAsync(boris)).Friends).Status);
    }

    [Fact]
    public async Task A_deleting_account_is_gone_from_friends_and_cannot_be_found_by_code()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (carl, _) = await api.CreatePlayerClientAsync();
        var borisCode = (await ListAsync(boris)).MyCode;
        await AddAsync(boris, (await ListAsync(anna)).MyCode); // заявка Бориса — Анне

        Assert.Equal(HttpStatusCode.Accepted, (await boris.DeleteAsync("/me", Cancel)).StatusCode); // Борис удаляет аккаунт

        Assert.Empty((await ListAsync(anna)).Friends);
        Assert.Equal((404, "friend_request_not_found"), await Problems.OfAsync(await AcceptAsync(anna, borisId), Cancel));
        Assert.Equal((404, "friend_code_invalid"), await Problems.OfAsync(await AddAsync(carl, borisCode), Cancel));
    }

    private async Task<FriendsResponse> ListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<FriendsResponse>("/friends", Json, Cancel))!;

    private Task<HttpResponseMessage> AddAsync(HttpClient client, string code) =>
        client.PostAsJsonAsync("/friends", new AddFriendRequest { Code = code }, Json, Cancel);

    private Task<HttpResponseMessage> AcceptAsync(HttpClient client, Guid requesterId) =>
        client.PostAsync($"/friends/{requesterId}/accept", null, Cancel);

    private async Task<FriendStatus> StatusAsync(Task<HttpResponseMessage> response) =>
        (await Problems.BodyAsync<FriendResponse>(await response, Cancel)).Status;
}
