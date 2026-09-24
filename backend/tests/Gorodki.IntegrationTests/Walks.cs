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

    /// <summary>Прямоугольник с углом (x; y) — как <see cref="Square"/>: прогулка кончается в метре от старта.</summary>
    public static (double X, double Y)[] Rectangle((double X, double Y) area, double x, double y, double width, double height) =>
    [
        (area.X + x, area.Y + y),
        (area.X + x + width, area.Y + y),
        (area.X + x + width, area.Y + y + height),
        (area.X + x, area.Y + y + height),
        (area.X + x, area.Y + y + 1),
    ];

    /// <summary>
    /// Многоугольник по вершинам (метры от угла места теста) — например, с наклонной стороной. Как у <see cref="Square"/>,
    /// прогулка кончается в метре от старта — по последней стороне.
    /// </summary>
    public static (double X, double Y)[] Polygon((double X, double Y) area, params (double X, double Y)[] vertices)
    {
        var (startX, startY) = vertices[0];
        var (lastX, lastY) = vertices[^1];
        var length = Math.Sqrt(((lastX - startX) * (lastX - startX)) + ((lastY - startY) * (lastY - startY)));
        return
        [
            .. vertices.Select(v => (area.X + v.X, area.Y + v.Y)),
            (area.X + startX + ((lastX - startX) / length), area.Y + startY + ((lastY - startY) / length)),
        ];
    }

    /// <param name="startedAgo">Когда начат забег (по умолчанию 20 минут назад); давно — забег из офлайна, отправленный сейчас.</param>
    public static async Task<StartRunRequest> StartWalkAsync(
        CancellationToken cancel,
        ApiFactory api,
        HttpClient client,
        bool motionAuthorized = true,
        Guid? deviceId = null,
        TimeSpan? startedAgo = null)
    {
        var start = NewStart(api, startedAgo: startedAgo ?? TimeSpan.FromMinutes(20)) with { MotionAuthorized = motionAuthorized };
        start = deviceId is { } device ? start with { DeviceId = device } : start;
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
        bool motionAuthorized = true,
        Guid? deviceId = null)
    {
        var start = await StartWalkAsync(cancel, api, client, motionAuthorized, deviceId);
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
        CancellationToken cancel, ApiFactory api, HttpClient client, IReadOnlyList<(double X, double Y)> vertices, TimeSpan? startedAgo = null)
    {
        var start = await StartWalkAsync(cancel, api, client, startedAgo: startedAgo);
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
        CancellationToken cancel, ApiFactory api, HttpClient client, IReadOnlyList<(double X, double Y)> vertices, TimeSpan? startedAgo = null)
    {
        var (start, points) = await WalkAsync(cancel, api, client, vertices, startedAgo);
        await FinishAsync(cancel, api, client, start, points);
        return start;
    }

    /// <summary>
    /// Петля, а за ней ещё путь по <paramref name="then"/>: заявка — только на петлю, забег завершён. Начат давно
    /// (<paramref name="startedAgo"/>) — забег из офлайна: его конец уже публичен, и визиты засчитываются сразу, а захват
    /// применяется сейчас и ещё 20 минут скрыт.
    /// </summary>
    public static async Task<(Guid RunId, Guid CaptureId)> WalkLoopAndOnAsync(
        CancellationToken cancel,
        ApiFactory api,
        HttpClient client,
        IReadOnlyList<(double X, double Y)> loop,
        IReadOnlyList<(double X, double Y)> then,
        TimeSpan startedAgo)
    {
        var (start, points) = await WalkAsync(cancel, api, client, [.. loop, .. then], startedAgo);
        var loopEnd = WalkPoints(start, loop).Count - 1; // точки петли — начало точек всего пути
        var claim = await client.PostAsJsonAsync(
            $"/runs/{start.Id}/loops",
            new LoopClaimRequest(0, 0, loopEnd, LoopClosure.Proximity, 10_000, api.Time.GetUtcNow().ToUnixTimeMilliseconds()),
            Json,
            cancel);
        Assert.Equal(HttpStatusCode.Accepted, claim.StatusCode);
        await FinishAsync(cancel, api, client, start, points);
        return (start.Id, (await claim.Content.ReadFromJsonAsync<CaptureResponse>(Json, cancel))!.Id);
    }

    private static async Task FinishAsync(
        CancellationToken cancel, ApiFactory api, HttpClient client, StartRunRequest start, List<TrackPointDto> points)
    {
        var finish = await client.PostAsJsonAsync(
            $"/runs/{start.Id}/finish",
            new FinishRunRequest(points[^1].T, points.Count - 1, api.Time.GetUtcNow().ToUnixTimeMilliseconds()),
            Json,
            cancel);
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
    }

    /// <summary>Визиты завершённого забега (null — ещё рано или уже посчитаны).</summary>
    public static async Task<int?> VisitAsync(ApiFactory api, Guid runId)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<VisitProcessor>().ProcessRunAsync(runId, CancellationToken.None);
    }

    public static async Task<int> ProcessAsync(ApiFactory api, Guid runId)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CaptureProcessor>().ProcessRunAsync(runId, CancellationToken.None);
    }

}
