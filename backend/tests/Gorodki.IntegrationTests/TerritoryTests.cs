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

    private static async Task<TileTerritory> ReadAsync(ApiFactory api, TileKey tile)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<TerritoryReader>();
        return (await reader.ReadAsync(Gorodki.Domain.Leagues.League.Run, [(tile, null)], CancellationToken.None)).Tiles.Single();
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
