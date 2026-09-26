using System.Net.Http.Json;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Osm;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Config;
using Gorodki.Domain.Fog;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Osm;
using Gorodki.OsmPipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Набор конвейера OSM в базе (docs/architecture/osm-pipeline.md): загрузка идемпотентна, маски читаются по тайлам и
/// вычитаются в шаге A захвата, «достижимое» читается для «% Бреста». Без набора в конфиге захват — как раньше.
/// </summary>
/// <remarks>
/// Каждый тест — со своим номером набора; конфиг с набором заводится с началом действия в 2100 году и ставится только
/// своему забегу: для остальных тестов действующий конфиг не меняется.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class OsmSetTests(DatabaseFixture database)
{
    private static int _nextSet = 900;

    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// Рамка — с запасом все тайлы, где гуляют интеграционные тесты: <see cref="Walks.NewArea"/> сдвигает каждое новое место
    /// на 1,5 км, и при полном прогоне места уходят на десятки километров к северу.
    /// </summary>
    private static readonly TileRange TestFrame = new(600, 5_700, 800, 5_900);

    private static int NewSetVersion() => Interlocked.Increment(ref _nextSet);

    /// <summary>Маска вида <paramref name="kind"/>: прямоугольник (метры от места теста), нарезанный по тайлам UTM.</summary>
    private static IEnumerable<MaskPiece> Pieces(MaskKind kind, (double X, double Y) area, double x1, double y1, double x2, double y2)
    {
        var (ox, oy) = (WalkOrigin.X + area.X, WalkOrigin.Y + area.Y);
        var box = GeoOps.Factory.ToGeometry(new Envelope(ox + x1, ox + x2, oy + y1, oy + y2));
        return TileKey.Covering(box.EnvelopeInternal)
            .SelectMany(tile => GeoOps.Polygons(GeoOps.Intersection(box, tile.ToPolygon())).Select(p => new MaskPiece(kind, tile, (Polygon)p.Normalized())));
    }

    private static OsmSetData Set(IEnumerable<MaskPiece> masks, PlayZone zone = PlayZone.Anywhere, SortedDictionary<FogTileKey, FogTileBits>? reachable = null, IReadOnlyList<DistrictData>? districts = null) => new()
    {
        Frame = TestFrame,
        PlayZone = zone,
        Masks = [.. masks],
        Land = [],
        Reachable = reachable ?? new SortedDictionary<FogTileKey, FogTileBits>(),
        Districts = districts ?? [],
        Notes = ["тест"],
    };

    private static OsmSetFileContent Content(int version, OsmSetData data)
    {
        var parameters = new PipelineParams { Frame = new Frame(23.5, 52.0, 23.9, 52.2), CityRelationId = 1, CountryRelationId = 2 };
        var source = new SetSource { File = "test.osm.pbf", Sha256 = new string('0', 64) };
        var fingerprint = OsmSetFile.Fingerprint(data, parameters.ToCanonicalJson());
        var builtAt = DateTimeOffset.UtcNow;
        return new OsmSetFileContent(version, data, source, OsmSetFile.Metadata(version, data, parameters, source, fingerprint, builtAt), fingerprint, builtAt);
    }

    private async Task<SetImporter.Outcome> ImportAsync(OsmSetFileContent content)
    {
        await using var db = database.CreateContext();
        return await SetImporter.ImportAsync(db, content, DateTimeOffset.UtcNow, Cancel);
    }

    /// <summary>Ставит забегу версию конфига с набором <paramref name="setVersion"/> (или без набора).</summary>
    private async Task UseSetAsync(Guid runId, int? setVersion)
    {
        await using var db = database.CreateContext();
        var version = 1_000_000 + (setVersion ?? Interlocked.Increment(ref _nextSet));
        db.GameConfigs.Add(new GameConfigEntity
        {
            Version = version,
            Json = (GameConfig.Default with { Osm = new OsmConfig { SetVersion = setVersion } }).ToJson(),
            ActiveFrom = new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), // никогда не станет действующей
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Cancel);
        await db.Runs.Where(r => r.Id == runId).ExecuteUpdateAsync(set => set.SetProperty(r => r.ConfigVersion, version), Cancel);
    }

    private async Task<CaptureResponse> WalkSquareWithSetAsync(ApiFactory api, HttpClient client, (double X, double Y) area, int? setVersion)
    {
        var claim = await WalkAndClaimAsync(Cancel, api, client, Square(area, 0, 0, 100));
        await UseSetAsync(claim.RunId, setVersion);
        Assert.Equal(1, await ProcessAsync(api, claim.RunId));
        var captures = await client.GetFromJsonAsync<List<CaptureResponse>>($"/runs/{claim.RunId}/captures", Json, Cancel);
        return captures!.Single(c => c.Id == claim.CaptureId);
    }

    // ── Загрузка ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Importing_the_same_set_twice_changes_nothing_the_second_time()
    {
        database.RequireDatabase();
        var version = NewSetVersion();
        var area = NewArea();
        var content = Content(version, Set(Pieces(MaskKind.Water, area, 40, -50, 60, 150)));

        Assert.Equal(SetImporter.Outcome.Imported, await ImportAsync(content));
        Assert.Equal(SetImporter.Outcome.AlreadyImported, await ImportAsync(content));

        await using var db = database.CreateContext();
        Assert.Equal(1, await db.OsmSets.CountAsync(s => s.Version == version, Cancel));
        Assert.Equal(content.Data.Masks.Count, await db.Masks.CountAsync(m => m.SetVersion == version, Cancel));
        var stored = await db.OsmSets.SingleAsync(s => s.Version == version, Cancel);
        Assert.Equal(content.Fingerprint, stored.Fingerprint);
        Assert.Contains("OpenStreetMap", stored.Metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_set_under_a_taken_number_is_refused_and_the_old_one_stays()
    {
        database.RequireDatabase();
        var version = NewSetVersion();
        var area = NewArea();
        await ImportAsync(Content(version, Set(Pieces(MaskKind.Water, area, 40, -50, 60, 150))));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ImportAsync(Content(version, Set(Pieces(MaskKind.Rail, area, 0, 0, 10, 10)))));

        await using var db = database.CreateContext();
        Assert.All(await db.Masks.Where(m => m.SetVersion == version).ToListAsync(Cancel), m => Assert.Equal(MaskKind.Water, m.Kind));
    }

    [Fact]
    public async Task Invalid_mask_geometry_is_refused_by_the_database()
    {
        database.RequireDatabase();
        var version = NewSetVersion();
        await ImportAsync(Content(version, Set([])));
        var bowTie = GeoOps.Factory.CreatePolygon(
        [
            new Coordinate(684_200, 5_775_200), new Coordinate(684_300, 5_775_300), new Coordinate(684_300, 5_775_200),
            new Coordinate(684_200, 5_775_300), new Coordinate(684_200, 5_775_200),
        ]);

        await using var db = database.CreateContext();
        db.Masks.Add(new MaskEntity { SetVersion = version, Kind = MaskKind.Water, TileX = 684, TileY = 5775, Geometry = bowTie });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
        Assert.Contains("ck_masks_geometry_valid", error.InnerException?.Message ?? "", StringComparison.Ordinal);
    }

    // ── Маски по тайлам ──────────────────────────────────────────────────────

    /// <remarks>
    /// Маски — внутри тайла места теста, а не через его край: место (<see cref="Walks.NewArea"/>) по номеру в общем
    /// счётчике всей сборки ложится то в середину тайла, то ровно на границу километра (остаток от деления на 1000 — 0 или
    /// 500). Прямоугольник через край разрезался бы на два ряда тайлов, и запрос по квадрату места читал бы один из них —
    /// тест зависел бы от числа и порядка остальных тестов.
    /// </remarks>
    [Fact]
    public async Task Mask_store_unions_the_masks_of_the_covered_tiles()
    {
        database.RequireDatabase();
        var version = NewSetVersion();
        var area = NewArea();
        await ImportAsync(Content(version, Set([.. Pieces(MaskKind.Water, area, 40, 0, 60, 200), .. Pieces(MaskKind.Rail, area, 50, 0, 70, 200)])));
        await using var api = new ApiFactory(database);
        await using var scope = api.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IMaskStore>();
        var (ox, oy) = (WalkOrigin.X + area.X, WalkOrigin.Y + area.Y);

        var masks = await store.CoveringAsync(new Envelope(ox, ox + 100, oy, oy + 100), version, Cancel);
        var again = await store.CoveringAsync(new Envelope(ox, ox + 100, oy, oy + 100), version, Cancel); // из кэша

        Assert.NotNull(masks);
        Assert.Equal(30 * 200, masks.Area, 1); // вода и ж/д накладываются — объединение, а не сумма
        Assert.True(masks.EqualsExact(again!));
        Assert.NotSame(masks, again); // каждый вызов — новые объекты
        var tile = TileKey.Of(ox + 50, oy + 50);
        var neighbour = new Envelope(((tile.X + 1) * 1_000) + 100, ((tile.X + 1) * 1_000) + 200, (tile.Y * 1_000) + 100, (tile.Y * 1_000) + 200);
        Assert.Null(await store.CoveringAsync(neighbour, version, Cancel)); // в соседнем тайле масок нет
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CoveringAsync(new Envelope(ox, ox + 1, oy, oy + 1), 999_999, Cancel));
    }

    [Fact]
    public async Task Tile_outside_the_frame_is_wholly_outside_the_play_zone_only_when_capture_is_limited()
    {
        database.RequireDatabase();
        var limited = NewSetVersion();
        var open = NewSetVersion();
        await ImportAsync(Content(limited, Set([], PlayZone.City)));
        await ImportAsync(Content(open, Set([])));
        await using var api = new ApiFactory(database);
        await using var scope = api.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IMaskStore>();
        var farAway = new Envelope(950_100, 950_200, 5_950_100, 5_950_200); // за рамкой 600–800 × 5700–5900

        var outside = await store.CoveringAsync(farAway, limited, Cancel);

        Assert.NotNull(outside);
        Assert.True(outside.EqualsTopologically(new TileKey(950, 5950).ToPolygon()));
        Assert.Null(await store.CoveringAsync(farAway, open, Cancel));
    }

    // ── Маски в шаге A захвата ───────────────────────────────────────────────

    [Fact]
    public async Task Loop_across_a_river_takes_the_land_without_the_river()
    {
        database.RequireDatabase();
        var version = NewSetVersion();
        var area = NewArea();
        await ImportAsync(Content(version, Set(Pieces(MaskKind.Water, area, 40, -50, 60, 150))));
        await using var api = new ApiFactory(database);
        var (client, userId) = await api.CreatePlayerClientAsync();

        var capture = await WalkSquareWithSetAsync(api, client, area, version);

        Assert.Equal(CaptureStatus.Applied, capture.Status);
        Assert.InRange(capture.AreaSquareMeters, 7_500, 8_500); // 100 × 100 минус река 20 × 100
        await using var db = database.CreateContext();
        var land = await db.Parcels.Where(p => p.OwnerId == userId).Select(p => p.Geometry).ToListAsync(Cancel);
        var river = GeoOps.UnionAll(Pieces(MaskKind.Water, area, 40, -50, 60, 150).Select(p => (Geometry)p.Geometry));
        Assert.All(land, piece => Assert.True(GeoOps.Intersection(piece, river).Area < 0.01)); // ни один кусок не заходит в маску
    }

    [Fact]
    public async Task Loop_wholly_inside_a_mask_is_rejected_as_empty()
    {
        database.RequireDatabase();
        var version = NewSetVersion();
        var area = NewArea();
        await ImportAsync(Content(version, Set(Pieces(MaskKind.Cemetery, area, -50, -50, 150, 150))));
        await using var api = new ApiFactory(database);
        var (client, userId) = await api.CreatePlayerClientAsync();

        var capture = await WalkSquareWithSetAsync(api, client, area, version);

        Assert.Equal(CaptureStatus.Rejected, capture.Status);
        Assert.Equal("empty", capture.RejectCode);
        await using var db = database.CreateContext();
        Assert.False(await db.Parcels.AnyAsync(p => p.OwnerId == userId, Cancel));
    }

    [Fact]
    public async Task Without_a_set_or_with_an_empty_one_capture_is_as_before()
    {
        database.RequireDatabase();
        var empty = NewSetVersion();
        await ImportAsync(Content(empty, Set([])));
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();

        var withoutSet = await WalkSquareWithSetAsync(api, client, NewArea(), setVersion: null);
        api.Time.Advance(TimeSpan.FromMinutes(30));
        var withEmptySet = await WalkSquareWithSetAsync(api, client, NewArea(), empty);

        Assert.Equal(CaptureStatus.Applied, withoutSet.Status);
        Assert.Equal(CaptureStatus.Applied, withEmptySet.Status);
        Assert.InRange(withoutSet.AreaSquareMeters, 9_500, 10_500);
        Assert.InRange(withEmptySet.AreaSquareMeters, 9_500, 10_500);
        Assert.Null(withEmptySet.RejectCode);
    }

    // ── «Достижимое» для «% Бреста» ──────────────────────────────────────────

    [Fact]
    public async Task Reachable_store_reads_the_city_and_districts_and_gives_their_shares()
    {
        database.RequireDatabase();
        var version = NewSetVersion();
        FogTileBits Bits(params int[] set)
        {
            var bits = new FogTileBits();
            foreach (var bit in set)
            {
                bits.Set(bit);
            }

            return bits;
        }

        var tile = new FogTileKey(9_380, 5_390);
        var city = new SortedDictionary<FogTileKey, FogTileBits> { [tile] = Bits(1, 2, 3, 4) };
        var quarter = new DistrictData(
            "quarter:1", DistrictKind.Quarter, "Кампус", null, true, GeoOps.Factory.CreateMultiPolygon([new TileKey(684, 5775).ToPolygon()]), 1_000_000,
            new SortedDictionary<FogTileKey, FogTileBits> { [tile] = Bits(1, 2) });
        var cityDistrict = new DistrictData(
            "city", DistrictKind.City, "Брест", 72615, false, GeoOps.Factory.CreateMultiPolygon([new TileKey(684, 5775).ToPolygon()]), 1_000_000, city);
        await ImportAsync(Content(version, Set([], reachable: city, districts: [cityDistrict, quarter])));
        await using var api = new ApiFactory(database);
        await using var scope = api.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ReachableStore>();

        var reach = await store.LoadAsync(version, Cancel);
        var shares = reach!.SharesOf(new Dictionary<FogTileKey, FogTileBits> { [tile] = Bits(1, 4, 60_000) });

        Assert.Equal(4, reach.City.TotalCells);
        Assert.Equal(new ExploredShare(2, 4), shares.City); // клетка 60 000 — вне «достижимого»
        var campus = Assert.Single(shares.Districts);
        Assert.Equal(("Кампус", true, new ExploredShare(1, 2)), (campus.Name, campus.Proposal, campus.Share));
        Assert.Null(await store.LoadAsync(999_999, Cancel));
        Assert.Null(await store.CurrentSetVersionAsync(Cancel)); // действующий конфиг набора не называет — процента нет
    }
}
