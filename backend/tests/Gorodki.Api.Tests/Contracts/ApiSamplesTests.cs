using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Fog;
using Gorodki.Api.Features.Me;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Features.Seasons;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Config;
using Gorodki.Domain.Fog;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Runs;
using Gorodki.Domain.Time;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Gorodki.Api.Tests.Contracts;

/// <summary>
/// «Пыточный набор» спайка S6: ответы, записанные самим сервером (его настройки JSON — строки-перечисления, числа только числами),
/// в <c>contracts/samples</c>. Swift-клиент (<c>ios/Packages/GorodkiAPI</c>) обязан разобрать каждый образец. Обновить:
/// <c>GORODKI_UPDATE_CONTRACTS=1 dotnet test</c>.
/// </summary>
public sealed class ApiSamplesTests
{
    private static readonly Guid Player = new("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");
    private static readonly Guid Run = new("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5c");
    private const long Start = 1_790_000_000_000;

    [Fact]
    public async Task Samples_are_exactly_what_the_server_writes()
    {
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Gorodki", "Host=localhost;Database=samples-only");
            builder.UseSetting("Auth:SigningKey", Convert.ToBase64String(new byte[32]));
            builder.UseSetting(CaptureWorker.EnabledSetting, "false");
        });
        var server = app.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        var pretty = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var directory = Path.Combine(RepositoryRoot(), "contracts", "samples");

        foreach (var (name, sample) in Samples())
        {
            var node = JsonNode.Parse(JsonSerializer.Serialize(sample, sample.GetType(), server))!;
            var path = Path.Combine(directory, $"{name}.json");
            if (Environment.GetEnvironmentVariable("GORODKI_UPDATE_CONTRACTS") == "1")
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(path, node.ToJsonString(pretty).ReplaceLineEndings("\n") + "\n");
            }

            Assert.True(
                JsonNode.DeepEquals(node, JsonNode.Parse(File.ReadAllText(path))),
                $"Сервер пишет {name} иначе, чем в contracts/samples. Обновить: GORODKI_UPDATE_CONTRACTS=1 dotnet test.");
        }
    }

    private static IEnumerable<(string Name, object Sample)> Samples()
    {
        yield return ("run-active", new RunResponse(
            Run, League.Run, RunSource.Live, 1, Start, null, RunStatus.Active, null, -1, [new SeqRange(0, 119)], [], Newcomer: true, FogNewCells: null));
        yield return ("run-finished", new RunResponse(
            Run, League.Bike, RunSource.Replay, 1, Start, Start + 3_600_000, RunStatus.Finished, 199, 179,
            [new SeqRange(0, 59), new SeqRange(120, 199)], [new SeqRange(60, 119)], Newcomer: false, FogNewCells: 1_234));
        yield return ("chunk-receipt", new ChunkReceipt(0, 119, Duplicate: true));
        yield return ("capture-pending", new CaptureResponse(
            Guid.Parse("2e19b697-e397-532d-a6e6-7c0fc1940226"), 0, 10, 300, CaptureStatus.Pending, "sensors", null, 0, null, null, null));
        yield return ("capture-applied", new CaptureResponse(
            Guid.Parse("624661fe-a660-59b4-be57-f29da86d1ebc"), 1, 300, 612, CaptureStatus.Applied, null, null, 10_004.3,
            new Dictionary<string, double> { ["claimedNeutral"] = 5_002.1, ["transferred"] = 5_002.2, ["cracked"] = 1_200 },
            [new TileRef(684, 5775), new TileRef(685, 5775)], Start + 612_000));
        yield return ("capture-rejected", new CaptureResponse(
            Guid.Parse("2cba2ab6-1fb3-5f35-b170-727e301a521c"), 2, 700, 900, CaptureStatus.Rejected, null, "segment_broken:vehicle",
            0, null, null, Start + 900_000));
        yield return ("territory", new TerritoryResponse(
            League.Run,
            [
                new TileTerritory(684, 5775, 3,
                [
                    new ParcelView(
                        41, Player, 7, 2, Ghost: false, Start, Start + 43_200_000, null,
                        [52.0976, 23.688, 52.0976, 23.6895, 52.0985, 23.6895, 52.0985, 23.688, 52.0976, 23.688],
                        [[52.0979, 23.6884, 52.0979, 23.6888, 52.0982, 23.6888, 52.0979, 23.6884]]),
                    new ParcelView(
                        42, Player, 7, 0, Ghost: true, Start - 700_000_000, null, null,
                        [52.099, 23.688, 52.099, 23.689, 52.0995, 23.689, 52.099, 23.688], []),
                ]),
            ],
            [new TileRef(685, 5775)]));
        yield return ("config", new ConfigResponse(1, 0, GameConfig.Default));
        yield return ("fog", new FogResponse(
            FogLayerKind.Foot,
            [new FogTileView(9_270, 5_404, 2, 55, FogTileCodec.Compress(SampleFogTile()))],
            [new TileRef(9_271, 5_404)]));
        yield return ("fog-summary", new FogSummaryResponse([new FogLayerSummary(FogLayerKind.Foot, 3, 1_234, 42_580.5)]));
        yield return ("seasons", SeasonEndpoints.ToResponse(
            new SeasonCalendar(
            [
                new Season(0, "Сезон 0 (бета)", SeasonCalendar.MinskMidnight(new DateOnly(2026, 11, 16))),
                new Season(1, "Сезон 1", SeasonCalendar.MinskMidnight(new DateOnly(2026, 11, 30))),
            ]),
            SeasonCalendar.MinskMidnight(new DateOnly(2026, 11, 20))));
        yield return ("session", new SessionResponse("eyJhbGciOiJIUzI1NiJ9.e30.c2lnbmF0dXJl", "cmVmcmVzaA", 900, IsNewUser: true));
        yield return ("me", new MeResponse(Player, "Бегун-1234", 7, "player", PublicProfile: false));
        yield return ("problem-chunk-invalid", Problem(400, "chunk_invalid", "Кусок забега не прошёл проверку.", "problems",
            new[] { new ChunkProblem("points[37]", "lat_range"), new ChunkProblem("points", "seq_limit") }));
        yield return ("problem-chunk-conflict", Problem(409, "chunk_conflict", "Эти номера точек уже заняты другим куском.", "overlaps",
            new[] { new SeqRange(0, 59) }));
        yield return ("problem-run-not-found", Problem(404, "run_not_found", "Забег не найден.", null, null));
    }

    /// <summary>Круг 25 м в центре Бреста — 55 клеток.</summary>
    private static FogTileBits SampleFogTile()
    {
        var layer = new FogLayer();
        layer.RevealAround(52.0976, 23.688, 25);
        return layer.Tiles.Values.Single();
    }

    private static ProblemDetails Problem(int status, string code, string title, string? extra, object? extraValue)
    {
        var problem = new ProblemDetails { Status = status, Title = title };
        problem.Extensions["code"] = code;
        if (extra is not null)
        {
            problem.Extensions[extra] = extraValue;
        }

        return problem;
    }

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
