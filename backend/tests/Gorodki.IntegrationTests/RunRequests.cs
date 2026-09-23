using System.Text.Json;
using System.Text.Json.Serialization;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Runs;

namespace Gorodki.IntegrationTests;

/// <summary>Запросы забегов для тестов: старт и куски точек, как их шлёт телефон.</summary>
internal static class RunRequests
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static StartRunRequest NewStart(ApiFactory api, TimeSpan? startedAgo = null, long clockSkewMs = 0, RunSource source = RunSource.Live)
    {
        var phoneNow = api.Time.GetUtcNow().ToUnixTimeMilliseconds() + clockSkewMs;
        return new StartRunRequest(
            Guid.CreateVersion7(),
            League.Run,
            source,
            ConfigVersion: 1,
            StartedAtMs: phoneNow - (long)(startedAgo ?? TimeSpan.FromMinutes(1)).TotalMilliseconds,
            SentAtMs: phoneNow,
            DeviceId: Guid.NewGuid(),
            AppVersion: "0.1.0 (1)",
            MotionAuthorized: true);
    }

    /// <summary>Кусок: точки раз в секунду от начала забега, по прямой на север от центра Бреста.</summary>
    public static UploadChunkRequest ChunkRequest(
        ApiFactory api, StartRunRequest start, int firstSeq, int count, double latitude = 52.0976, long clockSkewMs = 0)
    {
        var points = Enumerable.Range(firstSeq, count)
            .Select(seq => new TrackPointDto(seq, start.StartedAtMs + 1_000 + (seq * 1_000L), latitude + (seq * 0.00002), 23.688, 5, 2.8, 0))
            .ToArray();
        return new UploadChunkRequest(
            SentAtMs: api.Time.GetUtcNow().ToUnixTimeMilliseconds() + clockSkewMs,
            SensorsCompleteThroughMs: points[^1].T,
            points,
            [new MotionSampleDto(points[0].T, MotionActivity.Running)],
            [new StepSampleDto(points[0].T, points[^1].T, count * 2)]);
    }

}
