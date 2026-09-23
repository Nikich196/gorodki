using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Territory;
using Gorodki.Domain.Geo;
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
    public async Task Others_see_a_capture_only_after_the_public_delay()
    {
        // PLAN.md, §3.16: чужие видят изменения через 20 минут — иначе карта показывала бы, где человек прямо сейчас.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        var (demo, _) = await api.CreatePlayerClientAsync(Gorodki.Api.Infrastructure.Persistence.UserRole.Demo);
        var area = NewArea();
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);

        var own = await TileAsync(anna, tile);
        var hidden = await TileAsync(boris, tile);
        var again = await TileAsync(boris, tile, known: hidden.Version);
        var onStage = await TileAsync(demo, tile);

        Assert.True(own.Version >= 1);
        Assert.Null(own.RevealAtMs);
        Assert.Equal(annaId, Assert.Single(own.Parcels).OwnerId); // свой захват — сразу
        Assert.Equal(0, hidden.Version); // приложение перезапросит тайл
        Assert.Empty(hidden.Parcels);
        Assert.NotNull(hidden.RevealAtMs);
        Assert.Empty(again.Parcels); // с версией 0 тайл приходит заново, а не «без изменений»
        Assert.Single(onStage.Parcels); // демо на показе — без задержки

        api.Time.Advance(TerritoryReader.PublicDelay + TimeSpan.FromMinutes(1));
        var revealed = await TileAsync(boris, tile, known: 0);

        Assert.Equal(own.Version, revealed.Version);
        Assert.Null(revealed.RevealAtMs);
        var parcel = Assert.Single(revealed.Parcels);
        Assert.Equal(annaId, parcel.OwnerId);
        Assert.Equal(0, parcel.LastVisitAtMs % 3_600_000); // время чужого визита — с точностью до часа
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
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromMinutes(30)); // захват Анны уже публичен
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100))).RunId);
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);

        var seenByVera = await TileAsync(vera, tile);
        var seenByBoris = await TileAsync(boris, tile);
        var seenByAnna = await TileAsync(anna, tile);

        // Вера и сама Анна видят квадрат Анны целым — захват Бориса ещё скрыт.
        var annaLand = Assert.Single(seenByVera.Parcels);
        Assert.Equal(annaId, annaLand.OwnerId);
        Assert.InRange(AreaOf(annaLand.Exterior), 9_500, 10_500);
        Assert.True(annaLand.Id < 0); // кусок собран проекцией — временный номер
        Assert.InRange(AreaOf(Assert.Single(seenByAnna.Parcels).Exterior), 9_500, 10_500);
        // Борис свой захват видит сразу: половина Анны — его.
        Assert.InRange(seenByBoris.Parcels.Where(p => p.OwnerId == borisId).Sum(p => AreaOf(p.Exterior)), 9_500, 10_500);

        api.Time.Advance(TerritoryReader.PublicDelay + TimeSpan.FromMinutes(1));
        var later = await TileAsync(vera, tile);

        Assert.InRange(later.Parcels.Where(p => p.OwnerId == borisId).Sum(p => AreaOf(p.Exterior)), 9_500, 10_500);
        Assert.InRange(later.Parcels.Where(p => p.OwnerId == annaId).Sum(p => AreaOf(p.Exterior)), 4_700, 5_300);
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
