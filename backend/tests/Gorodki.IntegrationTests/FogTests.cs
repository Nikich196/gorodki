using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Fog;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Fog;
using Gorodki.Domain.Time;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>Туман «Исследования» на настоящей базе: забег открывает клетки один раз, видит их только игрок.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class FogTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Finished_walk_opens_fog_once_and_only_its_owner_sees_it()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();
        var (stranger, _) = await api.CreatePlayerClientAsync();
        var run = await WalkAndFinishAsync(Cancel, api, client, Square(NewArea(), 0, 0, 100));

        Assert.Contains(run.Id, await ReadyAsync(api));
        var opened = await StampAsync(api, run.Id);
        var again = await StampAsync(api, run.Id);

        // Квартал 100×100 м с полосой по 25 м в обе стороны: 150² − 50² = 20 000 м² ≈ 580 клеток по ~34,5 м².
        Assert.InRange(opened!.Value, 480, 680);
        Assert.Null(again);
        var summary = await client.GetFromJsonAsync<FogSummaryResponse>("/fog/summary", Json, Cancel);
        var foot = Assert.Single(summary!.Layers); // до первого сезона — только «за всё время»
        Assert.Equal((FogLayerKind.Foot, (int?)null, opened.Value), (foot.Layer, foot.Season, foot.CellCount));
        Assert.InRange(foot.AreaSquareMeters, 16_000, 24_000);

        var mine = await client.GetFromJsonAsync<FogResponse>("/fog?layer=foot", Json, Cancel);
        var tile = Assert.Single(mine!.Tiles);
        Assert.Equal(opened.Value, FogTileCodec.Decompress(tile.Bits).Count);
        var runView = await client.GetFromJsonAsync<RunResponse>($"/runs/{run.Id}", Json, Cancel);
        Assert.Equal(opened, runView!.FogNewCells);

        var theirs = await stranger.GetFromJsonAsync<FogResponse>("/fog?layer=foot", Json, Cancel);
        Assert.Empty(theirs!.Tiles);

        var known = await client.GetFromJsonAsync<FogResponse>($"/fog?layer=foot&tiles={tile.X}:{tile.Y}@{tile.Version}", Json, Cancel);
        Assert.Empty(known!.Tiles);
        Assert.Single(known.Unchanged);
    }

    [Fact]
    public async Task Same_path_again_opens_nothing_new()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();

        var first = await WalkAndFinishAsync(Cancel, api, client, Square(area, 0, 0, 100));
        await StampAsync(api, first.Id);
        api.Time.Advance(TimeSpan.FromMinutes(30));
        var second = await WalkAndFinishAsync(Cancel, api, client, Square(area, 0, 0, 100));

        Assert.Equal(0, await StampAsync(api, second.Id));
    }

    [Fact]
    public async Task Walk_in_a_season_opens_both_the_seasonal_and_the_all_time_layer()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync(); // до сдвига часов: токен «из будущего» не прошёл бы проверку
        var area = NewArea();
        var before = await WalkAndFinishAsync(Cancel, api, client, Square(area, 0, 0, 100)); // предсезонье
        var beforeCells = await StampAsync(api, before.Id);
        api.Time.SetUtcNow(SeasonCalendar.MinskMidnight(new DateOnly(2026, 11, 20)).AddHours(9));
        var inSeason = await WalkAndFinishAsync(Cancel, api, client, Square(area, 50, 0, 100));

        var newCells = await StampAsync(api, inSeason.Id);

        var summary = (await client.GetFromJsonAsync<FogSummaryResponse>("/fog/summary", Json, Cancel))!.Layers;
        var allTime = Assert.Single(summary, l => l.Season is null);
        var season = Assert.Single(summary, l => l.Season == 0);
        Assert.Equal(beforeCells + newCells, allTime.CellCount); // «+N га» — только новое за всё время
        Assert.True(season.CellCount > newCells!.Value); // сезонный слой — весь квартал второй прогулки, включая уже открытое раньше
        Assert.True(season.CellCount < allTime.CellCount);

        var seasonal = await client.GetFromJsonAsync<FogResponse>("/fog?layer=foot&season=0", Json, Cancel);
        Assert.Equal(0, seasonal!.Season);
        Assert.Equal(season.CellCount, seasonal.Tiles.Sum(t => FogTileCodec.Decompress(t.Bits).Count));
        var past = await client.GetFromJsonAsync<FogResponse>("/fog?layer=foot&season=1", Json, Cancel);
        Assert.Empty(past!.Tiles);
    }

    [Fact]
    public async Task Far_apart_tiles_are_answered_truthfully_however_many_lie_between()
    {
        // Кэш телефона спрашивает до 25 тайлов с версиями. Между двумя далёкими — больше тайлов, чем отдаётся без списка:
        // сервер всё равно отвечает о каждом спрошенном, а не выдаёт настоящий тайл за пустой.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, userId) = await api.CreatePlayerClientAsync();
        var bits = new FogTileBits();
        bits.Set(0);
        await using (var db = database.CreateContext())
        {
            db.FogTiles.AddRange(Enumerable.Range(0, FogEndpoints.MaxTilesWithoutList + 10).Select(i => new FogTileEntity
            {
                UserId = userId,
                Layer = FogLayerKind.Foot,
                Season = SeasonCalendar.AllTime,
                TileX = 9_000 + i,
                TileY = 5_400,
                Bits = FogTileCodec.Compress(bits),
                CellCount = 1,
                Version = 1,
                UpdatedAt = api.Time.GetUtcNow(),
            }));
            await db.SaveChangesAsync(Cancel);
        }

        var last = 9_000 + FogEndpoints.MaxTilesWithoutList + 9;
        var fog = await client.GetFromJsonAsync<FogResponse>($"/fog?layer=foot&tiles=9000:5400@0,{last}:5400@0", Json, Cancel);

        Assert.Equal([9_000, last], fog!.Tiles.Select(t => t.X).Order());
        Assert.Empty(fog.Unchanged);
    }

    [Fact]
    public async Task Listed_tiles_are_looked_up_as_exact_pairs_in_the_players_own_layer()
    {
        // Спрошены (9000, 5400) и (9001, 5401), а рядом лежат и «перекрёстные» (9000, 5401), (9001, 5400) — их в ответе нет.
        // Тайл (9002, 5402) есть у другого игрока, у самого игрока — в слое «вело» и в сезонном: для «пешком за всё время» он
        // пуст.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, userId) = await api.CreatePlayerClientAsync();
        var (_, strangerId) = await api.CreatePlayerClientAsync();
        var now = api.Time.GetUtcNow();
        await using (var db = database.CreateContext())
        {
            db.FogTiles.AddRange(
                Tile(userId, FogLayerKind.Foot, SeasonCalendar.AllTime, 9_000, 5_400, 1, now),
                Tile(userId, FogLayerKind.Foot, SeasonCalendar.AllTime, 9_001, 5_401, 2, now),
                Tile(userId, FogLayerKind.Foot, SeasonCalendar.AllTime, 9_000, 5_401, 3, now),
                Tile(userId, FogLayerKind.Foot, SeasonCalendar.AllTime, 9_001, 5_400, 4, now),
                Tile(strangerId, FogLayerKind.Foot, SeasonCalendar.AllTime, 9_002, 5_402, 5, now),
                Tile(userId, FogLayerKind.Bike, SeasonCalendar.AllTime, 9_002, 5_402, 6, now),
                Tile(userId, FogLayerKind.Foot, 0, 9_002, 5_402, 7, now));
            await db.SaveChangesAsync(Cancel);
        }

        var fog = await client.GetFromJsonAsync<FogResponse>("/fog?layer=foot&tiles=9000:5400@0,9001:5401@0,9002:5402@0", Json, Cancel);

        Assert.Equal([(9_000, 5_400, 1L), (9_001, 5_401, 2L)], fog!.Tiles.Select(t => (t.X, t.Y, t.Version)).Order());
        var empty = Assert.Single(fog.Unchanged);
        Assert.Equal((9_002, 5_402), (empty.X, empty.Y));

        static FogTileEntity Tile(Guid owner, FogLayerKind layer, int season, int x, int y, long version, DateTimeOffset now)
        {
            var bits = new FogTileBits();
            bits.Set(0);
            return new FogTileEntity
            {
                UserId = owner,
                Layer = layer,
                Season = season,
                TileX = x,
                TileY = y,
                Bits = FogTileCodec.Compress(bits),
                CellCount = 1,
                Version = version,
                UpdatedAt = now,
            };
        }
    }

    [Fact]
    public async Task Broken_query_is_refused()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();

        var response = await client.GetAsync("/fog?layer=swim", Cancel);
        var negativeSeason = await client.GetAsync("/fog?layer=foot&season=-1", Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, negativeSeason.StatusCode); // −1 — внутреннее «за всё время», не номер сезона
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
}
