using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Inventory;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// «Припасы» и Рюкзак — <c>/me/inventory</c> (PLAN.md, §3.11). Задача #TBD-E11 для Егора: тесты со <c>Skip</c> снимаются
/// вместе с реализацией. Детерминированный тип и потолок «4 в сутки» удобнее проверять чистой функцией без Docker.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class InventoryTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(Skip = "ЗАДАЧА #TBD-E11")]
    public async Task New_player_has_an_empty_backpack_of_twelve_slots()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync(newcomer: true);

        var backpack = (await anna.GetFromJsonAsync<InventoryResponse>("/me/inventory", Json, Cancel))!;

        Assert.Equal((InventoryEndpoints.Slots, 0), (backpack.Slots, backpack.Items.Count));
    }

    [Fact(Skip = "ЗАДАЧА #TBD-E11")]
    public async Task Two_kilometres_give_one_item_that_lives_seven_days_and_is_not_activated_during_a_run()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync(newcomer: true);

        // Прямоугольник 600 × 500 м — 2,2 км пути; забег начат час назад: его конец уже публичен, путь посчитан.
        var run = await WalkAndFinishAsync(Cancel, api, anna, Rectangle(NewArea(), 0, 0, 600, 500), startedAgo: TimeSpan.FromHours(1));
        await VisitAsync(api, run.Id);
        var item = Assert.Single((await anna.GetFromJsonAsync<InventoryResponse>("/me/inventory", Json, Cancel))!.Items);
        Assert.Equal(TimeSpan.FromDays(7).TotalMilliseconds, item.ExpiresAtMs - item.ReceivedAtMs);
        Assert.False(item.Active);

        await StartWalkAsync(Cancel, api, anna); // идёт новый забег
        var during = await anna.PostAsync($"/me/inventory/{item.Id}/activate", null, Cancel);

        Assert.Equal((409, "run_active"), await Problems.OfAsync(during, Cancel));
    }

    [Fact(Skip = "ЗАДАЧА #TBD-E11")]
    public async Task Someone_elses_or_unknown_item_is_not_found()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (boris, _) = await api.CreatePlayerClientAsync();

        var response = await boris.PostAsync($"/me/inventory/{Guid.NewGuid()}/activate", null, Cancel);

        Assert.Equal((404, "item_not_found"), await Problems.OfAsync(response, Cancel));
        Assert.Equal(HttpStatusCode.OK, (await boris.GetAsync("/me/inventory", Cancel)).StatusCode);
    }
}
