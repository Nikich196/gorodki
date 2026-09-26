using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Clans;
using Gorodki.Api.Features.Collection;
using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Duels;
using Gorodki.Api.Features.Fog;
using Gorodki.Api.Features.HallOfFame;
using Gorodki.Api.Features.Inbox;
using Gorodki.Api.Features.Inventory;
using Gorodki.Api.Features.Leaderboards;
using Gorodki.Api.Features.Me;
using Gorodki.Api.Features.Players;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Features.Seasons;
using Gorodki.Api.Features.Segments;
using Gorodki.Api.Features.Social;
using Gorodki.Api.Features.Streaks;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Jobs;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Config;
using Gorodki.Domain.Fog;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Osm;
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
    private static readonly Guid Other = new("6f1e0c2a-94b7-4d38-a51e-2c7b9d4e8f10");
    private static readonly Guid Third = new("b3a7d9e1-5c2f-4e86-9a0d-71f4c8e2b6a3");
    private static readonly Guid Clan = new("0c9d8e7f-6a5b-4c3d-8e2f-1a0b9c8d7e6f");
    private static readonly Guid Segment = new("5e4d3c2b-1a09-4f8e-b7d6-c5b4a3928170");
    private const long Start = 1_790_000_000_000;

    [Fact]
    public async Task Samples_are_exactly_what_the_server_writes()
    {
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Gorodki", "Host=localhost;Database=samples-only");
            builder.UseSetting("Auth:SigningKey", Convert.ToBase64String(new byte[32]));
            builder.UseSetting(CaptureWorker.EnabledSetting, "false");
            builder.UseSetting(ScheduledJobs.EnabledSetting, "false");
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
            Run, League.Run, RunSource.Live, 1, Start, null, RunStatus.Active, null, -1, [new SeqRange(0, 119)], [], Newcomer: true, FogNewCells: null, VisitedParcels: null));
        yield return ("run-finished", new RunResponse(
            Run, League.Bike, RunSource.Replay, 1, Start, Start + 3_600_000, RunStatus.Finished, 199, 179,
            [new SeqRange(0, 59), new SeqRange(120, 199)], [new SeqRange(60, 119)], Newcomer: false, FogNewCells: 1_234, VisitedParcels: null));
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
                ],
                [
                    // Чужая большая петля обвела угол куска 41: зона до «через сутки», вверх до 10 минут.
                    new ContestedZoneView(
                        1_790_086_800_000,
                        [52.0983, 23.689, 52.0983, 23.6895, 52.0985, 23.6895, 52.0985, 23.689, 52.0983, 23.689],
                        []),
                ]),
            ],
            [new TileRef(685, 5775)]));
        yield return ("config", new ConfigResponse(1, 0, GameConfig.Default));
        yield return ("fog", new FogResponse(
            FogLayerKind.Foot,
            0,
            [new FogTileView(9_270, 5_404, 2, 55, FogTileCodec.Compress(SampleFogTile()))],
            [new TileRef(9_271, 5_404)]));
        yield return ("fog-summary", new FogSummaryResponse(
        [
            new FogLayerSummary(FogLayerKind.Foot, null, 3, 1_234, 42_580.5, BrestPercent: 1.37,
            [
                new DistrictPercent("leninsky", "Ленинский район", DistrictKind.District, Proposal: false, 2.41),
                new DistrictPercent("moskovsky", "Московский район", DistrictKind.District, Proposal: false, 0.52),
                new DistrictPercent("arena", "Арена БрГТУ", DistrictKind.Arena, Proposal: true, 18.9),
            ]),
            new FogLayerSummary(FogLayerKind.Foot, 0, 1, 321, 11_074.9, BrestPercent: 0.35, []),
        ],
        OsmSetVersion: 1));
        yield return ("seasons", SeasonEndpoints.ToResponse(
            new SeasonCalendar(
            [
                new Season(0, "Сезон 0 (бета)", SeasonCalendar.MinskMidnight(new DateOnly(2026, 11, 16))),
                new Season(1, "Сезон 1", SeasonCalendar.MinskMidnight(new DateOnly(2026, 11, 30))),
            ]),
            SeasonCalendar.MinskMidnight(new DateOnly(2026, 11, 20))));
        yield return ("session", new SessionResponse("eyJhbGciOiJIUzI1NiJ9.e30.c2lnbmF0dXJl", "cmVmcmVzaA", 900, IsNewUser: true));
        yield return ("me", new MeResponse(Player, "Бегун-1234", 7, "player", PublicProfile: false));
        yield return ("me-stats", new MyStatsResponse(12, 42_195.5, 1_234_567.8, 0, 321_000.4, 7));
        yield return ("player", new PlayerResponse(Player, LeaderboardEndpoints.Pseudonym(Player), 7, IsMe: false));

        // Заготовки задач Егора (C15): образцы — то, что телефон может показывать уже сейчас.
        yield return ("clan", new ClanResponse(
            Clan,
            "Бегуны БрГТУ",
            4,
            Full: true,
            [
                new ClanMemberResponse(Player, "Бегун-1234", 7, ClanRole.Leader, Me: true),
                new ClanMemberResponse(Other, LeaderboardEndpoints.Pseudonym(Other), 2, ClanRole.Officer, Me: false),
                new ClanMemberResponse(Third, "Муха", 11, ClanRole.Member, Me: false),
            ],
            ClanRole.Leader,
            "K7M2-9QXA"));
        yield return ("clan-mine", new MyClanResponse(null, Start + (72 * 3_600_000L)));
        yield return ("clan-hues", new ClanHuesResponse([0, 1, 3, 5, 6, 8, 9, 10]));
        yield return ("leaderboard-territory", new TerritoryLeaderboardResponse(
            "2026-11-20",
            League.Run,
            0,
            Final: false,
            [
                new TerritoryLeaderboardEntry(1, "Муха", 1_240, Me: false),
                new TerritoryLeaderboardEntry(2, LeaderboardEndpoints.Pseudonym(Other), 980, Me: false),
            ],
            new TerritoryLeaderboardEntry(17, "Бегун-1234", 215, Me: true)));
        yield return ("hall-of-fame", new HallOfFameResponse(
        [
            new HallOfFameSeason(0, "Сезон 0 (бета)",
            [
                new HallOfFameEntry(HallOfFameKind.Player, League.Run, 1, Third, null, "Муха", 4_310, Me: false),
                new HallOfFameEntry(HallOfFameKind.Player, League.Run, 2, Player, null, "Бегун-1234", 3_905, Me: true),
                new HallOfFameEntry(HallOfFameKind.Player, League.Run, 3, null, null, null, 3_100, Me: false), // удалил аккаунт
                new HallOfFameEntry(HallOfFameKind.Clan, League.Run, 1, null, Clan, "Бегуны БрГТУ", 11_315, Me: true),
            ]),
        ]));
        yield return ("inbox", new InboxResponse(
            [
                new InboxItem(
                    Guid.Parse("8d2f6a1c-3b7e-4c59-a0d4-e1f2b3c4d5e6"), InboxKind.Attack,
                    "Часть твоей земли взяли — 0,4 га. Освежи её забегом.", Start + 1_500_000, Read: false),
                new InboxItem(
                    Guid.Parse("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d"), InboxKind.Streak,
                    "Серия — 5 дней подряд! Не прерывай её завтра.", Start - 86_400_000, Read: true),
            ],
            "MTc5MDAwMTUwMDAwMDo4ZDJm",
            Unread: 1));
        yield return ("inventory", new InventoryResponse(
            InventoryEndpoints.Slots,
            [
                new InventoryItemResponse(
                    Guid.Parse("3c4d5e6f-7a8b-4c9d-8e0f-1a2b3c4d5e6f"), ItemKind.Radar, Start, Start + (7 * 86_400_000L),
                    Active: true, RemainingMeters: 1_850.5),
                new InventoryItemResponse(
                    Guid.Parse("4d5e6f7a-8b9c-4d0e-9f1a-2b3c4d5e6f7a"), ItemKind.StreakFreeze, Start - 86_400_000,
                    Start + (6 * 86_400_000L), Active: false, RemainingMeters: null),
            ]));
        yield return ("weekly", new WeeklyCardResponse("2026-11-16", 12_400.3, 21_000.7, 1.3, 8_450.2));
        yield return ("streak", new StreakResponse(5, TodayCounted: false, FreezeActive: true));
        yield return ("friends", new FriendsResponse(
            "R4T8-KD2M",
            [
                new FriendResponse(Other, LeaderboardEndpoints.Pseudonym(Other), 2, FriendStatus.Incoming),
                new FriendResponse(Third, "Муха", 11, FriendStatus.Friend),
            ]));
        yield return ("feed", new FeedResponse(
            [
                new FeedPost(
                    Guid.Parse("9e8d7c6b-5a49-4382-a716-f5e4d3c2b1a0"), Third, "Муха", 11, FeedPostKind.Capture, League.Run, "2026-11-20",
                    DistanceMeters: null, CapturedSquareMeters: 4_120, Respects: 3, RespectedByMe: true, Mine: false),
                new FeedPost(
                    Guid.Parse("0f1e2d3c-4b5a-4968-8776-a5b4c3d2e1f0"), Player, "Бегун-1234", 7, FeedPostKind.Run, League.Run, "2026-11-19",
                    DistanceMeters: 5_230, CapturedSquareMeters: null, Respects: 0, RespectedByMe: false, Mine: true),
            ],
            NextCursor: null));
        yield return ("collection", new CollectionResponse(
        [
            new CacheBadgeResponse(
                Guid.Parse("7b6a5948-3726-4150-9e8d-7c6b5a493827"), BadgeRarity.Epic, "Мост над Мухавцом",
                "Там, где река делится надвое, а люди — нет.", Start - 172_800_000, FinderNumber: 1, GoldFrame: true),
            new CacheBadgeResponse(
                Guid.Parse("6a594837-2615-4f0e-8d7c-6b5a49382716"), BadgeRarity.Legendary, null,
                "Ищи там, где часы идут, а стрелки стоят.", null, null, GoldFrame: false),
        ]));
        SegmentEntry[] times = [new(1, "Муха", 71_400, Me: false), new(2, LeaderboardEndpoints.Pseudonym(Other), 74_950, Me: false)];
        yield return ("segments", new SegmentListResponse(
            League.Run,
            [
                new SegmentSummary(Segment, "Набережная Мухавца", 640.5, times[0]),
                new SegmentSummary(Guid.Parse("4d3c2b1a-0918-4e7d-a6c5-b4a392817060"), "Аллея у БрГТУ", 310, null),
            ]));
        yield return ("segment", new SegmentResponse(
            Segment,
            "Набережная Мухавца",
            640.5,
            [52.0826, 23.6534, 52.0831, 23.6582, 52.0839, 23.6621],
            League.Run,
            times[0],
            times[0],
            new SegmentLegend("Бегун-1234", 14, Me: true),
            MyBestTimeMs: 80_120));
        yield return ("segment-leaderboard", new SegmentLeaderboardResponse(
            Segment, League.Run, 0, times, new SegmentEntry(9, "Бегун-1234", 80_120, Me: true)));
        yield return ("duels", new DuelsResponse(
        [
            new DuelResponse(
                Guid.Parse("2b1a0918-7e6d-4c5b-a493-82716f5e4d3c"), DuelStatus.Active, League.Run, DuelMetric.Distance, null, 3,
                new DuelSide(Player, "Бегун-1234", 7, 8_420.5, Staked: true),
                new DuelSide(Third, "Муха", 11, 9_010, Staked: true),
                Start,
                Start + (3 * 86_400_000L),
                WinnerId: null),
            new DuelResponse(
                Guid.Parse("1a09187e-6d5c-4b4a-9382-716f5e4d3c2b"), DuelStatus.Pending, League.Run, DuelMetric.SegmentTime, Segment, 1,
                new DuelSide(Player, "Бегун-1234", 7, null, Staked: false),
                new DuelSide(Other, LeaderboardEndpoints.Pseudonym(Other), 2, null, Staked: false),
                StartsAtMs: null,
                EndsAtMs: null,
                WinnerId: null),
        ]));
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
