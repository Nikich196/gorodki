using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Inbox;
using Gorodki.Api.Features.Territory;
using Microsoft.EntityFrameworkCore;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// «Входящие» — <c>GET /inbox</c>, <c>POST /inbox/read</c> (PLAN.md, §3.17). Задача #139 для Егора: тесты со <c>Skip</c>
/// снимаются вместе с реализацией. Тест о нападении ждёт ещё и C10 (события уведомлений после границы — задача Claude).
/// Бюджет пушей — <c>NotificationBudgetTests</c> без Docker, когда появится <c>NotificationBudget</c>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class InboxTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(Skip = "ЗАДАЧА #139")]
    public async Task New_player_has_an_empty_inbox_and_marking_read_is_idempotent()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync(newcomer: true);

        var inbox = (await anna.GetFromJsonAsync<InboxResponse>("/inbox", Json, Cancel))!;
        var read = new InboxReadRequest { UpToAtMs = api.Time.GetUtcNow().ToUnixTimeMilliseconds() };

        Assert.Equal((0, 0, (string?)null), (inbox.Items.Count, inbox.Unread, inbox.NextCursor));
        Assert.Equal(HttpStatusCode.NoContent, (await anna.PostAsJsonAsync("/inbox/read", read, Json, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await anna.PostAsJsonAsync("/inbox/read", read, Json, Cancel)).StatusCode);
    }

    [Fact(Skip = "ЗАДАЧА #139")]
    public async Task Broken_cursor_is_rejected()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();

        var response = await anna.GetAsync("/inbox?cursor=%%%не-курсор", Cancel);

        Assert.Equal((400, "inbox_cursor_invalid"), await Problems.OfAsync(response, Cancel));
    }

    [Fact(Skip = "ЗАДАЧА #139 (и C10)")]
    public async Task An_attack_on_my_land_arrives_only_with_the_map_and_without_the_attackers_nickname()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);

        // Борис взял половину земли Анны: захват ещё скрыт — Анна о нём не знает ни по карте, ни по «Входящим».
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100))).RunId);
        var appliedAt = api.Time.GetUtcNow();
        Assert.DoesNotContain((await InboxAsync(anna)).Items, i => i.Kind == InboxKind.Attack);

        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        var inbox = await InboxAsync(anna);

        var attack = Assert.Single(inbox.Items, i => i.Kind == InboxKind.Attack);
        Assert.True(attack.AtMs >= (appliedAt + TerritoryReader.PublicDelay).ToUnixTimeMilliseconds()); // время — видимости
        Assert.Equal(1, inbox.Unread);
        Assert.Equal(4, attack.Id.Version); // случайный номер: в UUIDv7 зашито время
        await using var db = database.CreateContext();
        var borisName = await db.Users.Where(u => u.Id == borisId).Select(u => u.DisplayName).SingleAsync(Cancel);
        Assert.DoesNotContain(borisName, attack.Text); // согласия Бориса нет
        Problems.HasNoCoordinates(await (await anna.GetAsync("/inbox", Cancel)).Content.ReadAsStringAsync(Cancel));

        await anna.PostAsJsonAsync("/inbox/read", new InboxReadRequest { UpToAtMs = attack.AtMs }, Json, Cancel);
        Assert.Equal(0, (await InboxAsync(anna)).Unread);
    }

    private async Task<InboxResponse> InboxAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<InboxResponse>("/inbox", Json, Cancel))!;
}
