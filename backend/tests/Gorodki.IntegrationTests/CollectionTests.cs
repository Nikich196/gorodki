using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Collection;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Тайники и «Коллекция» — <c>GET /collection</c> и находки в фоновом обработчике (PLAN.md, §3.12; #145). Координат в ответе
/// нет никогда; чужая находка влияет на «№ N нашедших» только с границы публичности. Правило «прошёл в 30 м» по точкам —
/// <c>CacheRulesTests</c> без Docker.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CollectionTests(DatabaseFixture database)
{
    private static int _nextNumber = 1_000;

    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Collection_has_no_coordinates_and_unfound_caches_are_silhouettes()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync(newcomer: true);

        var response = await anna.GetAsync("/collection", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Problems.HasNoCoordinates(await response.Content.ReadAsStringAsync(Cancel)); // тест обязан это проверять (раздел 4)
        var collection = (await response.Content.ReadFromJsonAsync<CollectionResponse>(Json, Cancel))!;
        Assert.All(collection.Caches, c => Assert.True( // новичок ещё ничего не нашёл
            c.Name is null && c.FoundAtMs is null && c.FinderNumber is null && !c.GoldFrame, $"Найден: {c.Id}"));
        Assert.All(collection.Caches, c => Assert.False(string.IsNullOrWhiteSpace(c.Hint)));
        Assert.True(collection.Caches.Count >= CacheSeed.All.Count); // начальный набор на месте
    }

    [Fact]
    public async Task Initial_set_is_a_proposal_of_twenty_with_the_planned_rarities()
    {
        database.RequireDatabase();
        await using var db = database.CreateContext();

        var seeded = await db.Caches.AsNoTracking().Where(c => c.Proposal).ToListAsync(Cancel);

        Assert.Equal(20, seeded.Count);
        Assert.Equal(
            [(BadgeRarity.Common, 10), (BadgeRarity.Rare, 6), (BadgeRarity.Epic, 3), (BadgeRarity.Legendary, 1)],
            seeded.GroupBy(c => c.Rarity).OrderBy(g => g.Key).Select(g => (g.Key, g.Count())));
        Assert.All(seeded, c => Assert.True(c.SetVersion == 1 && c.OsmRef is not null && c.Hint.Length <= 200, c.Name));
    }

    [Fact]
    public async Task Walking_past_a_cache_finds_it_once_and_others_count_it_only_after_the_public_boundary()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync(newcomer: true);
        var (boris, _) = await api.CreatePlayerClientAsync(newcomer: true);
        var area = NewArea();
        var near = await AddCacheAsync(area, 300, 10); // в 10 м от пути
        var far = await AddCacheAsync(area, 300, 60); // в 60 м — мимо
        (double X, double Y)[] path = [(area.X, area.Y), (area.X + 600, area.Y)];

        var annaRun = await WalkAndFinishAsync(Cancel, api, anna, path);
        Assert.Contains(annaRun.Id, await ReadyAsync(api)); // завершённый забег сразу готов, как у тумана
        Assert.True(await FindAsync(api, annaRun.Id) >= 1);
        Assert.Null(await FindAsync(api, annaRun.Id)); // повтор забега не проверяет второй раз

        // Через минуту Борис проходит там же: находка Анны ещё скрыта границей публичности — Борис пока «первый».
        api.Time.Advance(TimeSpan.FromMinutes(1));
        var borisRun = await WalkAndFinishAsync(Cancel, api, boris, path);
        await FindAsync(api, borisRun.Id);
        var borisBefore = await BadgeAsync(boris, near);
        var annaBadge = await BadgeAsync(anna, near);

        Assert.Equal((1, true), (borisBefore.FinderNumber, borisBefore.GoldFrame));
        Assert.Equal((1, true), (annaBadge.FinderNumber, annaBadge.GoldFrame));
        Assert.NotNull(annaBadge.Name);
        await using (var db = database.CreateContext())
        {
            var found = await db.CacheFinds.SingleAsync(f => f.CacheId == near && f.UserId == annaId, Cancel);
            Assert.Equal(found.FoundAt.ToUnixTimeMilliseconds(), annaBadge.FoundAtMs);
            Assert.Equal(4, found.Id.Version); // случайный номер: в UUIDv7 зашито время
            Assert.False(await db.CacheFinds.AnyAsync(f => f.CacheId == far, Cancel));
        }

        Assert.Null((await BadgeAsync(anna, far)).Name); // мимо — силуэт

        // Когда находка Анны стала видна другим, Борис — второй, рамка у Анны. Ни её номера, ни ника, ни координат.
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        var borisAfter = await BadgeAsync(boris, near);
        var body = await (await boris.GetAsync("/collection", Cancel)).Content.ReadAsStringAsync(Cancel);

        Assert.Equal((2, false), (borisAfter.FinderNumber, borisAfter.GoldFrame));
        Assert.Equal((1, true), ((await BadgeAsync(anna, near)).FinderNumber, (await BadgeAsync(anna, near)).GoldFrame));
        Problems.HasNoCoordinates(body);
        Assert.DoesNotContain(annaId.ToString(), body, StringComparison.OrdinalIgnoreCase);

        // Ещё один забег Анны мимо того же тайника второй находки не даёт.
        var again = await WalkAndFinishAsync(Cancel, api, anna, path);
        await FindAsync(api, again.Id);
        await using (var db = database.CreateContext())
        {
            Assert.Equal(1, await db.CacheFinds.CountAsync(f => f.CacheId == near && f.UserId == annaId, Cancel));
        }
    }

    /// <summary>Тайник своего теста: (x; y) метров от места <paramref name="area"/> (от <see cref="WalkOrigin"/>).</summary>
    private async Task<Guid> AddCacheAsync((double X, double Y) area, double x, double y)
    {
        var (latitude, longitude) = Utm34.Inverse(WalkOrigin.X + area.X + x, WalkOrigin.Y + area.Y + y);
        var id = Guid.NewGuid();
        await using var db = database.CreateContext();
        db.Caches.Add(new CacheEntity
        {
            Id = id,
            Number = (short)Interlocked.Increment(ref _nextNumber),
            Name = $"Тестовый {id.ToString("N")[..6]}",
            Rarity = BadgeRarity.Epic,
            Hint = "Тестовая подсказка.",
            Latitude = latitude,
            Longitude = longitude,
            SetVersion = 1,
        });
        await db.SaveChangesAsync(Cancel);
        return id;
    }

    private async Task<CacheBadgeResponse> BadgeAsync(HttpClient client, Guid cacheId) =>
        (await client.GetFromJsonAsync<CollectionResponse>("/collection", Json, Cancel))!.Caches.Single(c => c.Id == cacheId);

    private static async Task<int?> FindAsync(ApiFactory api, Guid runId)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CacheProcessor>().ProcessRunAsync(runId, CancellationToken.None);
    }

    private static async Task<List<Guid>> ReadyAsync(ApiFactory api)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CacheProcessor>().RunsReadyAsync(10_000, CancellationToken.None);
    }
}
