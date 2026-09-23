using System.Text.Json;
using Gorodki.Domain.Config;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Runs;

namespace Gorodki.Domain.Tests.Runs;

/// <summary>
/// Судья на сервере против эталонов <c>contracts/segment-judge.v1.json</c>, которые записала реализация на Swift
/// (<c>JudgeContractTests</c> в GameCore): вердикт каждой точки должен совпасть.
/// </summary>
public sealed class SegmentJudgeContractTests
{
    private static readonly Contract Vectors = JsonSerializer.Deserialize<Contract>(
        File.ReadAllText(Path.Combine(RepositoryRoot(), "contracts", "segment-judge.v1.json")),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    public static TheoryData<string> Scenarios => [.. Vectors.Scenarios.Select(s => s.Name)];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Server_judge_gives_the_same_verdicts_as_the_phone(string name)
    {
        var scenario = Vectors.Scenarios.Single(s => s.Name == name);
        var league = scenario.League == "bike" ? League.Bike : League.Run;
        var judge = new SegmentJudge(GameConfig.Default.Leagues.For(league));

        for (var index = 0; index < scenario.Events.Count; index++)
        {
            var e = scenario.Events[index];
            if (e.Motion is { } motion)
            {
                judge.Record(new MotionSample(motion.T, Enum.Parse<MotionActivity>(motion.Activity, ignoreCase: true)));
            }

            if (e.Steps is { } steps)
            {
                judge.Record(new StepSample(steps.Start, steps.End, steps.Steps));
            }

            if (e.Point is { } p)
            {
                var point = TrackPoint.FromMeasurements(index, p.T, p.Lat, p.Lon, p.Acc, p.Speed, PointFlags.None);
                var verdict = judge.Judge(point, now: p.T / 1000.0).ToString();
                Assert.True(e.Expect == verdict, $"«{name}», событие {index}: ожидали {e.Expect}, сервер решил {verdict}");
            }
        }
    }

    [Fact]
    public void Vectors_cover_every_verdict_the_server_can_see()
    {
        var seen = Vectors.Scenarios.SelectMany(s => s.Events).Select(e => e.Expect).OfType<string>().ToHashSet();

        Assert.Superset(
            new HashSet<string>
            {
                "accepted", "ignored:poorAccuracy", "broken:teleport", "broken:tooFast", "broken:vehicle", "broken:cycling",
                "broken:noSteps", "broken:strideOutOfRange", "broken:carLaunch",
            },
            seen);
    }

    [Fact]
    public void One_degree_of_meridian_matches_the_phone()
    {
        // То же число проверяет GeodesyTests в GameCore.
        Assert.Equal(111_195.08, Geodesy.Distance(52, 23, 53, 23), precision: 2);
    }

    private sealed record Contract(int Version, List<Scenario> Scenarios);

    private sealed record Scenario(string Name, string League, List<Event> Events);

    private sealed record Event(PointEvent? Point, MotionEvent? Motion, StepsEvent? Steps, string? Expect);

    private sealed record PointEvent(long T, double Lat, double Lon, double Acc, double? Speed);

    private sealed record MotionEvent(long T, string Activity);

    private sealed record StepsEvent(long Start, long End, int? Steps);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "backend")) && Directory.Exists(Path.Combine(directory.FullName, "docs")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Не найден корень репозитория (папки backend и docs).");
    }
}
