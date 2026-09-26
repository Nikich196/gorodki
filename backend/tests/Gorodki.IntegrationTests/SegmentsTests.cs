using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Segments;
using Gorodki.Domain.Leagues;
using static Gorodki.IntegrationTests.RunRequests;

namespace Gorodki.IntegrationTests;

/// <summary>
/// «Короли участков» — <c>/segments</c> (PLAN.md, §3.13). Задача #TBD-E16 для Егора: тесты со <c>Skip</c> снимаются вместе с
/// реализацией. Отрезки в базе даёт C16 (задача Claude); когда они будут, сюда — тесты прохода по прогулке (ворота, коридор,
/// «бег» быстрее 30 км/ч не засчитан), короны и границы публичности (чужой проход виден, когда конец забега публичен).
/// Правила прохода — <c>SegmentEffortDetectorTests</c> без Docker.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class SegmentsTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(Skip = "ЗАДАЧА #TBD-E16")]
    public async Task List_is_per_league_and_a_wrong_league_is_rejected()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();

        var run = (await anna.GetFromJsonAsync<SegmentListResponse>("/segments", Json, Cancel))!;
        var bike = (await anna.GetFromJsonAsync<SegmentListResponse>("/segments?league=bike", Json, Cancel))!;

        Assert.Equal((League.Run, League.Bike), (run.League, bike.League));
        Assert.Equal((400, "segments_invalid"), await Problems.OfAsync(await anna.GetAsync("/segments?league=walk", Cancel), Cancel));
    }

    [Fact(Skip = "ЗАДАЧА #TBD-E16")]
    public async Task Unknown_segment_is_not_found()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var id = Guid.NewGuid();

        Assert.Equal((404, "segment_not_found"), await Problems.OfAsync(await anna.GetAsync($"/segments/{id}", Cancel), Cancel));
        Assert.Equal((404, "segment_not_found"), await Problems.OfAsync(await anna.GetAsync($"/segments/{id}/leaderboard", Cancel), Cancel));
        Assert.NotEqual(HttpStatusCode.OK, (await anna.GetAsync($"/segments/{id}/leaderboard?season=-1", Cancel)).StatusCode);
    }
}
