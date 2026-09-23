using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Me;
using Gorodki.Domain.Geo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Приватные зоны на настоящей базе (PLAN.md, §3.16): свои зоны видит и убирает только сам игрок, их не больше трёх,
/// визиты в них не засчитываются, они входят в «мои данные».
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PrivacyZonesTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Zones_belong_to_their_owner_and_are_limited()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();

        var created = new List<PrivacyZoneResponse>();
        for (var i = 0; i < 3; i++)
        {
            var response = await anna.PostAsJsonAsync("/me/privacy-zones", new PrivacyZoneRequest(52.09 + (i * 0.01), 23.7), Json, Cancel);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            created.Add((await response.Content.ReadFromJsonAsync<PrivacyZoneResponse>(Json, Cancel))!);
        }

        var fourth = await anna.PostAsJsonAsync("/me/privacy-zones", new PrivacyZoneRequest(52.2, 23.7), Json, Cancel);
        var invalid = await boris.PostAsJsonAsync("/me/privacy-zones", new PrivacyZoneRequest(91, 23.7), Json, Cancel);
        var foreign = await boris.DeleteAsync($"/me/privacy-zones/{created[0].Id}", Cancel);
        var borisSees = await boris.GetFromJsonAsync<List<PrivacyZoneResponse>>("/me/privacy-zones", Json, Cancel);
        var own = await anna.DeleteAsync($"/me/privacy-zones/{created[0].Id}", Cancel);
        var annaSees = (await anna.GetFromJsonAsync<List<PrivacyZoneResponse>>("/me/privacy-zones", Json, Cancel))!;
        var export = await anna.GetFromJsonAsync<AccountExportResponse>("/me/export", Json, Cancel);

        Assert.Equal(HttpStatusCode.Conflict, fourth.StatusCode);
        Assert.Contains("zone_limit", await fourth.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode); // чужую зону не убрать и не увидеть
        Assert.Empty(borisSees!);
        Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);
        Assert.Equal(created.Skip(1).Select(z => z.Id), annaSees.Select(z => z.Id));
        Assert.All(annaSees, z => Assert.Equal(400, z.RadiusMeters));
        Assert.Equal(annaSees.Select(z => z.Id), export!.PrivacyZones.Select(z => z.Id));
    }

    [Fact]
    public async Task Walk_inside_a_privacy_zone_is_not_a_visit()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync(); // до сдвига часов: токен «из будущего» не прошёл бы проверку
        var area = NewArea();
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);

        // Зона вокруг квадрата — как дом, рядом с которым игрок бегает каждый день.
        var (lat, lon) = Utm34.Inverse(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        var zone = await anna.PostAsJsonAsync("/me/privacy-zones", new PrivacyZoneRequest(lat, lon), Json, Cancel);
        Assert.Equal(HttpStatusCode.Created, zone.StatusCode);
        api.Time.Advance(TimeSpan.FromHours(21));

        // Та же прогулка, что засчитывается визитом без зоны (VisitsTests): 700 м по прямой через квадрат.
        var run = await WalkAndFinishAsync(Cancel, api, anna, [(area.X - 300, area.Y + 50), (area.X + 400, area.Y + 50)]);
        api.Time.Advance(Gorodki.Api.Features.Territory.TerritoryReader.PublicDelay);
        await using var scope = api.Services.CreateAsyncScope();

        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<VisitProcessor>().ProcessRunAsync(run.Id, Cancel));
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.Parcels.Where(p => p.OwnerId == annaId).Select(p => (int)p.Level).SingleAsync(Cancel));
    }
}
