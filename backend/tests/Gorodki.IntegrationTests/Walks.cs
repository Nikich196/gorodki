using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;

namespace Gorodki.IntegrationTests;

/// <summary>Прогулки для тестов захватов: старт, куски с датчиками, заявка петли, обработка.</summary>
internal static class Walks
{
    private static int _nextArea;

    /// <summary>Своё место для каждого теста: сдвиг на 1,5 км — отдельный тайл, соседние тесты не делят землю.</summary>
    public static (double X, double Y) NewArea()
    {
        var n = Interlocked.Increment(ref _nextArea);
        return (-6_000 + (1_500.0 * (n % 8)), -6_000 + (1_500.0 * (n / 8)));
    }

    public static (double X, double Y)[] Square((double X, double Y) area, double x, double y, double size) =>
    [
        (area.X + x, area.Y + y),
        (area.X + x + size, area.Y + y),
        (area.X + x + size, area.Y + y + size),
        (area.X + x, area.Y + y + size),
        (area.X + x, area.Y + y + 1),
    ];

    public static async Task<StartRunRequest> StartWalkAsync(
        CancellationToken cancel, ApiFactory api, HttpClient client, bool motionAuthorized = true)
    {
        var start = NewStart(api, startedAgo: TimeSpan.FromMinutes(20)) with { MotionAuthorized = motionAuthorized };
        var response = await client.PostAsJsonAsync("/runs", start, Json, cancel);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return start;
    }

    /// <summary>Прогулка по вершинам: старт забега, куски с датчиками, заявка петли от первой до последней точки.</summary>
    public static async Task<(Guid RunId, Guid CaptureId, long EndMs)> WalkAndClaimAsync(
        CancellationToken cancel,
        ApiFactory api,
        HttpClient client,
        IReadOnlyList<(double X, double Y)> vertices,
        Action<List<TrackPointDto>>? tamper = null,
        bool motionAuthorized = true)
    {
        var start = await StartWalkAsync(cancel, api, client, motionAuthorized);
        var points = WalkPoints(start, vertices);
        tamper?.Invoke(points);
        foreach (var chunk in WalkChunks(api, points))
        {
            var put = await client.PutAsJsonAsync($"/runs/{start.Id}/chunks/{chunk.Points![0].Seq}", chunk, Json, cancel);
            Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        }

        var claim = await client.PostAsJsonAsync(
            $"/runs/{start.Id}/loops",
            new LoopClaimRequest(0, 0, points.Count - 1, LoopClosure.Proximity, 10_000, api.Time.GetUtcNow().ToUnixTimeMilliseconds()),
            Json,
            cancel);
        Assert.Equal(HttpStatusCode.Accepted, claim.StatusCode);
        return (start.Id, (await claim.Content.ReadFromJsonAsync<CaptureResponse>(Json, cancel))!.Id, points[^1].T);
    }

    /// <summary>Прогулка по вершинам: старт забега и все куски — без заявок петель и без завершения.</summary>
    public static async Task<(StartRunRequest Start, List<TrackPointDto> Points)> WalkAsync(
        CancellationToken cancel, ApiFactory api, HttpClient client, IReadOnlyList<(double X, double Y)> vertices)
    {
        var start = await StartWalkAsync(cancel, api, client);
        var points = WalkPoints(start, vertices);
        foreach (var chunk in WalkChunks(api, points))
        {
            var put = await client.PutAsJsonAsync($"/runs/{start.Id}/chunks/{chunk.Points![0].Seq}", chunk, Json, cancel);
            Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        }

        return (start, points);
    }

    /// <summary>Прогулка по вершинам с завершением забега (все куски отправлены) — без заявок петель.</summary>
    public static async Task<StartRunRequest> WalkAndFinishAsync(
        CancellationToken cancel, ApiFactory api, HttpClient client, IReadOnlyList<(double X, double Y)> vertices)
    {
        var (start, points) = await WalkAsync(cancel, api, client, vertices);
        var finish = await client.PostAsJsonAsync(
            $"/runs/{start.Id}/finish",
            new FinishRunRequest(points[^1].T, points.Count - 1, api.Time.GetUtcNow().ToUnixTimeMilliseconds()),
            Json,
            cancel);
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
        return start;
    }

    public static async Task<int> ProcessAsync(ApiFactory api, Guid runId)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CaptureProcessor>().ProcessRunAsync(runId, CancellationToken.None);
    }

}
