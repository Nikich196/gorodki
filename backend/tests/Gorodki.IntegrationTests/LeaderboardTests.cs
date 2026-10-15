using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Fog;
using Gorodki.Api.Features.Leaderboards;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Рейтинг «Кто открыл больше» на настоящей базе (PLAN.md, §3.10): срез каждые 5 минут (plan-deviations, №15), свежий туман —
/// с границы публичности, только числа, ник — только с согласием. Часы тестов — в 2031 году (Сезон 2 на календаре открыт до
/// показа): там рейтинг сезона — только этих тестов.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class LeaderboardTests(DatabaseFixture database)
{
    private static int _nextDay;

    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Leaderboard_shows_new_hectares_from_the_public_boundary_with_names_only_by_consent()
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

        // Новый забег в те же сутки. Его гектары — не в следующем проходе, а с границы публичности (20 минут шагами по 5), как
        // захват на карте: до неё у Анны места прошлого среза. Живой рейтинг выдавал бы «она только что бегала».
        var stampedAt = await WalkAndStampAsync(api, anna);
        var publicAt = TerritoryReader.PublicAt(stampedAt, TerritoryReader.PublicDelay);
        Assert.True(await SnapshotAsync(api) > 0);
        Assert.Equal(annaMine.Hectares, (await BoardAsync(anna, "foot", season: 2)).Mine!.Hectares);
        Assert.Equal(annaMine.Hectares, (await BoardAsync(boris, "foot", season: 2)).Entries.Single(e => e.Name == annaName).Hectares);

        api.Time.Advance(publicAt - TimeSpan.FromMilliseconds(1) - api.Time.GetUtcNow());
        await SnapshotAsync(api);
        Assert.Equal(annaMine.Hectares, (await BoardAsync(anna, "foot", season: 2)).Mine!.Hectares);

        api.Time.Advance(TimeSpan.FromMilliseconds(1)); // граница — и проход в эту же минуту (расписание — каждые 5 минут)
        await SnapshotAsync(api);
        var later = await BoardAsync(anna, "foot", season: 2);
        Assert.True(later.Mine!.Hectares > annaMine.Hectares);
        Assert.Equal(Today(api), later.Day); // срез — сегодняшний: строки суток переснимаются
    }

    [Fact]
    public async Task First_hectares_enter_the_board_from_the_public_boundary_but_a_demo_accounts_at_once()
    {
        // Новичка прошлого среза нет — до границы публичности его в рейтинге нет вовсе. Демо-аккаунт на показе виден сразу,
        // как его захваты на карте (§11).
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (vera, _) = await api.CreatePlayerClientAsync();
        var (demo, _) = await api.CreatePlayerClientAsync(UserRole.Demo);
        api.Time.SetUtcNow(SeasonCalendar.MinskMidnight(new DateOnly(2031, 1, 1).AddDays(Interlocked.Increment(ref _nextDay) * 10)).AddHours(12));

        var stampedAt = await WalkAndStampAsync(api, vera);
        await WalkAndStampAsync(api, demo);
        await SnapshotAsync(api);
        Assert.Null((await BoardAsync(vera, "foot", season: 2)).Mine);
        Assert.NotNull((await BoardAsync(demo, "foot", season: 2)).Mine);

        api.Time.Advance(TerritoryReader.PublicAt(stampedAt, TerritoryReader.PublicDelay) - api.Time.GetUtcNow());
        await SnapshotAsync(api);
        Assert.NotNull((await BoardAsync(vera, "foot", season: 2)).Mine);
    }

    [Fact]
    public async Task Two_servers_refreshing_at_once_leave_one_row_per_board()
    {
        // Во время деплоя задача может пойти на двух экземплярах сразу: проход — под блокировкой суток (4), строки — заменой.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        api.Time.SetUtcNow(SeasonCalendar.MinskMidnight(new DateOnly(2031, 1, 1).AddDays(Interlocked.Increment(ref _nextDay) * 10)).AddHours(12));
        await WalkAsync(api, anna, 1);

        var both = await Task.WhenAll(SnapshotAsync(api), SnapshotAsync(api));

        Assert.All(both, rows => Assert.True(rows > 0));
        await using var db = database.CreateContext();
        var day = GameClock.GameDayOf(api.Time.GetUtcNow());
        var mine = await db.LeaderboardSnapshots.AsNoTracking()
            .Where(s => s.Board == LeaderboardBoard.Exploration && s.UserId == annaId && s.Day == day)
            .Select(s => new { s.Layer, s.Season })
            .ToListAsync(Cancel);
        // «Пешком» и «Всего» — за всё время и за Сезон 2: по строке на доску, без дублей.
        Assert.Equal(4, mine.Count);
        Assert.Equal(mine.Count, mine.Distinct().Count());
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

    /// <summary>Забег без паузы после него: туман открыт «только что». Возвращает момент открытия.</summary>
    private async Task<DateTimeOffset> WalkAndStampAsync(ApiFactory api, HttpClient client)
    {
        var run = await WalkAndFinishAsync(Cancel, api, client, Square(NewArea(), 0, 0, 100));
        await using var scope = api.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<FogProcessor>().StampRunAsync(run.Id, Cancel) > 0);
        return api.Time.GetUtcNow();
    }

    private static string Today(ApiFactory api) =>
        GameClock.GameDayOf(api.Time.GetUtcNow()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<int> SnapshotAsync(ApiFactory api)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LeaderboardSnapshots>().RefreshAsync(CancellationToken.None);
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
