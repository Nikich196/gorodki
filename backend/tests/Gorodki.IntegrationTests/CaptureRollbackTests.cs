using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Admin;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Config;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Territory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using Npgsql;
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
    public async Task Cheater_cannot_launder_stolen_land_by_walking_over_it()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var scene = await AnnaThenBorisAsync(api);
        var (annaId, boris, borisId, area, admin) = (scene.AnnaId, scene.Boris, scene.BorisId, scene.Area, scene.Admin);
        api.Time.Advance(TimeSpan.FromHours(21));

        // Борис дважды, с перерывом в сутки, прошёл по отнятому у Анны: визиты подняли уровень до 3 — земля «изменилась
        // после захвата». Два повышения переносом визитов не объяснить (за 20 ч уровень растёт один раз), так что землю
        // возвращает только правило «свои касания нарушителя — не касание».
        for (var day = 0; day < 2; day++)
        {
            var walk = await WalkAndFinishAsync(Cancel, api, boris, [(area.X + 75, area.Y - 250), (area.X + 75, area.Y + 450)]);
            api.Time.Advance(Gorodki.Api.Features.Territory.TerritoryReader.PublicDelay); // визит — через 20 минут после забега
            Assert.True(await VisitAsync(api, walk.Id) > 0);
            api.Time.Advance(TimeSpan.FromHours(21));
        }

        await using (var db = database.CreateContext())
        {
            var taken = await db.Parcels.AsNoTracking().SingleAsync(p => p.OwnerId == borisId && p.ShieldUntil != null, Cancel);
            Assert.Equal(3, taken.Level);
        }

        var done = await RollBackAsync(api, admin, borisId);

        Assert.InRange(done.SkippedArea, 0, 50); // свои визиты нарушителя — не «касание»
        Assert.InRange(await LandAreaAsync(annaId), 9_500, 10_500);
        Assert.Equal(0, await LandAreaAsync(borisId), 1);
        Assert.Empty(TerritoryInvariants.Check(await MapOfAsync(area)));
    }

    [Fact]
    public async Task Victims_visit_to_a_cracked_part_does_not_keep_the_crack_after_rollback()
    {
        // Борис треснул правую часть квадрата Анны (L2 → L1 и осада), потом Анна пробежала по треснувшей части. Раньше
        // визит делал её «тронутой»: откат нарушителя оставлял жертве −1 уровень и осаду. Теперь визит переносится на
        // возвращённую землю — посчитанный заново от прежнего куска.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (admin, _) = await api.CreatePlayerClientAsync(UserRole.Admin);
        var area = NewArea();
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromHours(21));
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId); // L2
        api.Time.Advance(TimeSpan.FromHours(1));
        ParcelEntity whole;
        await using (var db = database.CreateContext())
        {
            whole = await db.Parcels.AsNoTracking().SingleAsync(p => p.OwnerId == annaId, Cancel);
        }

        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Rectangle(area, 40, -10, 100, 120))).RunId);
        api.Time.Advance(TimeSpan.FromMinutes(30));
        // 60 м по треснувшей части (x от 40 до 100) — визит; по остальной земле 40 м — не визит.
        var walk = await WalkAndFinishAsync(Cancel, api, anna, [(area.X - 250, area.Y + 50), (area.X + 450, area.Y + 50)]);
        api.Time.Advance(Gorodki.Api.Features.Territory.TerritoryReader.PublicDelay + Gorodki.Api.Features.Territory.TerritoryReader.RevealStep);
        Assert.Equal(1, await VisitAsync(api, walk.Id));
        DateTimeOffset visitedAt;
        await using (var db = database.CreateContext())
        {
            var cracked = await db.Parcels.AsNoTracking().SingleAsync(p => p.OwnerId == annaId && p.SiegeUntil != null, Cancel);
            Assert.Equal(1, cracked.Level);
            Assert.True(cracked.LastVisitAt > cracked.SiegeUntil!.Value - TimeSpan.FromHours(24)); // визит — после трещины
            visitedAt = cracked.LastVisitAt;
        }

        var done = await RollBackAsync(api, admin, borisId);

        Assert.Equal(1, done.RolledBack);
        Assert.InRange(done.SkippedArea, 0, 50);
        Assert.Equal(0, await LandAreaAsync(borisId), 1);
        await using var check = database.CreateContext();
        var land = await check.Parcels.AsNoTracking().Where(p => p.OwnerId == annaId).ToListAsync(Cancel);

        // Треснувшая часть — прежний кусок с визитом Анны, посчитанным заново от него (L2: осады нет, но и 20 ч с
        // повышения не прошло); остаток — прежний кусок как был. Вместе — ровно прежний квадрат. Остаток отката по граням:
        // без захвата визит лёг бы на весь квадрат (по нему 100 м пути), а здесь — только на треснувшую часть: остаток вне
        // следа, по нему самому 40 м — меньше порога. Захват давно публичен, это не утечка, а неточность угасания остатка
        // на один визит.
        var before = StateOf(whole);
        var visited = CaptureRules.Visit(before, visitedAt, GameConfig.Default.Territory.ToRules())!;
        Assert.Equal((2, visitedAt, before.LastLevelUpAt), (visited.Level, visited.LastVisitAt, visited.LastLevelUpAt));
        Assert.Equal(2, land.Count);
        Assert.Contains(land, p => StateOf(p) == before);
        Assert.Contains(land, p => StateOf(p) == visited);
        Assert.True(GeoOps.UnionAll(land.Select(p => (Geometry)p.Geometry)).EqualsTopologically(whole.Geometry));
        Assert.Empty(TerritoryInvariants.Check(await MapOfAsync(area)));
    }

    [Fact]
    public async Task Rollback_never_moves_a_visible_version_back()
    {
        // Вера смотрит на тайл, пока захват Бориса ещё скрыт; откат захвата — изменение, и её версия только растёт.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var scene = await AnnaThenBorisAsync(api);
        var tile = TileKey.Of(WalkOrigin.X + scene.Area.X + 50, WalkOrigin.Y + scene.Area.Y + 50);
        var before = await VisibleVersionAsync(scene.Vera, tile);

        await RollBackAsync(api, scene.Admin, scene.BorisId);
        var after = await scene.Vera.GetFromJsonAsync<Gorodki.Api.Features.Territory.TerritoryResponse>(
            $"/territory?league=run&tiles={tile.X}:{tile.Y}@{before}", Json, Cancel);

        var changed = Assert.Single(after!.Tiles);
        Assert.True(changed.Version > before);
        Assert.InRange(changed.Parcels.Where(p => p.OwnerId == scene.AnnaId).Sum(p => p.Exterior.Count), 1, int.MaxValue);
    }

    private async Task<long> VisibleVersionAsync(HttpClient client, TileKey tile)
    {
        var response = await client.GetFromJsonAsync<Gorodki.Api.Features.Territory.TerritoryResponse>(
            $"/territory?league=run&tiles={tile.X}:{tile.Y}", Json, Cancel);
        return response!.Tiles.Single().Version;
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
    public async Task Damaged_journal_fails_only_that_capture_and_the_job_still_finishes()
    {
        // Испорченная запись журнала (ручная правка базы) — неудача этого захвата в итоге задания. Иначе задание
        // оставалось бы ожидающим и стояло первым в очереди откатов навсегда.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var scene = await AnnaThenBorisAsync(api);
        await using (var db = database.CreateContext())
        {
            await db.CaptureJournal
                .Where(j => j.CaptureId == scene.BorisClaim.CaptureId)
                .ExecuteUpdateAsync(set => set.SetProperty(j => j.Footprint, new byte[] { 0 }), Cancel);
        }

        var done = await RollBackAsync(api, scene.Admin, scene.BorisId);

        Assert.Equal(CaptureRollbackStatus.Done, done.Status);
        Assert.Equal((0, 1), (done.RolledBack, done.Failed));
        Assert.InRange(await LandAreaAsync(scene.AnnaId), 4_700, 5_300); // земля осталась как была
        await using var check = database.CreateContext();
        var job = await check.CaptureRollbacks.AsNoTracking().SingleAsync(r => r.Id == done.Id, Cancel);
        Assert.Contains(scene.BorisClaim.CaptureId.ToString(), job.LastError);
    }

    [Fact]
    public async Task Transient_database_failure_leaves_the_job_pending_for_the_next_pass()
    {
        // Обрыв соединения — не неудача захвата: иначе задание завершилось бы с неоткаченным захватом, и откат пришлось
        // бы ставить снова. Задание остаётся ожидающим, и следующий проход откатывает захват как обычно.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var scene = await AnnaThenBorisAsync(api);
        var requested = await RequestAsync(scene.Admin, scene.BorisId);

        await using (var scope = api.Services.CreateAsyncScope())
        {
            var rollback = scope.ServiceProvider.GetRequiredService<CaptureRollback>();
            // Так EF Core отдаёт обрыв соединения в запросе: InvalidOperationException вокруг временной NpgsqlException.
            rollback.BeforeWrite = _ => throw new InvalidOperationException(
                "An exception has been raised that is likely due to a transient failure.",
                new NpgsqlException("Exception while reading from stream", new IOException("Connection reset by peer")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => rollback.ProcessAsync(requested.Id, Cancel));
        }

        var pending = await scene.Admin.GetFromJsonAsync<RollbackResponse>($"/admin/rollbacks/{requested.Id}", Json, Cancel);
        Assert.Equal((CaptureRollbackStatus.Pending, 0), (pending!.Status, pending.Failed));
        Assert.InRange(await LandAreaAsync(scene.AnnaId), 4_700, 5_300); // земля не тронута

        await ProcessAsync(api, requested.Id); // следующий проход

        var done = await scene.Admin.GetFromJsonAsync<RollbackResponse>($"/admin/rollbacks/{requested.Id}", Json, Cancel);
        Assert.Equal((CaptureRollbackStatus.Done, 1, 0), (done!.Status, done.RolledBack, done.Failed));
        Assert.InRange(await LandAreaAsync(scene.AnnaId), 9_500, 10_500);
        Assert.Equal(0, await LandAreaAsync(scene.BorisId), 1);
    }

    [Fact]
    public async Task Summary_after_a_retry_counts_captures_rolled_back_in_every_attempt()
    {
        // Два захвата Бориса; обрыв связи на записи второго. Первый уже откачен и зафиксирован, повтор его не видит —
        // итог всё равно должен назвать оба, иначе администратор решит, что откачено меньше, чем на самом деле.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var scene = await AnnaThenBorisAsync(api);
        api.Time.Advance(TimeSpan.FromMinutes(30));
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, scene.Boris, Square(scene.Area, 300, 0, 60))).RunId);
        var requested = await RequestAsync(scene.Admin, scene.BorisId);

        await using (var scope = api.Services.CreateAsyncScope())
        {
            var rollback = scope.ServiceProvider.GetRequiredService<CaptureRollback>();
            var writes = 0;
            rollback.BeforeWrite = _ => ++writes == 1
                ? Task.CompletedTask
                : throw new InvalidOperationException(
                    "An exception has been raised that is likely due to a transient failure.",
                    new NpgsqlException("Exception while reading from stream", new IOException("Connection reset by peer")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => rollback.ProcessAsync(requested.Id, Cancel));
        }

        await ProcessAsync(api, requested.Id); // следующий проход

        var done = await scene.Admin.GetFromJsonAsync<RollbackResponse>($"/admin/rollbacks/{requested.Id}", Json, Cancel);
        Assert.Equal((CaptureRollbackStatus.Done, 2, 0), (done!.Status, done.RolledBack, done.Failed));
        await using var db = database.CreateContext();
        var areas = await db.Captures.Where(c => c.RollbackId == requested.Id).Select(c => c.RolledBackArea).ToListAsync(Cancel);
        Assert.Equal(2, areas.Count);
        Assert.Equal(areas.Sum(a => a ?? 0), done.RestoredArea, 1);
        Assert.Equal(0, await LandAreaAsync(scene.BorisId), 1);
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
        map.Load(parcels.Select(p => new Parcel(new TileKey(p.TileX, p.TileY), p.Geometry, StateOf(p))));
        return map;
    }

    /// <summary>Состояние куска из базы целиком.</summary>
    private static ParcelState StateOf(ParcelEntity p) => new()
    {
        OwnerId = p.OwnerId,
        Level = p.Level,
        LastVisitAt = p.LastVisitAt,
        LastLevelUpAt = p.LastLevelUpAt,
        ShieldUntil = p.ShieldUntil,
        SiegeUntil = p.SiegeUntil,
        LossWindowSince = p.LossWindowSince,
        LossAttackers = AttackerSet.Of(p.LossAttackers),
    };
}
