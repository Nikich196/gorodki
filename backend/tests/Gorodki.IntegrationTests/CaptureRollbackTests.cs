using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Admin;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Territory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Откат захватов нарушителя на настоящей базе (PLAN.md, §3.9, слой 5): заморозка, возврат земли жертвам, только
/// нетронутое, пересчёт, если тайлы изменились между расчётом и записью, доступ только администратору.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CaptureRollbackTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Rollback_returns_the_victims_land_and_freezes_the_cheater()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var scene = await AnnaThenBorisAsync(api);
        var (annaId, boris, borisId, borisClaim, area, admin) = (scene.AnnaId, scene.Boris, scene.BorisId, scene.BorisClaim, scene.Area, scene.Admin);

        var requested = await RequestAsync(admin, borisId);
        Assert.Equal(CaptureRollbackStatus.Pending, requested.Status);
        Assert.Equal(api.Time.GetUtcNow() + CaptureRollback.FreezeFor, requested.FrozenUntil);
        Assert.Equal(requested.Id, (await RequestAsync(admin, borisId, HttpStatusCode.OK)).Id); // повтор — то же задание
        await ProcessAsync(api, requested.Id);

        var done = await admin.GetFromJsonAsync<RollbackResponse>($"/admin/rollbacks/{requested.Id}", Json, Cancel);
        Assert.Equal(CaptureRollbackStatus.Done, done!.Status);
        Assert.Equal(1, done.RolledBack);
        Assert.InRange(done.RestoredArea, 9_500, 10_500); // половина вернулась Анне, половина снова ничья
        Assert.InRange(done.SkippedArea, 0, 50);
        Assert.InRange(await LandAreaAsync(annaId), 9_500, 10_500);
        Assert.Equal(0, await LandAreaAsync(borisId), 1);
        Assert.Empty(TerritoryInvariants.Check(await MapOfAsync(area)));

        // Для суточного лимита и для приложения захват по-прежнему «применён» — откат виден только в базе.
        await using (var db = database.CreateContext())
        {
            var capture = await db.Captures.AsNoTracking().SingleAsync(c => c.Id == borisClaim.CaptureId, Cancel);
            Assert.Equal(CaptureStatus.Applied, capture.Status);
            Assert.NotNull(capture.RolledBackAt);
            Assert.Equal(requested.Id, capture.RollbackId);
        }

        var listed = await boris.GetFromJsonAsync<List<CaptureResponse>>($"/runs/{borisClaim.RunId}/captures", Json, Cancel);
        Assert.Equal(CaptureStatus.Applied, listed!.Single().Status);

        // Заморожен: новый захват Бориса на карту не ложится.
        var next = await WalkAndClaimAsync(Cancel, api, boris, Square(NewArea(), 0, 0, 100));
        await Walks.ProcessAsync(api, next.RunId);
        await using (var db = database.CreateContext())
        {
            var capture = await db.Captures.AsNoTracking().SingleAsync(c => c.Id == next.CaptureId, Cancel);
            Assert.Equal(CaptureStatus.Rejected, capture.Status);
            Assert.Equal("account_frozen", capture.RejectCode);
        }

        Assert.Equal(0, await LandAreaAsync(borisId), 1);
    }

    [Fact]
    public async Task Land_changed_after_the_capture_stays_as_it_is()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var scene = await AnnaThenBorisAsync(api);
        var (annaId, borisId, vera, veraId, area, admin) = (scene.AnnaId, scene.BorisId, scene.Vera, scene.VeraId, scene.Area, scene.Admin);
        api.Time.Advance(TimeSpan.FromHours(13)); // щит после перехода — 12 часов
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, vera, Square(area, 90, 20, 60))).RunId);
        var veraBefore = await LandAreaAsync(veraId);
        Assert.InRange(veraBefore, 3_300, 3_900);

        var done = await RollBackAsync(api, admin, borisId);

        Assert.Equal(1, done.RolledBack);
        Assert.InRange(done.SkippedArea, veraBefore - 300, veraBefore + 300);
        Assert.Equal(veraBefore, await LandAreaAsync(veraId), 1); // земля Веры не тронута
        Assert.InRange(await LandAreaAsync(annaId), 9_100, 9_700); // Анне вернулось всё, кроме взятого Верой
        Assert.Equal(0, await LandAreaAsync(borisId), 1);
        Assert.Empty(TerritoryInvariants.Check(await MapOfAsync(area)));
    }

    [Fact]
    public async Task Tiles_changed_between_calculation_and_write_are_recalculated()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var scene = await AnnaThenBorisAsync(api);
        var (annaId, borisId, vera, veraId, area, admin) = (scene.AnnaId, scene.BorisId, scene.Vera, scene.VeraId, scene.Area, scene.Admin);
        api.Time.Advance(TimeSpan.FromHours(13));
        var veraClaim = await WalkAndClaimAsync(Cancel, api, vera, Square(area, 90, 20, 60));
        var requested = await RequestAsync(admin, borisId);

        // Захват Веры применяется ровно между расчётом отката и его записью.
        var interfered = false;
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var rollback = scope.ServiceProvider.GetRequiredService<CaptureRollback>();
            rollback.BeforeWrite = async _ =>
            {
                if (!interfered)
                {
                    interfered = true;
                    Assert.Equal(1, await Walks.ProcessAsync(api, veraClaim.RunId));
                }
            };
            await rollback.ProcessAsync(requested.Id, Cancel);
        }

        var done = await admin.GetFromJsonAsync<RollbackResponse>($"/admin/rollbacks/{requested.Id}", Json, Cancel);
        Assert.True(interfered);
        Assert.Equal(1, done!.RolledBack);
        Assert.InRange(done.SkippedArea, 3_000, 4_200); // пересчитано с учётом Веры
        Assert.InRange(await LandAreaAsync(veraId), 3_300, 3_900);
        Assert.InRange(await LandAreaAsync(annaId), 9_100, 9_700);
        Assert.Equal(0, await LandAreaAsync(borisId), 1);
        Assert.Empty(TerritoryInvariants.Check(await MapOfAsync(area)));
    }

    [Fact]
    public async Task Only_an_administrator_can_roll_back()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (player, playerId) = await api.CreatePlayerClientAsync();
        var (admin, _) = await api.CreatePlayerClientAsync(UserRole.Admin);

        var byPlayer = await player.PostAsJsonAsync($"/admin/users/{playerId}/rollback", new RollbackRequest("я сам"), Json, Cancel);
        var anonymous = await api.CreateClient().PostAsJsonAsync($"/admin/users/{playerId}/rollback", new RollbackRequest("кто-то"), Json, Cancel);
        var noReason = await admin.PostAsJsonAsync($"/admin/users/{playerId}/rollback", new RollbackRequest("  "), Json, Cancel);
        var nobody = await admin.PostAsJsonAsync($"/admin/users/{Guid.CreateVersion7()}/rollback", new RollbackRequest("нет такого"), Json, Cancel);
        var unknownJob = await admin.GetAsync($"/admin/rollbacks/{Guid.CreateVersion7()}", Cancel);

        Assert.Equal(HttpStatusCode.Forbidden, byPlayer.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nobody.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownJob.StatusCode);
        await using var db = database.CreateContext();
        Assert.Null(await db.Users.Where(u => u.Id == playerId).Select(u => u.FrozenUntil).SingleAsync(Cancel)); // отказ не замораживает
    }

    // MARK: — вспомогательное

    private sealed record Scene(
        Guid AnnaId,
        HttpClient Boris,
        Guid BorisId,
        (Guid RunId, Guid CaptureId, long EndMs) BorisClaim,
        HttpClient Vera,
        Guid VeraId,
        HttpClient Admin,
        (double X, double Y) Area);

    /// <summary>
    /// Анна берёт квадрат 100 × 100 м, через полчаса Борис — такой же со сдвигом на 50 м: половина — у Анны.
    /// Все игроки и админ создаются сразу: токен, выпущенный после сдвига поддельных часов, был бы «из будущего».
    /// </summary>
    private async Task<Scene> AnnaThenBorisAsync(ApiFactory api)
    {
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (vera, veraId) = await api.CreatePlayerClientAsync();
        var (admin, _) = await api.CreatePlayerClientAsync(UserRole.Admin);
        var area = NewArea();
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromMinutes(30));
        var borisClaim = await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100));
        await Walks.ProcessAsync(api, borisClaim.RunId);
        Assert.InRange(await LandAreaAsync(annaId), 4_700, 5_300);
        return new Scene(annaId, boris, borisId, borisClaim, vera, veraId, admin, area);
    }

    private async Task<RollbackResponse> RequestAsync(HttpClient admin, Guid userId, HttpStatusCode expected = HttpStatusCode.Accepted)
    {
        var response = await admin.PostAsJsonAsync($"/admin/users/{userId}/rollback", new RollbackRequest("тест: телепорт по карте"), Json, Cancel);
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RollbackResponse>(Json, Cancel))!;
    }

    private async Task<RollbackResponse> RollBackAsync(ApiFactory api, HttpClient admin, Guid userId)
    {
        var requested = await RequestAsync(admin, userId);
        await ProcessAsync(api, requested.Id);
        return (await admin.GetFromJsonAsync<RollbackResponse>($"/admin/rollbacks/{requested.Id}", Json, Cancel))!;
    }

    private static async Task ProcessAsync(ApiFactory api, Guid rollbackId)
    {
        await using var scope = api.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CaptureRollback>().ProcessAsync(rollbackId, CancellationToken.None);
    }

    private async Task<double> LandAreaAsync(Guid userId)
    {
        await using var db = database.CreateContext();
        var parcels = await db.Parcels.Where(p => p.OwnerId == userId).Select(p => p.Geometry).ToListAsync(Cancel);
        return parcels.Sum(g => g.Area);
    }

    /// <summary>Карта тайла теста из базы со всеми полями состояния.</summary>
    private async Task<TerritoryMap> MapOfAsync((double X, double Y) area)
    {
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        await using var db = database.CreateContext();
        var parcels = await db.Parcels.AsNoTracking()
            .Where(p => p.League == Gorodki.Domain.Leagues.League.Run && p.TileX == tile.X && p.TileY == tile.Y)
            .ToListAsync(Cancel);
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
            })));
        return map;
    }
}
