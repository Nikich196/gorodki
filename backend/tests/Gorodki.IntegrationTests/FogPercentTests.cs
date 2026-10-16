using System.Net.Http.Json;
using Gorodki.Api.Features.Fog;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Config;
using Gorodki.Domain.Fog;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Osm;
using Gorodki.Domain.Time;
using Gorodki.OsmPipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// «% Бреста» и районов в <c>GET /fog/summary</c> (PLAN.md, §3.10; osm-pipeline.md, «Как считается % Бреста»), задача #138
/// (сделано Claude 07.10). Тест «набора нет» — ещё и контракт: старый телефон не ломается, пока набора нет.
/// </summary>
/// <remarks>
/// Набор в действующем конфиге меняет захват для всех тестов, поэтому здесь конфиг с набором начинает действовать в 2200 году,
/// и часы этого теста переводятся туда: у остальных тестов действующий конфиг прежний (как в <c>OsmSetTests</c>).
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class FogPercentTests(DatabaseFixture database)
{
    private static int _nextSet = 1_900;

    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_an_osm_set_the_percents_are_empty_and_the_areas_stay()
    {
        // Это проверяет контракт (старый телефон не ломается, пока набора нет), а не реализацию, — поэтому без Skip.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var run = await WalkAndFinishAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100));
        await StampAsync(api, run.Id);

        var summary = (await anna.GetFromJsonAsync<FogSummaryResponse>("/fog/summary", Json, Cancel))!;

        Assert.Null(summary.OsmSetVersion);
        Assert.NotEmpty(summary.Layers);
        Assert.All(summary.Layers, l => Assert.Null(l.BrestPercent));
        Assert.All(summary.Layers, l => Assert.Null(l.Districts));
        Assert.All(summary.Layers, l => Assert.True(l.AreaSquareMeters > 0));
    }

    [Fact]
    public async Task Percent_is_the_share_of_the_reachable_cells_and_districts_follow_the_same_rule()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync(); // клиент — до сдвига часов: токен «из будущего» не пройдёт
        var (boris, _) = await api.CreatePlayerClientAsync();
        var run = await WalkAndFinishAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100));
        await StampAsync(api, run.Id);
        await StampAsync(api, (await WalkAndFinishAsync(Cancel, api, boris, Square(NewArea(), 0, 0, 100))).Id);

        // «Достижимое» набора: открытые Анной клетки её первого тайла и столько же закрытых клеток в далёком тайле — это 50 %
        // города. Район — только её клетки: 100 %.
        FogTileKey tile;
        FogTileBits opened;
        await using (var db = database.CreateContext())
        {
            var first = await db.FogTiles.AsNoTracking()
                .Where(f => f.UserId == annaId && f.Layer == FogLayerKind.Foot && f.Season == SeasonCalendar.AllTime)
                .OrderBy(f => f.TileX).ThenBy(f => f.TileY)
                .FirstAsync(Cancel);
            (tile, opened) = (new FogTileKey(first.TileX, first.TileY), FogTileCodec.Decompress(first.Bits));
        }

        var far = new FogTileKey(tile.X + 40, tile.Y + 40);
        var farBits = new FogTileBits();
        for (var cell = 0; farBits.Count < opened.Count; cell++)
        {
            farBits.Set(cell);
        }

        var version = Interlocked.Increment(ref _nextSet);
        var outline = GeoOps.Factory.CreateMultiPolygon([new TileKey(684, 5775).ToPolygon()]);
        var city = new SortedDictionary<FogTileKey, FogTileBits> { [tile] = opened, [far] = farBits };
        var district = new DistrictData(
            "leninsky", DistrictKind.District, "Ленинский район", null, false, outline, 1_000_000,
            new SortedDictionary<FogTileKey, FogTileBits> { [tile] = opened });
        var cityDistrict = new DistrictData("city", DistrictKind.City, "Брест", null, false, outline, 2_000_000, city);
        await ImportSetAsync(database, version, city, [cityDistrict, district], Cancel);
        await UseFromYear2200Async(api, version);

        var summary = (await anna.GetFromJsonAsync<FogSummaryResponse>("/fog/summary", Json, Cancel))!;

        Assert.Equal(version, summary.OsmSetVersion);
        var allTime = summary.Layers.Single(l => l.Layer == FogLayerKind.Foot && l.Season is null);
        Assert.Equal((double?)50, allTime.BrestPercent);
        var leninsky = Assert.Single(allTime.Districts!);
        Assert.Equal(("leninsky", DistrictKind.District, 100.0), (leninsky.Key, leninsky.Kind, leninsky.Percent));
        Assert.All(summary.Layers, l => Assert.NotNull(l.BrestPercent)); // и у сезонного слоя — по тому же правилу

        // Процент — только по своим тайлам: Борис ходил в другом месте, «достижимое» Анны ему не засчитано.
        var theirs = (await boris.GetFromJsonAsync<FogSummaryResponse>("/fog/summary", Json, Cancel))!;
        Assert.Equal(version, theirs.OsmSetVersion);
        Assert.Equal((double?)0, theirs.Layers.Single(l => l.Layer == FogLayerKind.Foot && l.Season is null).BrestPercent);
    }

    [Fact]
    public async Task A_run_remembers_its_new_area_and_the_reachable_cells_it_opened_first_for_both_layers()
    {
        // Карточка недели (§3.15) складывает эти числа по забегам недели: площадь — «+N га», «новые достижимые» клетки —
        // прирост пожизненного «% Бреста» «Всего» (оба слоя, osm-pipeline.md, вопрос 6.10).
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var first = await WalkAndFinishAsync(Cancel, api, anna, Square(area, 0, 0, 100));
        api.Time.Advance(TimeSpan.FromMinutes(30));
        var second = await WalkAndFinishAsync(Cancel, api, anna, Square(area, 40, 40, 100));
        api.Time.Advance(TimeSpan.FromMinutes(30));
        var third = await WalkAndFinishAsync(Cancel, api, anna, Square(area, -40, 80, 100));

        // Набора нет: площадь записана (у каждой клетки — площадь на широте её тайла), «достижимых» нет.
        await StampAsync(api, first.Id);
        var opened = await FootTilesAsync(annaId);
        var afterFirst = await RunAsync(first.Id);
        var expectedArea = opened.Sum(t => t.Value.Count * FogTileCodec.CellAreaSquareMeters(t.Key));
        Assert.InRange(afterFirst.FogNewSquareMeters!.Value, expectedArea - 0.11, expectedArea + 0.11);
        Assert.Equal((null, (int?)null), (afterFirst.FogNewReachableCells, afterFirst.FogOsmSet));

        // «Достижимое» — тайлы первого забега и их соседи целиком: всё, что откроют следующие забеги рядом, достижимо.
        var city = new SortedDictionary<FogTileKey, FogTileBits>();
        foreach (var key in opened.Keys)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    city.TryAdd(new FogTileKey(key.X + dx, key.Y + dy), Full());
                }
            }
        }

        var version = Interlocked.Increment(ref _nextSet);
        var outline = GeoOps.Factory.CreateMultiPolygon([new TileKey(684, 5775).ToPolygon()]);
        await ImportSetAsync(database, version, city, [new DistrictData("city", DistrictKind.City, "Брест", null, false, outline, 2_000_000, city)], Cancel);
        await UseFromYear2200Async(api, version);

        // Второй забег: каждая новая клетка достижима и нова для «Всего».
        await StampAsync(api, second.Id);
        var afterSecond = await RunAsync(second.Id);
        var grown = (await FootTilesAsync(annaId)).Where(t => city.ContainsKey(t.Key)).Sum(t => t.Value.Count)
            - opened.Where(t => city.ContainsKey(t.Key)).Sum(t => t.Value.Count);
        Assert.True(grown > 0);
        Assert.Equal((afterSecond.FogNewCells, (int?)version), (afterSecond.FogNewReachableCells, afterSecond.FogOsmSet));
        Assert.Equal(grown, afterSecond.FogNewReachableCells);

        // Всё «достижимое» уже открыто на велосипеде: третий забег открывает новое пешком, но для «Всего» — ничего.
        await using (var db = database.CreateContext())
        {
            db.FogTiles.AddRange(city.Keys.Select(key => new FogTileEntity
            {
                UserId = annaId,
                Layer = FogLayerKind.Bike,
                Season = SeasonCalendar.AllTime,
                TileX = key.X,
                TileY = key.Y,
                Bits = FogTileCodec.Compress(Full()),
                CellCount = FogTileBits.WordCount * 64,
                Version = 1,
                UpdatedAt = api.Time.GetUtcNow(),
            }));
            await db.SaveChangesAsync(Cancel);
        }

        await StampAsync(api, third.Id);
        var afterThird = await RunAsync(third.Id);
        Assert.True(afterThird.FogNewCells > 0);
        Assert.Equal((0, (int?)version), (afterThird.FogNewReachableCells, afterThird.FogOsmSet));
    }

    // MARK: — вспомогательное

    private static FogTileBits Full()
    {
        var bits = new FogTileBits();
        for (var cell = 0; cell < FogTileBits.WordCount * 64; cell++)
        {
            bits.Set(cell);
        }

        return bits;
    }

    private async Task<RunEntity> RunAsync(Guid runId)
    {
        await using var db = database.CreateContext();
        return await db.Runs.AsNoTracking().SingleAsync(r => r.Id == runId, Cancel);
    }

    /// <summary>Пешеходный туман игрока за всё время по тайлам.</summary>
    private async Task<Dictionary<FogTileKey, FogTileBits>> FootTilesAsync(Guid userId)
    {
        await using var db = database.CreateContext();
        return (await db.FogTiles.AsNoTracking()
                .Where(f => f.UserId == userId && f.Layer == FogLayerKind.Foot && f.Season == SeasonCalendar.AllTime)
                .ToListAsync(Cancel))
            .ToDictionary(f => new FogTileKey(f.TileX, f.TileY), f => FogTileCodec.Decompress(f.Bits));
    }

    private static async Task StampAsync(ApiFactory api, Guid runId)
    {
        await using var scope = api.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<FogProcessor>().StampRunAsync(runId, CancellationToken.None) > 0);
    }

    /// <summary>Набор OSM в базу теста: только «достижимое» и районы (масок и землепользования нет, играть можно везде).</summary>
    internal static async Task ImportSetAsync(
        DatabaseFixture database,
        int version,
        SortedDictionary<FogTileKey, FogTileBits> reachable,
        IReadOnlyList<DistrictData> districts,
        CancellationToken cancel)
    {
        var data = new OsmSetData
        {
            Frame = new TileRange(600, 5_700, 800, 5_900),
            PlayZone = PlayZone.Anywhere,
            Masks = [],
            Land = [],
            Reachable = reachable,
            Districts = districts,
            Notes = ["тест"],
        };
        var parameters = new PipelineParams { Frame = new Frame(23.5, 52.0, 23.9, 52.2), CityRelationId = 1, CountryRelationId = 2 };
        var source = new SetSource { File = "test.osm.pbf", Sha256 = new string('0', 64) };
        var fingerprint = OsmSetFile.Fingerprint(data, parameters.ToCanonicalJson());
        var builtAt = DateTimeOffset.UtcNow;
        var content = new OsmSetFileContent(
            version, data, source, OsmSetFile.Metadata(version, data, parameters, source, fingerprint, builtAt), fingerprint, builtAt);
        await using var db = database.CreateContext();
        Assert.Equal(SetImporter.Outcome.Imported, await SetImporter.ImportAsync(db, content, DateTimeOffset.UtcNow, cancel));
    }

    /// <summary>Конфиг с набором действует с 2200 года (для остальных тестов — никогда), часы теста — туда же.</summary>
    private async Task UseFromYear2200Async(ApiFactory api, int setVersion)
    {
        var activeFrom = new DateTimeOffset(2200, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(setVersion);
        await using (var db = database.CreateContext())
        {
            db.GameConfigs.Add(new GameConfigEntity
            {
                Version = 2_000_000 + setVersion, // новее конфигов OsmSetTests (они действуют с 2100 года)
                Json = (GameConfig.Default with { Osm = new OsmConfig { SetVersion = setVersion } }).ToJson(),
                ActiveFrom = activeFrom,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Cancel);
        }

        api.Time.SetUtcNow(activeFrom.AddMinutes(1));
    }
}
