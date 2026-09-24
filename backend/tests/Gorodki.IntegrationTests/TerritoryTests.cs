using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class TerritoryTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Captured_block_appears_on_the_map_and_known_tiles_are_not_sent_again()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, userId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var claim = await WalkAndClaimAsync(Cancel, api, client, Square(area, 0, 0, 100));
        await ProcessAsync(api, claim.RunId);
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);

        var first = await client.GetFromJsonAsync<TerritoryResponse>($"/territory?league=run&tiles={tile.X}:{tile.Y}", Json, Cancel);
        var version = first!.Tiles.Single().Version;
        var again = await client.GetFromJsonAsync<TerritoryResponse>(
            $"/territory?league=run&tiles={tile.X}:{tile.Y}@{version}", Json, Cancel);

        var parcel = Assert.Single(first.Tiles.Single().Parcels);
        Assert.Equal((userId, (short)1), (parcel.OwnerId, parcel.Level));
        Assert.True(version >= 1);
        Assert.InRange(AreaOf(parcel.Exterior), 9_500, 10_500);
        Assert.Empty(again!.Tiles);
        Assert.Equal(new TileRef(tile.X, tile.Y), Assert.Single(again.Unchanged));
    }

    [Fact]
    public async Task Land_decays_to_a_ghost_and_then_disappears_from_the_map()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, userId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var claim = await WalkAndClaimAsync(Cancel, api, client, Square(area, 0, 0, 100));
        await ProcessAsync(api, claim.RunId);
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);

        // Чтение — напрямую через сервис: после сдвига часов на неделю токен для HTTP мог бы истечь.
        api.Time.Advance(TimeSpan.FromDays(7));
        var ghost = (await ReadAsync(api, tile)).Parcels.Single();
        api.Time.Advance(TimeSpan.FromDays(3));
        var gone = (await ReadAsync(api, tile)).Parcels;

        Assert.Equal((userId, (short)0, true), (ghost.OwnerId, ghost.Level, ghost.Ghost));
        Assert.Empty(gone);
    }

    [Fact]
    public async Task Others_see_a_capture_only_after_the_public_delay_and_nothing_gives_it_away_before()
    {
        // PLAN.md, §3.16: чужие видят изменения через 20 минут — иначе карта показывала бы, где человек прямо сейчас.
        // Скрытый захват не выдаёт себя и версией тайла: с прежней версией тайл «без изменений».
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        var (demo, _) = await api.CreatePlayerClientAsync(Gorodki.Api.Infrastructure.Persistence.UserRole.Demo);
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        var before = await TileAsync(boris, tile);
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);

        var own = await TileAsync(anna, tile);
        var hidden = await TileAsync(boris, tile);
        var sameVersion = await IsUnchangedAsync(boris, tile, before.Version);
        var demoViewer = await TileAsync(demo, tile);

        Assert.Equal(annaId, Assert.Single(own.Parcels).OwnerId); // свой захват — сразу
        Assert.True(own.Version > before.Version);
        Assert.Equal(before.Version, hidden.Version); // видимая версия не сдвинулась
        Assert.Empty(hidden.Parcels);
        Assert.True(sameVersion); // с прежней версией — «без изменений»
        Assert.Empty(demoViewer.Parcels); // демо-зритель видит карту как все

        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        var revealed = await TileAsync(boris, tile, known: before.Version);

        Assert.True(revealed.Version > before.Version);
        var parcel = Assert.Single(revealed.Parcels);
        Assert.Equal(annaId, parcel.OwnerId);
        Assert.Equal(0, parcel.LastVisitAtMs % 3_600_000); // время чужого визита — с точностью до часа
    }

    [Fact]
    public async Task Demo_account_captures_are_public_at_once()
    {
        // PLAN.md, §11, показ: «захват точно по контуру, /live обновился» — изменения демо-аккаунта без задержки.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (demo, demoId) = await api.CreatePlayerClientAsync(Gorodki.Api.Infrastructure.Persistence.UserRole.Demo);
        var (boris, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, demo, Square(area, 0, 0, 100))).RunId);

        var seen = await TileAsync(boris, TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50));

        Assert.Equal(demoId, Assert.Single(seen.Parcels).OwnerId);
    }

    [Fact]
    public async Task While_hidden_the_land_looks_as_it_was_before_the_capture()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (vera, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep); // захват Анны уже публичен
        var beforeBoris = await TileAsync(vera, tile);
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100))).RunId);

        var seenByVera = await TileAsync(vera, tile);
        var veraUnchanged = await IsUnchangedAsync(vera, tile, beforeBoris.Version);
        var seenByBoris = await TileAsync(boris, tile);
        var seenByAnna = await TileAsync(anna, tile);

        // Вера и сама Анна видят квадрат Анны целым — захват Бориса ещё скрыт и версию не сдвигает.
        var annaLand = Assert.Single(seenByVera.Parcels);
        Assert.Equal(annaId, annaLand.OwnerId);
        Assert.InRange(AreaOf(annaLand.Exterior), 9_500, 10_500);
        Assert.Equal(beforeBoris.Version, seenByVera.Version);
        Assert.True(veraUnchanged);
        Assert.True(annaLand.Id > 0); // номер собранного проекцией куска — такой же, как у настоящего
        Assert.InRange(AreaOf(Assert.Single(seenByAnna.Parcels).Exterior), 9_500, 10_500);
        // Борис свой захват видит сразу: половина Анны — его, щит — до миллисекунды.
        var borisOwn = seenByBoris.Parcels.Where(p => p.OwnerId == borisId).ToList();
        Assert.InRange(borisOwn.Sum(p => AreaOf(p.Exterior)), 9_500, 10_500);

        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        var later = await TileAsync(vera, tile, known: beforeBoris.Version);

        Assert.True(later.Version > beforeBoris.Version);
        var borisLand = later.Parcels.Where(p => p.OwnerId == borisId).ToList();
        Assert.InRange(borisLand.Sum(p => AreaOf(p.Exterior)), 9_500, 10_500);
        Assert.InRange(later.Parcels.Where(p => p.OwnerId == annaId).Sum(p => AreaOf(p.Exterior)), 4_700, 5_300);
        // Чужой щит (12 ч после захвата) — с точностью до 10 минут: минута захвата не видна и после раскрытия.
        Assert.Contains(borisLand, p => p.ShieldUntilMs is not null);
        Assert.All(borisLand.Where(p => p.ShieldUntilMs is not null), p => Assert.Equal(0, p.ShieldUntilMs!.Value % 600_000));
    }

    [Fact]
    public async Task Capture_that_changes_no_land_leaves_the_map_as_it_was_for_everyone()
    {
        // Аудит BE-01: новичок обвёл землю Анны и Веры поперёк их границ и ничего не взял (§3.3). Раньше куски всё равно
        // переписывались — с вершинами там, где прошла петля, — а версия тайла росла без записи в журнале: все видели
        // перемену сразу, а не через 20 минут.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (vera, _) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync(newcomer: true);
        var (gleb, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        await NeighboursAsync(api, anna, vera, area);
        var before = await TileAsync(gleb, tile);

        var claim = await WalkAndClaimAsync(Cancel, api, boris, Rectangle(area, 25, 20, 100, 60));
        await ProcessAsync(api, claim.RunId);

        await using (var db = database.CreateContext())
        {
            var capture = await db.Captures.AsNoTracking().SingleAsync(c => c.Id == claim.CaptureId, Cancel);
            Assert.Equal(CaptureStatus.Applied, capture.Status);
            Assert.Contains("newAccountLimited", capture.AreaByOutcome!); // петля правда прошла по чужой земле
        }

        var after = await TileAsync(gleb, tile);
        Assert.Equal(before.Version, (await ReadAsync(api, tile)).Version); // настоящая версия не выросла
        Assert.Equal(before.Version, after.Version);
        Assert.True(await IsUnchangedAsync(gleb, tile, before.Version));
        Assert.Equal(Ids(before), Ids(after));
    }

    [Fact]
    public async Task While_hidden_a_capture_leaves_no_trace_on_the_neighbours_land()
    {
        // Аудит BE-01: новичок обвёл землю Анны и Веры и ничью рядом — взял только ничью. Пока захват скрыт, Глеб получает
        // тайл, откаченный по журналу. Куски Анны и Веры в нём те же до вершины и с теми же номерами: раньше на них
        // оставались вершины ровно там, где скрытая петля пересекла их границы.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (vera, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync(newcomer: true);
        var (gleb, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        await NeighboursAsync(api, anna, vera, area);
        var before = await TileAsync(gleb, tile);

        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Rectangle(area, 25, 20, 200, 60))).RunId);
        var hidden = await TileAsync(gleb, tile);

        Assert.True((await ReadAsync(api, tile)).Version > before.Version); // захват применён и тайл изменился
        Assert.Equal(before.Version, hidden.Version);
        Assert.Equal(Ids(before), Ids(hidden));

        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        var revealed = await TileAsync(gleb, tile, known: before.Version);

        // После раскрытия у Анны и Веры всё те же куски, у Бориса — взятая ничья земля.
        Assert.True(revealed.Version > before.Version);
        Assert.Equal(Ids(before), Ids(revealed).Where(id => revealed.Parcels.Single(p => p.Id == id).OwnerId != borisId).ToList());
        Assert.Contains(revealed.Parcels, p => p.OwnerId == borisId);
    }

    /// <summary>
    /// Анна — квадрат 100 × 100 м, Вера забрала его правую половину и ничью землю рядом: три куска с общими границами
    /// (у Веры два — со щитом и без). Оба захвата уже публичны.
    /// </summary>
    private async Task NeighboursAsync(ApiFactory api, HttpClient anna, HttpClient vera, (double X, double Y) area)
    {
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromMinutes(30));
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, vera, Square(area, 50, 0, 100))).RunId);
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
    }

    private static List<long> Ids(TileTerritory tile) => [.. tile.Parcels.Select(p => p.Id).Order()];

    private async Task<bool> IsUnchangedAsync(HttpClient client, TileKey tile, long known)
    {
        var response = await client.GetFromJsonAsync<TerritoryResponse>(
            $"/territory?league=run&tiles={tile.X}:{tile.Y}@{known}", Json, Cancel);
        return response!.Tiles.Count == 0 && response.Unchanged.Single() == new TileRef(tile.X, tile.Y);
    }

    private async Task<TileTerritory> TileAsync(HttpClient client, TileKey tile, long? known = null)
    {
        var at = known is { } version ? $"@{version}" : "";
        var response = await client.GetFromJsonAsync<TerritoryResponse>($"/territory?league=run&tiles={tile.X}:{tile.Y}{at}", Json, Cancel);
        return Assert.Single(response!.Tiles);
    }

    private static async Task<TileTerritory> ReadAsync(ApiFactory api, TileKey tile)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<TerritoryReader>();
        return (await reader.ReadAsync(Gorodki.Domain.Leagues.League.Run, [(tile, null)], new TerritoryViewer(null, Immediate: true), CancellationToken.None))
            .Tiles.Single();
    }

    [Fact]
    public async Task Empty_tile_has_version_zero_and_no_parcels()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();

        var response = await client.GetFromJsonAsync<TerritoryResponse>("/territory?league=bike&tiles=1:1", Json, Cancel);

        var tile = Assert.Single(response!.Tiles);
        Assert.Equal((0L, 0), (tile.Version, tile.Parcels.Count));
    }

    [Fact]
    public async Task Broken_query_is_refused()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();

        var response = await client.GetAsync("/territory?league=swim&tiles=1:1", Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Площадь кольца [широта, долгота, …] — обратно в UTM и по формуле площади многоугольника.</summary>
    private static double AreaOf(IReadOnlyList<double> ring)
    {
        var coordinates = new Coordinate[ring.Count / 2];
        for (var i = 0; i < coordinates.Length; i++)
        {
            var (easting, northing) = Utm34.Forward(ring[2 * i], ring[(2 * i) + 1]);
            coordinates[i] = new Coordinate(easting, northing);
        }

        return GeoOps.Factory.CreatePolygon(coordinates).Area;
    }
}
