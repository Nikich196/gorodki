using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Territory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Обработка захватов целиком на настоящей базе: прогулка → куски с датчиками → заявка → судья, контур, земля в базе.
/// Каждый тест гуляет в своём месте (свой тайл), чтобы тесты не делили землю.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CaptureProcessingTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Walked_block_becomes_the_players_land()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, userId) = await api.CreatePlayerClientAsync();
        var area = NewArea();

        var claim = await WalkAndClaimAsync(Cancel, api, client, Square(area, 0, 0, 100));
        var decided = await ProcessAsync(api, claim.RunId);

        var capture = await CaptureAsync(client, claim);
        Assert.Equal(1, decided);
        Assert.Equal(CaptureStatus.Applied, capture.Status);
        Assert.InRange(capture.AreaSquareMeters, 9_500, 10_500);
        Assert.Null(capture.AreaByOutcome); // разбивка — только после границы публичности, даже по одной ничьей земле
        Assert.InRange(await LandAreaAsync(userId), 9_500, 10_500);
        Assert.NotEmpty(capture.ChangedTiles!);
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        Assert.InRange((await CaptureAsync(client, claim)).AreaByOutcome!["claimedNeutral"], 9_500, 10_500);
        await using var db = database.CreateContext();
        var tile = capture.ChangedTiles![0];
        Assert.True(await db.TileVersions.AnyAsync(t => t.TileX == tile.X && t.TileY == tile.Y && t.Version >= 1, Cancel));
    }

    [Fact]
    public async Task Second_player_takes_level_one_land_and_the_map_stays_consistent()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var area = NewArea();

        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromMinutes(30));
        var borisClaim = await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100));
        await ProcessAsync(api, borisClaim.RunId);

        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep); // разбивка — после границы публичности
        var capture = await CaptureAsync(boris, borisClaim);
        Assert.Equal(CaptureStatus.Applied, capture.Status);
        Assert.InRange(capture.AreaByOutcome!["transferred"], 4_700, 5_300);
        Assert.InRange(await LandAreaAsync(annaId), 4_700, 5_300);
        Assert.InRange(await LandAreaAsync(borisId), 9_500, 10_500);
        Assert.Empty(TerritoryInvariants.Check(await MapOfAsync()));
    }

    [Fact]
    public async Task Breakdown_by_outcome_waits_for_the_public_boundary_and_gives_away_no_hidden_capture()
    {
        // docs/architecture/run-hud.md, «Приватность итога заявки». Борис только что взял ничью землю: захват скрыт, у Анны
        // на карте там ничья. Анна обегает это место. Сервер решает по настоящей земле, и разбивка («transferred» вместо
        // «claimedNeutral») выдала бы ей скрытый захват. Поэтому до границы публичности разбивки нет — ни в списке заявок,
        // ни в ответе на повтор заявки; «взятое» — сразу, это ровно новая земля Анны на её карте.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromMinutes(5)); // петля Анны кончается позже, чем у Бориса, — иначе «superseded»

        var claim = await WalkAndClaimAsync(Cancel, api, anna, Square(area, 50, 0, 100));
        await ProcessAsync(api, claim.RunId);

        var listed = await CaptureAsync(anna, claim);
        Assert.Equal(CaptureStatus.Applied, listed.Status);
        Assert.Null(listed.AreaByOutcome);
        Assert.Null((await RepeatClaimAsync(anna, claim.RunId, listed)).AreaByOutcome);
        Assert.InRange(listed.AreaSquareMeters, 9_500, 10_500);
        Assert.True(Math.Abs(listed.AreaSquareMeters - await LandAreaAsync(annaId)) < 1, "«Взятое» — не новая земля Анны");

        // Граница — момент применения захвата Анны: за миллисекунду до неё разбивки ещё нет, на ней — есть. Захват Бориса
        // применён раньше, поэтому к этому моменту он уже публичен.
        DateTimeOffset appliedAt;
        await using (var db = database.CreateContext())
        {
            appliedAt = (await db.Captures.AsNoTracking().SingleAsync(c => c.Id == claim.CaptureId, Cancel)).AppliedAt!.Value;
        }

        var publicAt = TerritoryReader.PublicAt(appliedAt, TerritoryReader.PublicDelay);
        api.Time.Advance(publicAt - api.Time.GetUtcNow() - TimeSpan.FromMilliseconds(1));
        Assert.Null((await CaptureAsync(anna, claim)).AreaByOutcome);

        api.Time.Advance(TimeSpan.FromMilliseconds(1));
        var revealed = (await CaptureAsync(anna, claim)).AreaByOutcome!;
        Assert.InRange(revealed["transferred"], 4_700, 5_300);
        Assert.InRange(revealed["claimedNeutral"], 4_700, 5_300);
        Assert.Equal(revealed, (await RepeatClaimAsync(anna, claim.RunId, listed)).AreaByOutcome!);
    }

    [Fact]
    public async Task Two_players_capturing_the_same_block_at_once_leave_a_consistent_map()
    {
        // PLAN.md, этап 2: «тест одновременных захватов». Петли пересекаются в одном тайле и обрабатываются параллельно:
        // блокировки тайлов ставят их в очередь, обе применяются, наложений нет.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var annaClaim = await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100));
        var borisClaim = await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 50, 100));

        var decided = await Task.WhenAll(ProcessAsync(api, annaClaim.RunId), ProcessAsync(api, borisClaim.RunId));

        Assert.Equal(new[] { 1, 1 }, decided);
        Assert.Equal(CaptureStatus.Applied, (await CaptureAsync(anna, annaClaim)).Status);
        Assert.Equal(CaptureStatus.Applied, (await CaptureAsync(boris, borisClaim)).Status);
        Assert.Empty(TerritoryInvariants.Check(await MapOfAsync()));

        // Вместе — объединение двух квадратов (2 × 10 000 − 2 500); пересечение досталось тому, кто применился вторым.
        var total = await LandAreaAsync(annaId) + await LandAreaAsync(borisId);
        Assert.InRange(total, 17_000, 18_000);
    }

    [Fact]
    public async Task Processing_again_or_in_parallel_applies_the_loop_once()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, userId) = await api.CreatePlayerClientAsync();
        var claim = await WalkAndClaimAsync(Cancel, api, client, Square(NewArea(), 0, 0, 100));

        var parallel = await Task.WhenAll(ProcessAsync(api, claim.RunId), ProcessAsync(api, claim.RunId), ProcessAsync(api, claim.RunId));
        var again = await ProcessAsync(api, claim.RunId);

        Assert.Equal(1, parallel.Sum());
        Assert.Equal(0, again);
        Assert.InRange(await LandAreaAsync(userId), 9_500, 10_500);
    }

    [Fact]
    public async Task Teleport_inside_the_loop_rejects_it_with_the_reason()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, userId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var square = Square(area, 0, 0, 100);

        // На середине пути — скачок на 600 м и обратно (выброс GPS или подмена): след рвётся.
        var claim = await WalkAndClaimAsync(Cancel, api, client, square, points =>
        {
            var middle = points.Count / 2;
            var (latitude, longitude) = Utm34.Inverse(WalkOrigin.X + area.X + 600, WalkOrigin.Y + area.Y + 600);
            points[middle] = points[middle] with { Lat = latitude, Lon = longitude };
        });
        await ProcessAsync(api, claim.RunId);

        var capture = await CaptureAsync(client, claim);
        Assert.Equal((CaptureStatus.Rejected, "segment_broken:teleport"), (capture.Status, capture.RejectCode));
        Assert.Equal(0, await LandAreaAsync(userId));
    }

    [Fact]
    public async Task Without_motion_permission_nothing_is_captured()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, userId) = await api.CreatePlayerClientAsync();

        var claim = await WalkAndClaimAsync(Cancel, api, client, Square(NewArea(), 0, 0, 100), motionAuthorized: false);
        await ProcessAsync(api, claim.RunId);

        Assert.Equal("motion_not_authorized", (await CaptureAsync(client, claim)).RejectCode);
        Assert.Equal(0, await LandAreaAsync(userId));
    }

    [Fact]
    public async Task Claim_whose_points_never_arrive_goes_stale_after_3_hours()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();
        var start = await StartWalkAsync(Cancel, api, client);
        var response = await client.PostAsJsonAsync(
            $"/runs/{start.Id}/loops", new LoopClaimRequest(0, 0, 300, LoopClosure.Proximity, 10_000, start.StartedAtMs), Json, Cancel);
        var captureId = (await response.Content.ReadFromJsonAsync<CaptureResponse>(Json, Cancel))!.Id;

        var early = await ProcessAsync(api, start.Id);
        api.Time.Advance(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(1));
        var late = await ProcessAsync(api, start.Id);

        // Итог — из базы: после сдвига часов на 3 часа токен клиента для HTTP уже мог бы истечь.
        Assert.Equal((0, 1), (early, late));
        await using var db = database.CreateContext();
        var capture = await db.Captures.SingleAsync(c => c.Id == captureId, Cancel);
        Assert.Equal((CaptureStatus.Stale, "points_not_received"), (capture.Status, capture.RejectCode));
    }

    [Fact]
    public async Task Thirty_captures_a_day_is_the_limit()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, userId) = await api.CreatePlayerClientAsync();
        var claim = await WalkAndClaimAsync(Cancel, api, client, Square(NewArea(), 0, 0, 100));
        await using (var db = database.CreateContext())
        {
            // 30 уже применённых захватов за эти игровые сутки (без земли — для лимита важен только счёт).
            for (var i = 0; i < 30; i++)
            {
                db.Captures.Add(new CaptureEntity
                {
                    Id = Guid.NewGuid(),
                    RunId = claim.RunId,
                    UserId = userId,
                    ClaimNo = 50 + i,
                    StartSeq = 0,
                    EndSeq = 10 + i,
                    Status = CaptureStatus.Applied,
                    ReceivedAt = api.Time.GetUtcNow(),
                    EffectiveAt = DateTimeOffset.FromUnixTimeMilliseconds(claim.EndMs), // те же игровые сутки при любом времени запуска
                });
            }

            await db.SaveChangesAsync(Cancel);
        }

        await ProcessAsync(api, claim.RunId);

        Assert.Equal("daily_limit", (await CaptureAsync(client, claim)).RejectCode);
    }

    // MARK: — вспомогательное

    private async Task<CaptureResponse> CaptureAsync(HttpClient client, (Guid RunId, Guid CaptureId, long EndMs) claim)
    {
        var captures = await client.GetFromJsonAsync<List<CaptureResponse>>($"/runs/{claim.RunId}/captures", Json, Cancel);
        return captures!.Single(c => c.Id == claim.CaptureId);
    }

    /// <summary>Та же заявка ещё раз (ответ мог потеряться): сервер отвечает 200 с её текущим итогом.</summary>
    private async Task<CaptureResponse> RepeatClaimAsync(HttpClient client, Guid runId, CaptureResponse capture)
    {
        var response = await client.PostAsJsonAsync(
            $"/runs/{runId}/loops",
            new LoopClaimRequest(capture.ClaimNo, capture.StartSeq, capture.EndSeq, LoopClosure.Proximity, 10_000, 0),
            Json,
            Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CaptureResponse>(Json, Cancel))!;
    }

    private async Task<double> LandAreaAsync(Guid userId)
    {
        await using var db = database.CreateContext();
        var parcels = await db.Parcels.Where(p => p.OwnerId == userId).Select(p => p.Geometry).ToListAsync(Cancel);
        return parcels.Sum(g => g.Area);
    }

    /// <summary>Вся карта «Бега» из базы — для проверки инвариантов (нет наложений, правильная геометрия, нет осколков).</summary>
    private async Task<TerritoryMap> MapOfAsync()
    {
        await using var db = database.CreateContext();
        var parcels = await db.Parcels.Where(p => p.League == Gorodki.Domain.Leagues.League.Run).ToListAsync(Cancel);
        var map = new TerritoryMap();
        map.Load(parcels.Select(p => new Parcel(
            new TileKey(p.TileX, p.TileY),
            p.Geometry,
            new ParcelState
            {
                OwnerId = p.OwnerId,
                Level = p.Level,
                LastVisitAt = p.LastVisitAt,
                LastLevelUpAt = p.LastLevelUpAt,
                ShieldUntil = p.ShieldUntil,
                SiegeUntil = p.SiegeUntil,
                LossWindowSince = p.LossWindowSince,
                LossAttackers = AttackerSet.Of(p.LossAttackers),
                TouchedAt = p.TouchedAt,
            })));
        return map;
    }
}
