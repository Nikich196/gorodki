using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Fog;
using Gorodki.Api.Features.Leaderboards;
using Gorodki.Domain.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Рейтинг «Кто открыл больше» на настоящей базе (PLAN.md, §3.10, §3.5): ежедневный срез, только числа, ник — только
/// с согласием. Часы тестов — в 2031 году (Сезон 2 на календаре открыт до показа): там рейтинг сезона — только этих тестов.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class LeaderboardTests(DatabaseFixture database)
{
    private static int _nextDay;

    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Leaderboard_is_a_daily_snapshot_with_names_only_by_consent()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync(); // до сдвига часов: токен «из будущего» не прошёл бы проверку
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        await using (var db = database.CreateContext())
        {
            await db.Users.Where(u => u.Id == annaId).ExecuteUpdateAsync(set => set.SetProperty(u => u.PublicProfile, true), Cancel);
        }

        api.Time.SetUtcNow(SeasonCalendar.MinskMidnight(new DateOnly(2031, 1, 1).AddDays(Interlocked.Increment(ref _nextDay) * 10)).AddHours(12));
        await WalkAsync(api, anna, 1);
        await WalkAsync(api, boris, 2); // Борис открыл больше
        Assert.True(await SnapshotAsync(api) > 0);

        var seenByAnna = await BoardAsync(anna, "foot", season: 2);
        var seenByBoris = await BoardAsync(boris, "foot", season: 2);
        var annaName = await NameAsync(annaId);

        var annaMine = Assert.IsType<LeaderboardEntry>(seenByAnna.Mine);
        var borisMine = Assert.IsType<LeaderboardEntry>(seenByBoris.Mine);
        Assert.True(borisMine.Rank < annaMine.Rank);
        Assert.True(borisMine.Hectares > annaMine.Hectares);
        Assert.Equal((annaName, true), (annaMine.Name, annaMine.Me));
        // Анна согласилась показывать ник — её видят по нику; Борис — нет: для Анны он «Игрок #…».
        Assert.Contains(seenByBoris.Entries, e => e.Name == annaName && !e.Me);
        Assert.Contains(seenByAnna.Entries, e => e.Name == LeaderboardEndpoints.Pseudonym(borisId) && !e.Me);
        Assert.DoesNotContain(seenByAnna.Entries, e => e.Name == borisMine.Name && !e.Me);
        Assert.Equal(annaMine.Hectares, (await BoardAsync(anna, "total", season: 2)).Mine!.Hectares); // «Всего» = пешком + вело

        // Новый забег в те же сутки — рейтинг прежний: срез раз в сутки, живой рейтинг выдавал бы «только что бегала».
        await WalkAsync(api, anna, 2);
        Assert.Equal(0, await SnapshotAsync(api));
        Assert.Equal(annaMine.Hectares, (await BoardAsync(anna, "foot", season: 2)).Mine!.Hectares);

        api.Time.Advance(TimeSpan.FromDays(1));
        Assert.True(await SnapshotAsync(api) > 0);
        Assert.True((await BoardAsync(anna, "foot", season: 2)).Mine!.Hectares > annaMine.Hectares);
    }

    [Fact]
    public async Task Broken_query_is_refused()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();

        foreach (var query in new[] { "layer=swim", "layer=1", "season=-1" })
        {
            var response = await client.GetAsync($"/leaderboards/exploration?{query}", Cancel);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    /// <summary>Квадраты 100 × 100 м в новом месте — каждый открывает около 2 га тумана «Пешком».</summary>
    private async Task WalkAsync(ApiFactory api, HttpClient client, int squares)
    {
        for (var i = 0; i < squares; i++)
        {
            var run = await WalkAndFinishAsync(Cancel, api, client, Square(NewArea(), 0, 0, 100));
            await using var scope = api.Services.CreateAsyncScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<FogProcessor>().StampRunAsync(run.Id, Cancel) > 0);
            api.Time.Advance(TimeSpan.FromMinutes(30)); // следующий забег — после этого
        }
    }

    private static async Task<int> SnapshotAsync(ApiFactory api)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LeaderboardSnapshots>().TakeIfDueAsync(CancellationToken.None);
    }

    private async Task<ExplorationLeaderboardResponse> BoardAsync(HttpClient client, string layer, int season) =>
        (await client.GetFromJsonAsync<ExplorationLeaderboardResponse>(
            $"/leaderboards/exploration?layer={layer}&season={season}", Json, Cancel))!;

    private async Task<string> NameAsync(Guid userId)
    {
        await using var db = database.CreateContext();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).SingleAsync(Cancel);
    }
}
