using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Collection;
using static Gorodki.IntegrationTests.RunRequests;

namespace Gorodki.IntegrationTests;

/// <summary>
/// «Коллекция» — <c>GET /collection</c> (PLAN.md, §3.12). Задача #TBD-E15 для Егора: тесты со <c>Skip</c> снимаются вместе с
/// реализацией. Тайники в базе и находки даёт C12 (задача Claude); когда они будут, сюда — тест находки («№ N нашедших»,
/// золотая рамка первому) и тест «подсказка проясняется по своему туману».
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CollectionTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(Skip = "ЗАДАЧА #TBD-E15")]
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
    }
}
