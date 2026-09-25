using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gorodki.Api.Features.Fog;
using Gorodki.Api.Features.Leaderboards;
using Gorodki.Api.Features.Me;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Fog;
using Gorodki.Domain.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// «Очистить историю исследований» на настоящей базе (PLAN.md, §3.10, «Приватность»): стирается весь свой туман и только
/// свой, телефон узнаёт, что тайлы пусты, путь до очистки туман не возвращает, в рейтинге стёртое не считается.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class FogHistoryTests(DatabaseFixture database)
{
    /// <summary>
    /// Срезы рейтинга — в 2030 году, раньше суток <see cref="LeaderboardTests"/> (2031): рейтинг отдаёт последний срез,
    /// и более поздний срез этих тестов подменил бы им их собственный.
    /// </summary>
    private static int _nextDay;

    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Clearing_erases_all_own_fog_and_the_phone_gets_the_tiles_empty_at_a_newer_version()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync(); // до сдвига часов: токен «из будущего» не прошёл бы проверку
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        api.Time.SetUtcNow(SeasonCalendar.MinskMidnight(new DateOnly(2026, 11, 20)).AddHours(9)); // Сезон 0: слоёв два
        var square = Square(NewArea(), 0, 0, 100);
        var opened = await WalkAndStampAsync(api, anna, square);
        await WalkAndStampAsync(api, boris, Square(NewArea(), 0, 0, 100));
        var allTime = (await FogAsync(anna, "layer=foot")).Tiles;
        var seasonal = (await FogAsync(anna, "layer=foot&season=0")).Tiles;
        var borisBefore = (await FogAsync(boris, "layer=foot")).Tiles;
        Assert.Equal(opened, allTime.Sum(t => t.CellCount));
        Assert.Equal(opened, seasonal.Sum(t => t.CellCount));

        // Номер другого игрока в запросе ничего не меняет: стирается только свой туман.
        var cleared = await anna.DeleteAsync($"/fog?userId={borisId}", Cancel);

        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        Assert.Empty((await FogAsync(anna, "layer=foot")).Tiles);
        Assert.Empty((await FogAsync(anna, "layer=foot&season=0")).Tiles);
        Assert.Empty((await anna.GetFromJsonAsync<FogSummaryResponse>("/fog/summary", Json, Cancel))!.Layers);
        Assert.Empty((await anna.GetFromJsonAsync<AccountExportResponse>("/me/export", Json, Cancel))!.Fog);

        // Телефон спрашивает тайлы из кэша со своими версиями — они приходят пустыми и новее: кэш их заменит.
        foreach (var (query, cached) in new[] { ("layer=foot", allTime), ("layer=foot&season=0", seasonal) })
        {
            var asked = await FogAsync(anna, $"{query}&tiles={Versions(cached)}");
            Assert.Empty(asked.Unchanged);
            Assert.Equal(cached.Select(t => (t.X, t.Y)).Order(), asked.Tiles.Select(t => (t.X, t.Y)).Order());
            foreach (var empty in asked.Tiles)
            {
                Assert.Equal(0, empty.CellCount);
                Assert.Equal(0, FogTileCodec.Decompress(empty.Bits).Count);
                Assert.True(empty.Version > cached.Single(t => t.X == empty.X && t.Y == empty.Y).Version);
            }
        }

        var borisAfter = (await FogAsync(boris, "layer=foot")).Tiles;
        Assert.Equal(
            borisBefore.Select(t => (t.X, t.Y, t.Version, t.CellCount)),
            borisAfter.Select(t => (t.X, t.Y, t.Version, t.CellCount)));

        // Новый забег по тем же местам открывает их заново — с версиями новее тех, с которыми телефон получил тайлы пустыми.
        var emptied = (await FogAsync(anna, $"layer=foot&tiles={Versions(allTime)}")).Tiles;
        api.Time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(opened, await WalkAndStampAsync(api, anna, square));
        var reopened = (await FogAsync(anna, $"layer=foot&tiles={Versions(emptied)}")).Tiles;
        Assert.Equal(opened, reopened.Sum(t => t.CellCount));
        Assert.Equal(emptied.Count, reopened.Count);
        foreach (var tile in reopened)
        {
            var emptyVersion = emptied.Single(t => t.X == tile.X && t.Y == tile.Y).Version;
            Assert.True(tile.Version > emptyVersion, $"версия {tile.Version} не новее пустой {emptyVersion}");
        }
    }

    [Fact]
    public async Task Walk_from_before_the_clear_never_opens_fog_even_if_it_is_delivered_later()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var finished = await WalkAndFinishAsync(Cancel, api, anna, Square(area, 0, 0, 100)); // туман ещё не открыт
        api.Time.Advance(TimeSpan.FromMinutes(30));
        var (going, points) = await WalkAsync(Cancel, api, anna, Square(area, 200, 0, 100)); // забег ещё идёт
        Assert.Contains(finished.Id, await ReadyAsync(api));

        Assert.Equal(HttpStatusCode.NoContent, (await anna.DeleteAsync("/fog", Cancel)).StatusCode);
        var finish = await anna.PostAsJsonAsync(
            $"/runs/{going.Id}/finish",
            new FinishRunRequest(points[^1].T, points.Count - 1, api.Time.GetUtcNow().ToUnixTimeMilliseconds()),
            Json,
            Cancel);

        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
        var ready = await ReadyAsync(api);
        Assert.DoesNotContain(finished.Id, ready);
        Assert.DoesNotContain(going.Id, ready);
        Assert.Null(await StampAsync(api, finished.Id));
        Assert.Null(await StampAsync(api, going.Id));
        Assert.Empty((await FogAsync(anna, "layer=foot")).Tiles);
        Assert.Equal(0, (await anna.GetFromJsonAsync<RunResponse>($"/runs/{finished.Id}", Json, Cancel))!.FogNewCells);
    }

    [Fact]
    public async Task Cleared_player_leaves_the_leaderboard_at_once_and_the_places_below_move_up()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        api.Time.SetUtcNow(SeasonCalendar.MinskMidnight(new DateOnly(2030, 1, 1).AddDays(Interlocked.Increment(ref _nextDay) * 10)).AddHours(12));
        await WalkAndStampAsync(api, anna, Square(NewArea(), 0, 0, 100));
        await WalkAndStampAsync(api, anna, Square(NewArea(), 0, 0, 100)); // Анна открыла больше Бориса
        await WalkAndStampAsync(api, boris, Square(NewArea(), 0, 0, 100));
        Assert.True(await SnapshotAsync(api) > 0);
        var day = GameClock.GameDayOf(api.Time.GetUtcNow());
        var annaRank = await RankAsync(day, annaId);
        var borisRank = await RankAsync(day, borisId);
        Assert.True(annaRank < borisRank);

        Assert.Equal(HttpStatusCode.NoContent, (await anna.DeleteAsync("/fog", Cancel)).StatusCode);

        await using (var db = database.CreateContext())
        {
            Assert.False(await db.LeaderboardSnapshots.AnyAsync(s => s.UserId == annaId, Cancel));
        }

        Assert.Equal(borisRank - 1, await RankAsync(day, borisId)); // место Анны выше Бориса освободилось
        Assert.Empty((await anna.GetFromJsonAsync<AccountExportResponse>("/me/export", Json, Cancel))!.Rankings);

        // Следующий срез — по туману после очистки: Анны в нём нет, пока она снова не откроет что-нибудь.
        api.Time.Advance(TimeSpan.FromDays(1));
        Assert.True(await SnapshotAsync(api) > 0);
        Assert.Null(await RankAsync(day.AddDays(1), annaId));
        Assert.NotNull(await RankAsync(day.AddDays(1), borisId));
    }

    [Fact]
    public async Task Places_after_the_clear_keep_ties_and_boards_without_the_player_are_untouched()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (xenia, xeniaId) = await api.CreatePlayerClientAsync();
        var others = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            others.Add((await api.CreatePlayerClientAsync()).UserId);
        }

        var day = new DateOnly(2030, 1, 1).AddDays(Interlocked.Increment(ref _nextDay) * 10);
        await using (var db = database.CreateContext())
        {
            // «Пешком»: Ксения на втором месте, за ней — двое с одинаковой площадью. «Вело» без Ксении — с дырой «1, 3»
            // (так оставляет удалённый аккаунт): очистка её не касается.
            var foot = new[] { (others[0], 300.0, 1), (xeniaId, 250.0, 2), (others[1], 200.0, 3), (others[2], 200.0, 3), (others[3], 100.0, 5) };
            var bike = new[] { (others[0], 300.0, 1), (others[1], 200.0, 3) };
            db.LeaderboardSnapshots.AddRange(foot.Select(r => Row(day, LeaderboardLayer.Foot, r)));
            db.LeaderboardSnapshots.AddRange(bike.Select(r => Row(day, LeaderboardLayer.Bike, r)));
            await db.SaveChangesAsync(Cancel);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await xenia.DeleteAsync("/fog", Cancel)).StatusCode);

        await using (var db = database.CreateContext())
        {
            var places = (await db.LeaderboardSnapshots
                    .Where(s => s.Day == day)
                    .Select(s => new { s.Layer, s.UserId, s.Rank })
                    .ToListAsync(Cancel))
                .Select(p => (p.Layer, p.UserId, p.Rank))
                .OrderBy(p => p.Layer).ThenBy(p => p.Rank).ThenBy(p => others.IndexOf(p.UserId));
            // Одинаковая площадь — одинаковое место, следующее — через одно (1, 2, 2, 4), как у среза.
            (LeaderboardLayer, Guid, int)[] expected =
            [
                (LeaderboardLayer.Foot, others[0], 1), (LeaderboardLayer.Foot, others[1], 2), (LeaderboardLayer.Foot, others[2], 2),
                (LeaderboardLayer.Foot, others[3], 4), (LeaderboardLayer.Bike, others[0], 1), (LeaderboardLayer.Bike, others[1], 3),
            ];
            Assert.Equal(expected, places);
        }

        static LeaderboardSnapshotEntity Row(DateOnly day, LeaderboardLayer layer, (Guid User, double Value, int Rank) row) => new()
        {
            Day = day,
            Board = LeaderboardBoard.Exploration,
            Layer = layer,
            Season = SeasonCalendar.AllTime,
            UserId = row.User,
            Value = row.Value,
            Rank = row.Rank,
        };
    }

    [Fact]
    public async Task Clear_waits_for_the_daily_snapshot_only_when_there_is_fog_and_answers_busy_after_the_lock_timeout()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        api.Time.SetUtcNow(SeasonCalendar.MinskMidnight(new DateOnly(2030, 1, 1).AddDays(Interlocked.Increment(ref _nextDay) * 10)).AddHours(12));
        await WalkAndStampAsync(api, anna, Square(NewArea(), 0, 0, 100));
        var day = GameClock.GameDayOf(api.Time.GetUtcNow());

        await using (var snapshot = database.CreateContext())
        {
            // Долгий срез рейтингов за сегодня держит свою блокировку, как LeaderboardSnapshots.TakeIfDueAsync.
            await using var transaction = await snapshot.Database.BeginTransactionAsync(Cancel);
            await snapshot.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(4, {day.DayNumber})", Cancel);

            // У Бориса тумана нет — его очистка срез не ждёт: повторные очистки не встают в общую очередь.
            Assert.Equal(HttpStatusCode.NoContent, (await boris.DeleteAsync("/fog", Cancel)).StatusCode);

            // Анне есть что стирать: очистка ждёт срез не дольше lock_timeout и отвечает «занято», ничего не стерев.
            var busy = await anna.DeleteAsync("/fog", Cancel);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
            Assert.Equal("fog_clear_busy", (await busy.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("code").GetString());
            Assert.NotEmpty((await FogAsync(anna, "layer=foot")).Tiles);
        }

        // Срез закончился — повтор проходит.
        Assert.Equal(HttpStatusCode.NoContent, (await anna.DeleteAsync("/fog", Cancel)).StatusCode);
        Assert.Empty((await FogAsync(anna, "layer=foot")).Tiles);
    }

    // MARK: — вспомогательное

    private async Task<int> WalkAndStampAsync(ApiFactory api, HttpClient client, (double X, double Y)[] square)
    {
        var run = await WalkAndFinishAsync(Cancel, api, client, square);
        var opened = await StampAsync(api, run.Id);
        Assert.True(opened > 0);
        api.Time.Advance(TimeSpan.FromMinutes(30)); // следующий забег — после этого
        return opened!.Value;
    }

    private async Task<FogResponse> FogAsync(HttpClient client, string query) =>
        (await client.GetFromJsonAsync<FogResponse>($"/fog?{query}", Json, Cancel))!;

    /// <summary>Тайлы с версиями, как их спрашивает кэш телефона: <c>x:y@версия,…</c>.</summary>
    private static string Versions(IEnumerable<FogTileView> tiles) => string.Join(",", tiles.Select(t => $"{t.X}:{t.Y}@{t.Version}"));

    /// <summary>Место игрока в срезе «Пешком» за всё время; null — его в срезе нет.</summary>
    private async Task<int?> RankAsync(DateOnly day, Guid userId)
    {
        await using var db = database.CreateContext();
        return await db.LeaderboardSnapshots
            .Where(s => s.Day == day && s.Board == LeaderboardBoard.Exploration && s.Layer == LeaderboardLayer.Foot
                && s.Season == SeasonCalendar.AllTime && s.UserId == userId)
            .Select(s => (int?)s.Rank)
            .SingleOrDefaultAsync(Cancel);
    }

    private static async Task<List<Guid>> ReadyAsync(ApiFactory api)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<FogProcessor>().RunsReadyAsync(1_000, CancellationToken.None);
    }

    private static async Task<int?> StampAsync(ApiFactory api, Guid runId)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<FogProcessor>().StampRunAsync(runId, CancellationToken.None);
    }

    private static async Task<int> SnapshotAsync(ApiFactory api)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LeaderboardSnapshots>().TakeIfDueAsync(CancellationToken.None);
    }
}
