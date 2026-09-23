using Gorodki.Api.Features.Captures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Визиты на настоящей базе (PLAN.md, §3.3): прогулка по своей земле освежает её и раз в 20 ч добавляет уровень;
/// первые и последние 200 м забега и меньше 50 м внутри куска — не визит.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class VisitsTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Walk_through_own_land_a_day_later_levels_it_up_once()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync(); // до сдвига часов: токен «из будущего» не прошёл бы проверку
        var area = NewArea();
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromHours(21));

        // 700 м по прямой через квадрат: первые и последние 200 м не в счёт, внутри остаётся 100 м.
        var run = await WalkAndFinishAsync(Cancel, api, anna, [(area.X - 300, area.Y + 50), (area.X + 400, area.Y + 50)]);

        Assert.Contains(run.Id, await ReadyAsync(api));
        Assert.Equal(1, await VisitAsync(api, run.Id));
        Assert.Null(await VisitAsync(api, run.Id)); // забег считается один раз
        Assert.DoesNotContain(run.Id, await ReadyAsync(api));
        var piece = Assert.Single(await LandAsync(annaId));
        Assert.Equal(2, piece.Level);
        Assert.Equal(piece.LastVisitAt, piece.LastLevelUpAt);

        // Визит — по часам сервера: последняя точка внутри квадрата — на 400-м метре прогулки, начатой 20 минут назад.
        var expected = DateTimeOffset.FromUnixTimeMilliseconds(run.StartedAtMs) + TimeSpan.FromSeconds(400 / 1.4);
        Assert.InRange(piece.LastVisitAt, expected - TimeSpan.FromSeconds(5), expected + TimeSpan.FromSeconds(5));

        await using var db = database.CreateContext();
        Assert.Equal(1, await db.Runs.Where(r => r.Id == run.Id).Select(r => r.VisitedParcels).SingleAsync(Cancel));
    }

    [Fact]
    public async Task Land_at_the_start_of_a_run_is_not_visited()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        var captured = Assert.Single(await LandAsync(annaId));
        api.Time.Advance(TimeSpan.FromHours(21));

        // Старт посреди своего квадрата — «у дома»: 50 м внутри приходятся на первые 200 м.
        var run = await WalkAndFinishAsync(Cancel, api, anna, [(area.X + 50, area.Y + 50), (area.X + 750, area.Y + 50)]);

        Assert.Equal(0, await VisitAsync(api, run.Id));
        var piece = Assert.Single(await LandAsync(annaId));
        Assert.Equal((1, captured.LastVisitAt), (piece.Level, piece.LastVisitAt));
    }

    [Fact]
    public async Task Cutting_a_corner_is_not_a_visit()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        var captured = Assert.Single(await LandAsync(annaId));
        api.Time.Advance(TimeSpan.FromHours(21));

        // Диагональ x + y = 25 срезает угол квадрата: внутри ~35 м, меньше 50.
        var run = await WalkAndFinishAsync(Cancel, api, anna, [(area.X - 325, area.Y + 350), (area.X + 375, area.Y - 350)]);

        Assert.Equal(0, await VisitAsync(api, run.Id));
        var piece = Assert.Single(await LandAsync(annaId));
        Assert.Equal((1, captured.LastVisitAt), (piece.Level, piece.LastVisitAt));
    }

    private static async Task<List<Guid>> ReadyAsync(ApiFactory api)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<VisitProcessor>().RunsReadyAsync(1_000, CancellationToken.None);
    }

    private static async Task<int?> VisitAsync(ApiFactory api, Guid runId)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<VisitProcessor>().ProcessRunAsync(runId, CancellationToken.None);
    }

    private async Task<List<Gorodki.Api.Infrastructure.Persistence.ParcelEntity>> LandAsync(Guid userId)
    {
        await using var db = database.CreateContext();
        return await db.Parcels.AsNoTracking().Where(p => p.OwnerId == userId).ToListAsync(Cancel);
    }
}
