using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Fog;
using Gorodki.Api.Features.Inventory;
using Gorodki.Api.Features.Streaks;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Inventory;
using Gorodki.Domain.Time;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// «Припасы» и Рюкзак — <c>/me/inventory</c> (PLAN.md, §3.11; #140). Потолок «4 в сутки», ячейки и очередь «Радар» /
/// «Заморозка серии» — чистая функция <c>SupplyRulesTests</c> без Docker; расход «Радара» по пути — <c>FogStampTests</c>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class InventoryTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task New_player_has_an_empty_backpack_of_twelve_slots()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync(newcomer: true);

        var backpack = (await anna.GetFromJsonAsync<InventoryResponse>("/me/inventory", Json, Cancel))!;

        Assert.Equal((InventoryEndpoints.Slots, 0), (backpack.Slots, backpack.Items.Count));
    }

    [Fact]
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

    [Fact]
    public async Task Someone_elses_or_unknown_item_is_not_found()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (boris, _) = await api.CreatePlayerClientAsync();

        var response = await boris.PostAsync($"/me/inventory/{Guid.NewGuid()}/activate", null, Cancel);

        Assert.Equal((404, "item_not_found"), await Problems.OfAsync(response, Cancel));
        Assert.Equal(HttpStatusCode.OK, (await boris.GetAsync("/me/inventory", Cancel)).StatusCode);
    }

    [Fact]
    public async Task Activated_radar_widens_the_fog_of_the_next_run_and_counts_its_metres_down()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync(newcomer: true);
        var (boris, _) = await api.CreatePlayerClientAsync(newcomer: true);

        // Первая фишка — «Радар» на 3 км.
        var supplies = await WalkAndFinishAsync(Cancel, api, anna, Rectangle(NewArea(), 0, 0, 600, 500), startedAgo: TimeSpan.FromHours(1));
        await VisitAsync(api, supplies.Id);
        var radar = Assert.Single((await anna.GetFromJsonAsync<InventoryResponse>("/me/inventory", Json, Cancel))!.Items);
        Assert.Equal((ItemKind.Radar, SupplyRules.RadarMeters), (radar.Kind, radar.RemainingMeters));

        var activated = await anna.PostAsync($"/me/inventory/{radar.Id}/activate", null, Cancel);
        var twice = await anna.PostAsync($"/me/inventory/{radar.Id}/activate", null, Cancel);
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        Assert.True((await Problems.BodyAsync<InventoryItemResponse>(activated, Cancel)).Active);
        Assert.Equal((409, "item_active"), await Problems.OfAsync(twice, Cancel));

        // Следующий забег (начат после активации) — 600 м по прямой; Борис идёт так же без «Радара».
        api.Time.Advance(TimeSpan.FromMinutes(30));
        var area = NewArea();
        (double X, double Y)[] line = [(area.X, area.Y), (area.X + 600, area.Y)];
        var withRadar = await StampAsync(api, (await WalkAndFinishAsync(Cancel, api, anna, line)).Id);
        var without = await StampAsync(api, (await WalkAndFinishAsync(Cancel, api, boris, line)).Id);

        Assert.True(withRadar > 1.6 * without, $"с «Радаром» {withRadar} клеток, без — {without}"); // радиус ×2
        var left = Assert.Single((await anna.GetFromJsonAsync<InventoryResponse>("/me/inventory", Json, Cancel))!.Items);
        Assert.InRange(left.RemainingMeters!.Value, SupplyRules.RadarMeters - 610, SupplyRules.RadarMeters - 590);
    }

    [Fact]
    public async Task Activated_streak_freeze_is_what_the_streak_sees()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync(newcomer: true);
        var now = api.Time.GetUtcNow();
        var freezeId = Guid.NewGuid();
        await using (var db = database.CreateContext())
        {
            db.InventoryItems.Add(new InventoryItemEntity
            {
                Id = freezeId,
                UserId = annaId,
                Kind = ItemKind.StreakFreeze,
                ReceivedAt = now,
                ExpiresAt = now.AddDays(SupplyRules.LifetimeDays),
                GameDay = GameClock.GameDayOf(now),
            });
            await db.SaveChangesAsync(Cancel);
        }

        var before = (await anna.GetFromJsonAsync<StreakResponse>("/me/streak", Json, Cancel))!;
        Assert.Equal(HttpStatusCode.OK, (await anna.PostAsync($"/me/inventory/{freezeId}/activate", null, Cancel)).StatusCode);
        var after = (await anna.GetFromJsonAsync<StreakResponse>("/me/streak", Json, Cancel))!;

        Assert.False(before.FreezeActive);
        Assert.True(after.FreezeActive);
        var item = Assert.Single((await anna.GetFromJsonAsync<InventoryResponse>("/me/inventory", Json, Cancel))!.Items);
        Assert.True(item.Active && item.RemainingMeters is null); // ждёт пропуска — и лежит в Рюкзаке

        // Неактивированная фишка через 7 дней пропадает.
        await using (var db = database.CreateContext())
        {
            db.InventoryItems.Add(new InventoryItemEntity
            {
                Id = Guid.NewGuid(),
                UserId = annaId,
                Kind = ItemKind.Radar,
                ReceivedAt = now,
                ExpiresAt = now.AddDays(SupplyRules.LifetimeDays),
                RemainingMeters = SupplyRules.RadarMeters,
                GameDay = GameClock.GameDayOf(now),
            });
            await db.SaveChangesAsync(Cancel);
        }

        api.Time.Advance(TimeSpan.FromDays(SupplyRules.LifetimeDays));
        var later = (await anna.GetFromJsonAsync<InventoryResponse>("/me/inventory", Json, Cancel))!;
        Assert.Equal([freezeId], later.Items.Select(i => i.Id));
    }

    private static async Task<int?> StampAsync(ApiFactory api, Guid runId)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<FogProcessor>().StampRunAsync(runId, CancellationToken.None);
    }
}
