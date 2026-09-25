using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Leagues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Помощники границы публичности для чисел и событий о чужих действиях (§3.16; docs/guides/egor-server.md, раздел 4):
/// площадь своей земли без скрытых захватов и <c>visible_at</c>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PublicBoundaryTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Visible_owned_area_does_not_give_away_a_hidden_capture_of_the_players_land()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        Assert.Equal(0, await VisibleOwnedAreaAsync(api, annaId));
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        Assert.InRange(await VisibleOwnedAreaAsync(api, annaId), 9_500, 10_500);

        // Борис взял правую половину квадрата Анны (L1), Анна обвела ещё квадрат рядом — оба захвата ещё скрыты.
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100))).RunId);
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 400, 0, 100))).RunId);

        await using (var db = database.CreateContext())
        {
            var real = (await db.Parcels.AsNoTracking().Where(p => p.OwnerId == annaId).ToListAsync(Cancel)).Sum(p => p.Geometry.Area);
            Assert.InRange(real, 14_500, 15_500); // по настоящим кускам вышло бы «−5 000 м²» — захват Бориса раньше карты
        }

        // Как видят остальные: захват Бориса ещё не вычтен, свежий квадрат Анны ещё не прибавлен.
        Assert.InRange(await VisibleOwnedAreaAsync(api, annaId), 9_500, 10_500);

        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        Assert.InRange(await VisibleOwnedAreaAsync(api, annaId), 14_500, 15_500);
        Assert.Equal(0, await VisibleOwnedAreaAsync(api, annaId, League.Bike));

        // Угасшая земля — «призрак» на карте, но уже не своя: не считается.
        api.Time.Advance(TimeSpan.FromDays(7));
        Assert.Equal(0, await VisibleOwnedAreaAsync(api, annaId));
    }

    [Fact]
    public async Task Events_about_a_player_become_visible_with_the_map_and_demo_at_once()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (_, playerId) = await api.CreatePlayerClientAsync();
        var (_, demoId) = await api.CreatePlayerClientAsync(UserRole.Demo);
        var applied = new DateTimeOffset(2026, 11, 16, 12, 1, 0, TimeSpan.Zero);

        await using var scope = api.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<TerritoryReader>();

        Assert.Equal(TerritoryReader.PublicAt(applied, TerritoryReader.PublicDelay), await reader.VisibleAtAsync(playerId, applied, Cancel));
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 12, 25, 0, TimeSpan.Zero), await reader.VisibleAtAsync(playerId, applied, Cancel));
        Assert.Equal(applied, await reader.VisibleAtAsync(demoId, applied, Cancel));
    }

    private static async Task<double> VisibleOwnedAreaAsync(ApiFactory api, Guid userId, League league = League.Run)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TerritoryReader>().VisibleOwnedAreaAsync(userId, league, CancellationToken.None);
    }
}
