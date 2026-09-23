using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Fog;
using Gorodki.Api.Features.Me;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// «Мои данные» на настоящей базе (PLAN.md, §3.16, закон 99-З): выгрузка содержит всё, что сервер хранит об игроке,
/// и ничего чужого.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AccountExportTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Export_contains_everything_stored_about_the_player_and_nothing_else()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var claim = await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100));
        await Walks.ProcessAsync(api, claim.RunId);
        var walk = await WalkAndFinishAsync(Cancel, api, anna, Square(area, 0, 0, 100));
        int cells;
        await using (var scope = api.Services.CreateAsyncScope())
        {
            cells = (await scope.ServiceProvider.GetRequiredService<FogProcessor>().StampRunAsync(walk.Id, Cancel))!.Value;
            await using var db = database.CreateContext();
            db.RefreshTokens.Add(scope.ServiceProvider.GetRequiredService<TokenService>().CreateRefreshToken(annaId, Guid.CreateVersion7()).Entity);
            await db.SaveChangesAsync(Cancel);
        }

        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Square(NewArea(), 0, 0, 100))).RunId);

        var response = await anna.GetAsync("/me/export", Cancel);
        var mine = (await response.Content.ReadFromJsonAsync<AccountExportResponse>(Json, Cancel))!;
        var theirs = (await boris.GetFromJsonAsync<AccountExportResponse>("/me/export", Json, Cancel))!;
        var anonymous = await api.CreateClient().GetAsync("/me/export", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(annaId, mine.Profile.Id);
        Assert.Single(mine.Sessions);

        // Забег с точками — в том же виде, в каком его прислал телефон; у старого забега точки уже стёрты.
        var walked = Assert.Single(mine.Runs, r => r.Id == walk.Id);
        Assert.Equal(WalkPoints(walk, Square(area, 0, 0, 100)).Count, walked.Points.Count);
        Assert.NotEmpty(walked.Motion);
        Assert.NotEmpty(walked.Steps);
        Assert.Equal(cells, walked.FogNewCells);
        Assert.Contains(mine.Runs, r => r.PointsErasedAtMs is not null && r.Points.Count == 0);

        var capture = Assert.Single(mine.Captures);
        Assert.Equal((claim.CaptureId, CaptureStatus.Applied), (capture.Id, capture.Status));
        var parcel = Assert.Single(mine.Land);
        Assert.True(parcel.Exterior.Count >= 8); // 4+ вершины: широта и долгота
        Assert.Equal(cells, mine.Fog.Where(f => f.Season == -1).Sum(f => f.CellCount));

        // Ничего чужого: у Бориса — только его забеги и захваты.
        Assert.DoesNotContain(theirs.Runs, r => r.Id == walk.Id || r.Id == claim.RunId);
        Assert.DoesNotContain(theirs.Captures, c => c.Id == claim.CaptureId);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
