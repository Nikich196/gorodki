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

    /// <summary>Центр тестовых прогулок — Брест, UTM 34N; середина тайла, чтобы петли не задевали соседние.</summary>
    public static readonly (double X, double Y) WalkOrigin = (684_500, 5_775_500);

    /// <summary>
    /// Прогулка по вершинам (метры от <see cref="WalkOrigin"/>) со скоростью <paramref name="speed"/>, точка раз в секунду
    /// с начала забега. Номера — с <paramref name="firstSeq"/>, время — продолжает с <paramref name="firstSecond"/>.
    /// </summary>
    public static List<TrackPointDto> WalkPoints(
        StartRunRequest start, IReadOnlyList<(double X, double Y)> vertices, double speed = 1.4, int firstSeq = 0, int firstSecond = 1)
    {
        var positions = new List<(double X, double Y)>();
        for (var i = 0; i + 1 < vertices.Count; i++)
        {
            var (x1, y1) = vertices[i];
            var (x2, y2) = vertices[i + 1];
            var length = Math.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));
            var steps = Math.Max(1, (int)Math.Round(length / speed));
            for (var s = 0; s < steps; s++)
            {
                positions.Add((x1 + ((x2 - x1) * s / steps), y1 + ((y2 - y1) * s / steps)));
            }
        }

        positions.Add(vertices[^1]);
        return [.. positions.Select((p, i) =>
        {
            var (latitude, longitude) = Gorodki.Domain.Geo.Utm34.Inverse(WalkOrigin.X + p.X, WalkOrigin.Y + p.Y);
            return new TrackPointDto(firstSeq + i, start.StartedAtMs + ((firstSecond + i) * 1_000L), latitude, longitude, 5, speed, 0);
        })];
    }

    /// <summary>
    /// Куски по <paramref name="size"/> точек с «Движением» (ходьба) и шагомером: шаг ~0,78 м, записи по 5 с.
    /// Отметка полноты датчиков — время последней точки куска.
    /// </summary>
    public static List<UploadChunkRequest> WalkChunks(ApiFactory api, IReadOnlyList<TrackPointDto> points, double speed = 1.4, int size = 120)
    {
        var stepsPerFiveSeconds = (int)Math.Round(5 * speed / 0.78);
        var chunks = new List<UploadChunkRequest>();
        for (var from = 0; from < points.Count; from += size)
        {
            var part = points.Skip(from).Take(size).ToArray();
            var steps = new List<StepSampleDto>();
            for (var t = part[0].T; t + 5_000 <= part[^1].T; t += 5_000)
            {
                steps.Add(new StepSampleDto(t, t + 5_000, stepsPerFiveSeconds));
            }

            chunks.Add(new UploadChunkRequest(
                api.Time.GetUtcNow().ToUnixTimeMilliseconds(),
                part[^1].T,
                part,
                [new MotionSampleDto(part[0].T, MotionActivity.Walking)],
                steps));
        }

        return chunks;
    }
}
