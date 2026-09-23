using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Runs;
using static Gorodki.IntegrationTests.RunRequests;

namespace Gorodki.IntegrationTests;

/// <summary>Заявки петель: идемпотентность, порядок и то, чего ждёт заявка, — на настоящей базе.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class CapturesTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Claim_is_accepted_once_and_its_id_is_what_the_phone_can_compute()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();
        var start = await StartAsync(api, client);

        var first = await ClaimAsync(client, start, claimNo: 0, startSeq: 10, endSeq: 200);
        var repeat = await ClaimAsync(client, start, claimNo: 0, startSeq: 10, endSeq: 200);
        var sameLoopOtherStart = await ClaimAsync(client, start, claimNo: 0, startSeq: 20, endSeq: 200);
        var sameNumberOtherLoop = await ClaimAsync(client, start, claimNo: 0, startSeq: 210, endSeq: 400);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var claim = await first.Content.ReadFromJsonAsync<CaptureResponse>(Json, Cancel);
        Assert.Equal(CaptureIds.For(start.Id, 200), claim!.Id);
        Assert.Equal((CaptureStatus.Pending, "points"), (claim.Status, claim.WaitingFor));
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal("claim_conflict", await CodeOf(sameLoopOtherStart));
        Assert.Equal("claim_conflict", await CodeOf(sameNumberOtherLoop));
    }

    [Fact]
    public async Task Claim_waits_for_points_then_sensors_then_the_previous_claim()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();
        var start = await StartAsync(api, client);
        await ClaimAsync(client, start, claimNo: 0, startSeq: 0, endSeq: 150);
        await ClaimAsync(client, start, claimNo: 1, startSeq: 60, endSeq: 100);

        var noPoints = await WaitingAsync(client, start);

        // Точки 0…119 пришли, но отметка датчиков отстаёт от конца второй петли.
        var chunk = ChunkRequest(api, start, firstSeq: 0, count: 120);
        await PutAsync(client, start, 0, chunk with { SensorsCompleteThroughMs = chunk.Points![50].T });
        var noSensors = await WaitingAsync(client, start);

        await PutAsync(client, start, 120, ChunkRequest(api, start, firstSeq: 120, count: 60));
        var turn = await WaitingAsync(client, start);

        api.Time.Advance(ClaimReadiness.PreviousClaimPatience);
        var afterPatience = await WaitingAsync(client, start);

        Assert.Equal(["points", "points"], noPoints);
        Assert.Equal(["points", "sensors"], noSensors);
        Assert.Equal(["queue", "previous_claim"], turn);
        Assert.Equal(["queue", "queue"], afterPatience);
    }

    [Fact]
    public async Task Broken_or_foreign_claims_are_refused()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (owner, _) = await api.CreatePlayerClientAsync();
        var (stranger, _) = await api.CreatePlayerClientAsync();
        var start = await StartAsync(api, owner);

        var tooShort = await ClaimAsync(owner, start, claimNo: 0, startSeq: 10, endSeq: 12);
        var negative = await ClaimAsync(owner, start, claimNo: -1, startSeq: 0, endSeq: 100);
        var foreign = await ClaimAsync(stranger, start, claimNo: 0, startSeq: 0, endSeq: 100);
        var foreignList = await stranger.GetAsync($"/runs/{start.Id}/captures", Cancel);

        Assert.Equal("claim_invalid", await CodeOf(tooShort));
        Assert.Equal("claim_invalid", await CodeOf(negative));
        Assert.Equal("run_not_found", await CodeOf(foreign));
        Assert.Equal(HttpStatusCode.NotFound, foreignList.StatusCode);
    }

    // MARK: — вспомогательное

    private async Task<StartRunRequest> StartAsync(ApiFactory api, HttpClient client)
    {
        var start = NewStart(api, startedAgo: TimeSpan.FromMinutes(10));
        var response = await client.PostAsJsonAsync("/runs", start, Json, Cancel);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return start;
    }

    private Task<HttpResponseMessage> ClaimAsync(HttpClient client, StartRunRequest start, int claimNo, int startSeq, int endSeq) =>
        client.PostAsJsonAsync(
            $"/runs/{start.Id}/loops",
            new LoopClaimRequest(claimNo, startSeq, endSeq, LoopClosure.Proximity, 12_000, start.StartedAtMs + 600_000),
            Json,
            Cancel);

    private async Task PutAsync(HttpClient client, StartRunRequest start, int firstSeq, UploadChunkRequest chunk)
    {
        var response = await client.PutAsJsonAsync($"/runs/{start.Id}/chunks/{firstSeq}", chunk, Json, Cancel);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<string?[]> WaitingAsync(HttpClient client, StartRunRequest start)
    {
        var captures = await client.GetFromJsonAsync<List<CaptureResponse>>($"/runs/{start.Id}/captures", Json, Cancel);
        return [.. captures!.Select(c => c.WaitingFor)];
    }

    private async Task<string?> CodeOf(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        return problem.TryGetProperty("code", out var code) ? code.GetString() : $"HTTP {(int)response.StatusCode}";
    }
}
