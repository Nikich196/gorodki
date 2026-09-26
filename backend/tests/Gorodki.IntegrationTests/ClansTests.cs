using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Clans;
using Gorodki.Api.Features.Leaderboards;
using Microsoft.EntityFrameworkCore;
using static Gorodki.IntegrationTests.RunRequests;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Кланы — <c>/clans</c> (PLAN.md, §3.3, §3.6). Задача #135 для Егора (E5a–c): тесты со <c>Skip</c> снимаются вместе с
/// реализацией (egor-server.md, раздел 3.2). Правила имени — ещё и в <c>ClanRulesTests</c> (без Docker), когда появится
/// <c>ClanRules</c>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ClansTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(Skip = "ЗАДАЧА #135")]
    public async Task Leader_creates_a_clan_and_others_join_by_its_code()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (carl, _) = await api.CreatePlayerClientAsync();

        var created = await CreateAsync(anna, UniqueName());
        Assert.Equal((ClanRole.Leader, false), (created.MyRole, created.Full));
        Assert.NotNull(created.InviteCode);

        var joined = await anna.PostAsJsonAsync("/clans/join", new JoinClanRequest { Code = created.InviteCode! }, Json, Cancel);
        Assert.Equal((409, "clan_already_member"), await Problems.OfAsync(joined, Cancel));
        Assert.Equal(HttpStatusCode.OK, (await JoinAsync(boris, created.InviteCode!)).StatusCode);
        var full = await Problems.BodyAsync<ClanResponse>(await JoinAsync(carl, created.InviteCode!), Cancel);

        Assert.True(full.Full); // трое — клан «полный»
        Assert.Equal([ClanRole.Leader, ClanRole.Member, ClanRole.Member], full.Members.Select(m => m.Role));
        var seenByBoris = (await boris.GetFromJsonAsync<ClanResponse>($"/clans/{created.Id}", Json, Cancel))!;
        Assert.Null(seenByBoris.InviteCode); // код — только лидеру и офицерам
        Assert.Equal(ClanRole.Member, seenByBoris.MyRole);
        Assert.Equal(created.Id, (await anna.GetFromJsonAsync<MyClanResponse>("/clans/mine", Json, Cancel))!.Clan?.Id);
        Assert.Contains(seenByBoris.Members, m => m.PlayerId == borisId && m.Me);
    }

    [Fact(Skip = "ЗАДАЧА #135")]
    public async Task Name_follows_the_rules_and_is_unique_ignoring_case()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        var name = UniqueName();
        await CreateAsync(anna, name);

        foreach (var bad in new[] { "ab", new string('а', 25), "Клан!", "   " })
        {
            Assert.Equal((400, "clan_name_invalid"), await Problems.OfAsync(await PostCreateAsync(boris, bad), Cancel));
        }

        Assert.Equal((409, "clan_name_taken"), await Problems.OfAsync(await PostCreateAsync(boris, name.ToUpperInvariant()), Cancel));
    }

    [Fact(Skip = "ЗАДАЧА #135")]
    public async Task A_clan_holds_at_most_twelve_players()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (leader, _) = await api.CreatePlayerClientAsync();
        var clan = await CreateAsync(leader, UniqueName());
        for (var i = 1; i < ClanEndpoints.MaxMembers; i++)
        {
            var (member, _) = await api.CreatePlayerClientAsync();
            Assert.Equal(HttpStatusCode.OK, (await JoinAsync(member, clan.InviteCode!)).StatusCode);
        }

        var (thirteenth, _) = await api.CreatePlayerClientAsync();

        Assert.Equal((409, "clan_full"), await Problems.OfAsync(await JoinAsync(thirteenth, clan.InviteCode!), Cancel));
    }

    [Fact(Skip = "ЗАДАЧА #135")]
    public async Task After_leaving_a_player_waits_72_hours_to_join_again_and_the_leader_passes_on()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync(); // клиенты — до сдвига часов: токен «из будущего» не пройдёт
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (carl, _) = await api.CreatePlayerClientAsync();
        var clan = await CreateAsync(anna, UniqueName());
        await JoinAsync(boris, clan.InviteCode!);
        await JoinAsync(carl, clan.InviteCode!);
        Assert.Equal(HttpStatusCode.OK, (await anna.PutAsJsonAsync(
            $"/clans/mine/members/{borisId}/role", new ClanRoleRequest { Role = ClanRole.Officer }, Json, Cancel)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await anna.PostAsync("/clans/mine/leave", null, Cancel)).StatusCode);

        var after = (await boris.GetFromJsonAsync<ClanResponse>($"/clans/{clan.Id}", Json, Cancel))!;
        Assert.Equal(ClanRole.Leader, after.MyRole); // ушёл лидер — лидер теперь офицер
        var mine = (await anna.GetFromJsonAsync<MyClanResponse>("/clans/mine", Json, Cancel))!;
        Assert.Null(mine.Clan);
        Assert.Equal(api.Time.GetUtcNow().Add(ClanEndpoints.JoinCooldown).ToUnixTimeMilliseconds(), mine.CanJoinAtMs);
        Assert.Equal((409, "clan_join_cooldown"), await Problems.OfAsync(await JoinAsync(anna, after.InviteCode!), Cancel));
        Assert.Equal((409, "clan_join_cooldown"), await Problems.OfAsync(await PostCreateAsync(anna, UniqueName()), Cancel));

        api.Time.Advance(ClanEndpoints.JoinCooldown);

        Assert.Equal(HttpStatusCode.OK, (await JoinAsync(anna, after.InviteCode!)).StatusCode);
    }

    [Fact(Skip = "ЗАДАЧА #135")]
    public async Task Only_the_clans_own_leader_or_officer_removes_members_and_a_stranger_gets_404()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (carl, carlId) = await api.CreatePlayerClientAsync();
        var (stranger, _) = await api.CreatePlayerClientAsync();
        var clan = await CreateAsync(anna, UniqueName());
        await JoinAsync(boris, clan.InviteCode!);
        await JoinAsync(carl, clan.InviteCode!);
        await CreateAsync(stranger, UniqueName()); // у чужого свой клан — и всё равно не исключит

        var byStranger = await stranger.DeleteAsync($"/clans/mine/members/{carlId}", Cancel);
        var byMember = await boris.DeleteAsync($"/clans/mine/members/{carlId}", Cancel);
        var byLeader = await anna.DeleteAsync($"/clans/mine/members/{carlId}", Cancel);

        Assert.Equal((404, "clan_member_not_found"), await Problems.OfAsync(byStranger, Cancel));
        Assert.Equal((403, "clan_forbidden"), await Problems.OfAsync(byMember, Cancel));
        Assert.Equal(HttpStatusCode.NoContent, byLeader.StatusCode);
        Assert.NotNull((await carl.GetFromJsonAsync<MyClanResponse>("/clans/mine", Json, Cancel))!.CanJoinAtMs); // 72 ч и ему
        Assert.DoesNotContain(
            (await anna.GetFromJsonAsync<ClanResponse>($"/clans/{clan.Id}", Json, Cancel))!.Members, m => m.PlayerId == carlId);
        Assert.Contains(
            (await anna.GetFromJsonAsync<ClanResponse>($"/clans/{clan.Id}", Json, Cancel))!.Members, m => m.PlayerId == borisId);
    }

    [Fact(Skip = "ЗАДАЧА #135")]
    public async Task Members_are_named_only_with_their_consent_and_officers_are_at_most_two()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var clan = await CreateAsync(anna, UniqueName());
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var (member, id) = await api.CreatePlayerClientAsync();
            await JoinAsync(member, clan.InviteCode!);
            ids.Add(id);
        }

        foreach (var id in ids.Take(ClanEndpoints.MaxOfficers))
        {
            Assert.Equal(HttpStatusCode.OK, (await SetRoleAsync(anna, id, ClanRole.Officer)).StatusCode);
        }

        Assert.Equal((409, "clan_officer_limit"), await Problems.OfAsync(await SetRoleAsync(anna, ids[2], ClanRole.Officer), Cancel));
        Assert.Equal((400, "clan_role_invalid"), await Problems.OfAsync(await SetRoleAsync(anna, ids[2], ClanRole.Leader), Cancel));

        var card = await anna.GetAsync($"/clans/{clan.Id}", Cancel);
        var body = await card.Content.ReadAsStringAsync(Cancel);
        await using var db = database.CreateContext();
        var names = await db.Users.Where(u => ids.Contains(u.Id)).Select(u => u.DisplayName).ToListAsync(Cancel);
        Assert.All(names, name => Assert.DoesNotContain(name, body)); // согласия нет — ники не утекли
        Assert.Contains(LeaderboardEndpoints.Pseudonym(ids[0]), body);
    }

    [Fact(Skip = "ЗАДАЧА #135")]
    public async Task Leader_renames_once_per_season_and_hues_come_from_the_palette()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        var hues = (await anna.GetFromJsonAsync<ClanHuesResponse>("/clans/hues", Json, Cancel))!;
        Assert.All(hues.Free, hue => Assert.InRange(hue, 0, ClanEndpoints.Hues - 1));
        var clan = await CreateAsync(anna, UniqueName());
        await JoinAsync(boris, clan.InviteCode!);

        var byMember = await boris.PutAsJsonAsync("/clans/mine/name", new RenameClanRequest { Name = UniqueName() }, Json, Cancel);
        var first = await anna.PutAsJsonAsync("/clans/mine/name", new RenameClanRequest { Name = UniqueName() }, Json, Cancel);
        var second = await anna.PutAsJsonAsync("/clans/mine/name", new RenameClanRequest { Name = UniqueName() }, Json, Cancel);
        var newCode = await Problems.BodyAsync<ClanResponse>(await anna.PostAsync("/clans/mine/code", null, Cancel), Cancel);

        Assert.Equal((403, "clan_forbidden"), await Problems.OfAsync(byMember, Cancel));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal((409, "clan_rename_limit"), await Problems.OfAsync(second, Cancel));
        Assert.NotEqual(clan.InviteCode, newCode.InviteCode);
        var (carl, _) = await api.CreatePlayerClientAsync();
        Assert.Equal((404, "clan_code_invalid"), await Problems.OfAsync(await JoinAsync(carl, clan.InviteCode!), Cancel)); // старый код не действует
        Assert.Equal((400, "clan_hue_invalid"), await Problems.OfAsync(
            await carl.PostAsJsonAsync("/clans", new CreateClanRequest { Name = UniqueName(), Hue = ClanEndpoints.Hues }, Json, Cancel), Cancel));
    }

    // MARK: — вспомогательное

    /// <summary>Уникальное допустимое название: база общая для всех тестов.</summary>
    private static string UniqueName() => $"Клан {Guid.NewGuid().ToString("N")[..8]}";

    private Task<HttpResponseMessage> PostCreateAsync(HttpClient client, string name) =>
        client.PostAsJsonAsync("/clans", new CreateClanRequest { Name = name }, Json, Cancel);

    private async Task<ClanResponse> CreateAsync(HttpClient client, string name)
    {
        var response = await PostCreateAsync(client, name);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await Problems.BodyAsync<ClanResponse>(response, Cancel);
    }

    private Task<HttpResponseMessage> JoinAsync(HttpClient client, string code) =>
        client.PostAsJsonAsync("/clans/join", new JoinClanRequest { Code = code }, Json, Cancel);

    private Task<HttpResponseMessage> SetRoleAsync(HttpClient client, Guid userId, ClanRole role) =>
        client.PutAsJsonAsync($"/clans/mine/members/{userId}/role", new ClanRoleRequest { Role = role }, Json, Cancel);
}
