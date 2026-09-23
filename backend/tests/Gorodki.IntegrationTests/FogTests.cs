using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Fog;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Fog;
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
        var foot = Assert.Single(summary!.Layers);
        Assert.Equal((FogLayerKind.Foot, opened.Value), (foot.Layer, foot.CellCount));
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
    public async Task Broken_query_is_refused()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();

        var response = await client.GetAsync("/fog?layer=swim", Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
