using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Admin;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Seasons;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Config;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Territory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Смена сезона на настоящей базе (PLAN.md, §3.4; #30): мягкий сброс земли и журнала той же формулой, что в домене,
/// один раз; скрытый в этот момент захват остаётся скрытым; старт Сезона 0 с чистой карты; «касались в этом сезоне».
/// </summary>
/// <remarks>Задача меняет всю землю общей тестовой базы — поэтому каждый тест сам снимает отметку своего сезона.</remarks>
[Collection(DatabaseCollection.Name)]
public sealed class SeasonRolloverTests(DatabaseFixture database)
{
    /// <summary>Начало Сезона 0 — 16.11 00:00 по Минску.</summary>
    private static readonly DateTimeOffset SeasonZero = new(2026, 11, 15, 21, 0, 0, TimeSpan.Zero);

    /// <summary>Начало Сезона 1 — 30.11 00:00 по Минску.</summary>
    private static readonly DateTimeOffset SeasonOne = new(2026, 11, 29, 21, 0, 0, TimeSpan.Zero);

    private static readonly TerritoryRules Rules = GameConfig.Default.Territory.ToRules();

    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Soft_reset_follows_the_domain_formula_on_land_and_journal_and_happens_once()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        GoTo(api, SeasonOne - TimeSpan.FromHours(3));
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromMinutes(30));
        var borisClaim = await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100)); // половина — L1 Анны, со щитом
        await ProcessAsync(api, borisClaim.RunId);

        // Остаток Анны — L3 с осадой и окном снятия уровней (визит вчера), земля «до» в журнале — L2 почти угасшая.
        await using (var db = database.CreateContext())
        {
            var yesterday = SeasonOne.AddDays(-1);
            await db.Parcels.Where(p => p.OwnerId == annaId).ExecuteUpdateAsync(
                s => s.SetProperty(p => p.Level, (short)3)
                    .SetProperty(p => p.LastVisitAt, yesterday)
                    .SetProperty(p => p.SiegeUntil, SeasonOne.AddHours(5))
                    .SetProperty(p => p.LossWindowSince, SeasonOne.AddHours(-2))
                    .SetProperty(p => p.LossAttackers, new[] { borisId }),
                Cancel);
            await db.CaptureJournalPieces.Where(p => p.CaptureId == borisClaim.CaptureId && !p.After).ExecuteUpdateAsync(
                s => s.SetProperty(p => p.Level, (short)2).SetProperty(p => p.LastVisitAt, SeasonOne.AddDays(-10)),
                Cancel);
        }

        await ReopenAsync(1);
        var tile = TileOf(area);
        var parcelsBefore = await ParcelsAsync(annaId, borisId);
        var journalBefore = await JournalAsync(borisClaim.CaptureId);
        var versionBefore = await VersionAsync(tile);
        Assert.Contains(parcelsBefore.Values, s => s.ShieldUntil is not null); // у Бориса — щит на взятом
        Assert.Contains(parcelsBefore.Values, s => s.Level == 3);

        GoTo(api, SeasonOne + TimeSpan.FromMinutes(1));
        Assert.True(await RolloverAsync(api) > 0);

        // Запрос в базе — та же формула, что SeasonReset.Soft: для каждого куска и каждой записи журнала.
        var parcelsAfter = await ParcelsAsync(annaId, borisId);
        Assert.Equal(parcelsBefore.Keys.Order(), parcelsAfter.Keys.Order());
        foreach (var (id, state) in parcelsBefore)
        {
            Assert.Equal(SeasonReset.Soft(state, SeasonOne, Rules), parcelsAfter[id]);
        }

        var journalAfter = await JournalAsync(borisClaim.CaptureId);
        foreach (var (id, state) in journalBefore)
        {
            Assert.Equal(SeasonReset.Soft(state, SeasonOne, Rules), journalAfter[id]);
        }

        Assert.All(parcelsAfter.Values, s =>
        {
            Assert.Equal(1, s.Level);
            Assert.True(s.ShieldUntil is null && s.SiegeUntil is null && s.LossWindowSince is null && s.LossAttackers.Count == 0);
        });
        Assert.Contains(parcelsAfter.Values, s => s.OwnerId == annaId && s.LastVisitAt == SeasonOne); // L3 со вчерашним визитом
        Assert.Equal(versionBefore + 1, await VersionAsync(tile)); // телефоны перечитают карту
        await using (var db = database.CreateContext())
        {
            Assert.Equal(api.Time.GetUtcNow(), (await db.Seasons.AsNoTracking().SingleAsync(s => s.Number == 1, Cancel)).ResetAt);
        }

        // Повтор задачи (Hangfire, второй экземпляр сервера) второй раз не сбрасывает.
        api.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await RolloverAsync(api));
        Assert.Equal(parcelsAfter, await ParcelsAsync(annaId, borisId));
        Assert.Equal(versionBefore + 1, await VersionAsync(tile));
    }

    [Fact]
    public async Task Capture_hidden_at_the_season_change_stays_hidden_until_its_public_boundary()
    {
        // §3.16: сброс меняет землю — и захват, скрытый задержкой в эту минуту, не должен от этого проступить на карте.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (vera, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        GoTo(api, SeasonOne - TimeSpan.FromHours(1));
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        GoTo(api, SeasonOne - TimeSpan.FromMinutes(5));
        var borisClaim = await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100));
        await ProcessAsync(api, borisClaim.RunId);
        DateTimeOffset publicAt;
        await using (var db = database.CreateContext())
        {
            var applied = (await db.Captures.AsNoTracking().SingleAsync(c => c.Id == borisClaim.CaptureId, Cancel)).AppliedAt!.Value;
            publicAt = TerritoryReader.PublicAt(applied, TerritoryReader.PublicDelay);
        }

        await ReopenAsync(1);
        GoTo(api, SeasonOne + TimeSpan.FromMinutes(1));
        Assert.True(await RolloverAsync(api) > 0);

        // Вера видит сброшенный мир без захвата Бориса: весь квадрат Анны одним куском, L1.
        var hidden = await TileAsync(vera, TileOf(area));
        Assert.DoesNotContain(hidden.Parcels, p => p.OwnerId == borisId);
        var annas = Assert.Single(hidden.Parcels, p => p.OwnerId == annaId);
        Assert.Equal(1, annas.Level);
        Assert.InRange(AreaOf(annas), 9_500, 10_500);

        // С границы публичности — захват Бориса, уже сброшенный: L1 без щита (взятое у Анны и ничьё — отдельными кусками:
        // сброс кусков не сливает).
        GoTo(api, publicAt);
        var borises = (await TileAsync(vera, TileOf(area))).Parcels.Where(p => p.OwnerId == borisId).ToList();
        Assert.NotEmpty(borises);
        Assert.All(borises, p => Assert.Equal((1, (long?)null), (p.Level, p.ShieldUntilMs)));
        Assert.InRange(borises.Sum(AreaOf), 9_500, 10_500);
    }

    [Fact]
    public async Task Rollback_after_the_season_change_returns_the_victim_its_reset_land()
    {
        // Журнал сброшен той же формулой — откат нарушителя через смену сезона возвращает жертве землю такой, какой она была бы
        // без захвата после сброса.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (admin, _) = await api.CreatePlayerClientAsync(UserRole.Admin);
        var area = NewArea();
        GoTo(api, SeasonOne - TimeSpan.FromHours(3));
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromMinutes(30));
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100))).RunId);
        var annaBefore = Assert.Single((await ParcelsAsync(annaId)).Values);

        await ReopenAsync(1);
        GoTo(api, SeasonOne + TimeSpan.FromMinutes(1));
        Assert.True(await RolloverAsync(api) > 0);

        var requested = await admin.PostAsJsonAsync($"/admin/users/{borisId}/rollback", new RollbackRequest("тест: смена сезона"), Json, Cancel);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var job = (await requested.Content.ReadFromJsonAsync<RollbackResponse>(Json, Cancel))!;
            await scope.ServiceProvider.GetRequiredService<CaptureRollback>().ProcessAsync(job.Id, CancellationToken.None);
        }

        var land = await ParcelsAsync(annaId);
        Assert.Equal(SeasonReset.Soft(annaBefore, SeasonOne, Rules), Assert.Single(land.Values)); // квадрат снова целый
        Assert.Empty(await ParcelsAsync(borisId));
    }

    [Fact]
    public async Task Season_zero_starts_from_a_clean_map_once_and_points_stay_as_history()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (vera, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        GoTo(api, SeasonZero - TimeSpan.FromHours(1)); // полевой тест
        var claim = await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100));
        await ProcessAsync(api, claim.RunId);
        Assert.NotEmpty(await ParcelsAsync(annaId));
        var versionBefore = await VersionAsync(TileOf(area));

        await ReopenAsync(0);
        GoTo(api, SeasonZero + TimeSpan.FromMinutes(1));
        Assert.True(await RolloverAsync(api) > 0);

        await using (var db = database.CreateContext())
        {
            Assert.False(await db.Parcels.AnyAsync(Cancel));
            Assert.False(await db.CaptureJournal.AnyAsync(Cancel));
            Assert.False(await db.CaptureJournalPieces.AnyAsync(Cancel));
            Assert.False(await db.ContestedZones.AnyAsync(Cancel));
            var score = await db.ScoreEvents.AsNoTracking().SingleAsync(e => e.CaptureId == claim.CaptureId, Cancel);
            Assert.Null(score.Season); // очки полевого теста — история вне сезонов
            Assert.NotNull((await db.Seasons.AsNoTracking().SingleAsync(s => s.Number == 0, Cancel)).ResetAt);
        }

        Assert.Equal(versionBefore + 1, await VersionAsync(TileOf(area)));
        Assert.Empty((await TileAsync(vera, TileOf(area))).Parcels);

        // Новая земля Сезона 0 повтором задачи не стирается.
        api.Time.Advance(TimeSpan.FromMinutes(30));
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await RolloverAsync(api));
        Assert.NotEmpty(await ParcelsAsync(annaId));
    }

    [Fact]
    public async Task Clean_map_also_wipes_points_of_season_zero_captures_applied_before_the_change()
    {
        // Сервер спал в полночь, и петля 00:25 применилась до задачи смены сезона — по земле полевых тестов, с бонусом за
        // взятую чужую. Землю смена стёрла, откатить захват уже нельзя (журнала нет) — очки уходят вместе с землёй. Очки
        // полевых тестов и дистанция Сезона 0 остаются: от карты они не зависят.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (vera, veraId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        await ReopenAsync(0);
        GoTo(api, SeasonZero - TimeSpan.FromHours(1)); // полевой тест
        var fieldTest = await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100));
        await ProcessAsync(api, fieldTest.RunId);

        GoTo(api, SeasonZero + TimeSpan.FromMinutes(30));
        var veraRun = await WalkAndFinishAsync(Cancel, api, vera, Rectangle(NewArea(), 0, 0, 500, 100));
        GoTo(api, SeasonZero + TimeSpan.FromMinutes(40)); // петля Бориса ≈ в 00:25 — половина по земле полевого теста
        var early = await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100));
        await ProcessAsync(api, early.RunId);
        api.Time.Advance(TerritoryReader.PublicDelay);
        Assert.NotNull(await VisitAsync(api, veraRun.Id));
        await using (var db = database.CreateContext())
        {
            var score = await db.ScoreEvents.AsNoTracking().SingleAsync(e => e.CaptureId == early.CaptureId, Cancel);
            Assert.Equal(0, score.Season);
            Assert.InRange(score.Basis, 107, 118); // 50 соток ничьей + 50 соток чужой × 1,5 — бонус за землю полевого теста
        }

        Assert.True(await RolloverAsync(api) > 0);

        await using (var db = database.CreateContext())
        {
            Assert.False(await db.Parcels.AnyAsync(p => p.OwnerId == borisId, Cancel));
            Assert.False(await db.ScoreEvents.AnyAsync(e => e.UserId == borisId, Cancel));
            Assert.Null((await db.ScoreEvents.AsNoTracking().SingleAsync(e => e.CaptureId == fieldTest.CaptureId, Cancel)).Season);
            var distance = await db.ScoreEvents.AsNoTracking().SingleAsync(e => e.UserId == veraId, Cancel);
            Assert.Equal((ScoreKind.Distance, 0), (distance.Kind, distance.Season));
        }
    }

    [Fact]
    public async Task Field_test_loop_applied_after_the_clean_start_does_not_land_on_the_clean_map()
    {
        // Петля полевого теста замкнута до полуночи старта Сезона 0, а применяется после смены: земли полевых тестов больше
        // нет, и этой петле ложиться не на что — иначе она принесла бы землю полевого теста на чистую карту.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        GoTo(api, SeasonZero - TimeSpan.FromMinutes(5));
        var claim = await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100)); // петля ≈ в 23:50

        await ReopenAsync(0);
        GoTo(api, SeasonZero + TimeSpan.FromMinutes(1));
        await RolloverAsync(api); // земли в базе может и не быть — версий не станет больше, но смена пройдёт
        await using (var check = database.CreateContext())
        {
            Assert.NotNull((await check.Seasons.AsNoTracking().SingleAsync(s => s.Number == 0, Cancel)).ResetAt);
        }

        Assert.Equal(1, await ProcessAsync(api, claim.RunId));

        await using var db = database.CreateContext();
        var capture = await db.Captures.AsNoTracking().SingleAsync(c => c.Id == claim.CaptureId, Cancel);
        Assert.Equal((CaptureStatus.Stale, "season_wipe"), (capture.Status, capture.RejectCode));
        Assert.True(capture.EffectiveAt < SeasonZero);
        Assert.False(await db.Parcels.AnyAsync(p => p.OwnerId == annaId, Cancel));
        Assert.False(await db.ScoreEvents.AnyAsync(e => e.CaptureId == claim.CaptureId, Cancel));
    }

    [Fact]
    public async Task Visit_from_before_midnight_counted_after_the_change_does_not_raise_the_level_but_keeps_new_season_levels()
    {
        // Визиты забега, кончившегося в 23:56, считаются после границы публичности — уже после задачи смены сезона. Кусок
        // должен стать таким, как в порядке «визит, потом сброс»: L1 с «последним визитом» в полночь, а не L2 на 12 дней.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var annaArea = NewArea();
        var borisArea = NewArea();
        GoTo(api, SeasonOne - TimeSpan.FromHours(30));
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(annaArea, 0, 0, 100))).RunId);
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Square(borisArea, 0, 0, 100))).RunId);
        var annaBefore = Assert.Single((await ParcelsAsync(annaId)).Values);

        // Путь проходит 100 м по своему квадрату (после обрезки 200 м) около 23:52.
        GoTo(api, SeasonOne - TimeSpan.FromMinutes(3));
        var annaRun = await WalkAndFinishAsync(Cancel, api, anna, [(annaArea.X - 300, annaArea.Y + 50), (annaArea.X + 400, annaArea.Y + 50)], TimeSpan.FromMinutes(9));
        var borisRun = await WalkAndFinishAsync(Cancel, api, boris, [(borisArea.X - 300, borisArea.Y + 50), (borisArea.X + 400, borisArea.Y + 50)], TimeSpan.FromMinutes(9));

        await ReopenAsync(1);
        GoTo(api, SeasonOne + TimeSpan.FromMinutes(1));
        Assert.True(await RolloverAsync(api) > 0);

        GoTo(api, SeasonOne + TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        Assert.Equal(1, await VisitAsync(api, annaRun.Id));
        var annas = Assert.Single((await ParcelsAsync(annaId)).Values);
        var at = annas.TouchedAt;
        Assert.True(at < SeasonOne && at > SeasonOne - TimeSpan.FromMinutes(15));
        Assert.Equal(SeasonReset.Soft(CaptureRules.Visit(annaBefore, at, Rules)!, SeasonOne, Rules), annas);
        Assert.Equal((1, SeasonOne), (annas.Level, annas.LastVisitAt));

        // Борис уже в новом сезоне поднял уровень своей петлёй; визит его забега до полуночи посчитан позже — L2 остаётся.
        GoTo(api, SeasonOne + TimeSpan.FromHours(2));
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Square(borisArea, 0, 0, 100))).RunId);
        var levelled = Assert.Single((await ParcelsAsync(borisId)).Values);
        Assert.Equal(2, levelled.Level);
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep); // визиты ждут раскрытия его захвата
        Assert.Equal(0, await VisitAsync(api, borisRun.Id)); // посчитан, но ничего не меняет — второго сброса нет
        Assert.Equal(levelled, Assert.Single((await ParcelsAsync(borisId)).Values));
    }

    [Fact]
    public async Task Loop_from_before_midnight_applied_after_the_change_does_not_carry_a_shield_into_the_new_season()
    {
        // Петля Бориса в 23:45 по L1 Анны применена после задачи смены сезона: взятая земля — как после сброса, без щита.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        GoTo(api, SeasonOne - TimeSpan.FromHours(30));
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        GoTo(api, SeasonOne - TimeSpan.FromMinutes(5));
        var late = await WalkAndClaimAsync(Cancel, api, boris, Square(area, 0, 0, 100));

        await ReopenAsync(1);
        GoTo(api, SeasonOne + TimeSpan.FromMinutes(1));
        Assert.True(await RolloverAsync(api) > 0);
        Assert.Equal(1, await ProcessAsync(api, late.RunId));

        await using (var db = database.CreateContext())
        {
            var capture = await db.Captures.AsNoTracking().SingleAsync(c => c.Id == late.CaptureId, Cancel);
            Assert.Equal(CaptureStatus.Applied, capture.Status);
            Assert.True(capture.EffectiveAt < SeasonOne);
            Assert.Equal(0, (await db.ScoreEvents.AsNoTracking().SingleAsync(e => e.CaptureId == late.CaptureId, Cancel)).Season);
        }

        var taken = (await ParcelsAsync(borisId)).Values.ToList();
        Assert.NotEmpty(taken);
        Assert.All(taken, p => Assert.Equal((1, (DateTimeOffset?)null), (p.Level, p.ShieldUntil)));
    }

    [Fact]
    public async Task Rollback_racing_the_season_change_returns_the_victim_its_reset_land()
    {
        // Смена сезона коммитится между расчётом отката и записью: запись отвергнута (версии тайлов выросли), и повтор
        // считает откат по сброшенному журналу — жертва получает землю такой, какой она была бы после сброса, а не L3 со щитом.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (admin, _) = await api.CreatePlayerClientAsync(UserRole.Admin);
        var area = NewArea();
        GoTo(api, SeasonOne - TimeSpan.FromHours(3));
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromMinutes(30));
        var borisClaim = await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100));
        await ProcessAsync(api, borisClaim.RunId);

        // Земля Анны «до» в журнале — L3 со щитом и визитом вчера.
        await using (var db = database.CreateContext())
        {
            Assert.True(await db.CaptureJournalPieces.Where(p => p.CaptureId == borisClaim.CaptureId && !p.After && p.OwnerId == annaId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(p => p.Level, (short)3)
                        .SetProperty(p => p.LastVisitAt, SeasonOne.AddDays(-1))
                        .SetProperty(p => p.ShieldUntil, SeasonOne.AddDays(3)),
                    Cancel) > 0);
        }

        await ReopenAsync(1);
        GoTo(api, SeasonOne + TimeSpan.FromMinutes(1));
        var (job, hookCalls) = await RollBackRacingTheChangeAsync(api, admin, borisId);

        Assert.Equal(2, hookCalls); // первая запись отвергнута, вторая прошла
        Assert.Equal((1, 0, 0), (job.RolledBack, job.WithoutJournal, job.Failed));
        var annas = (await ParcelsAsync(annaId)).Values.ToList();
        Assert.All(annas, p => Assert.Equal((1, (DateTimeOffset?)null), (p.Level, p.ShieldUntil)));
        Assert.Contains(annas, p => p.LastVisitAt == SeasonOne); // возвращённая половина: L3 со вчерашним визитом после сброса
        Assert.Empty(await ParcelsAsync(borisId));
    }

    [Fact]
    public async Task Rollback_racing_the_clean_start_finds_no_journal_and_does_not_fail()
    {
        // Смена на Сезон 0 стёрла журнал между расчётом отката и записью: откатывать не по чему — как у отката после смены.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (admin, _) = await api.CreatePlayerClientAsync(UserRole.Admin);
        var area = NewArea();
        GoTo(api, SeasonZero - TimeSpan.FromHours(3));
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromMinutes(30));
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100))).RunId);

        await ReopenAsync(0);
        GoTo(api, SeasonZero + TimeSpan.FromMinutes(1));
        var (job, hookCalls) = await RollBackRacingTheChangeAsync(api, admin, borisId);

        Assert.Equal(1, hookCalls);
        Assert.Equal((0, 1, 0), (job.RolledBack, job.WithoutJournal, job.Failed));
    }

    [Fact]
    public async Task Only_land_the_owner_took_or_refreshed_this_season_counts_as_touched_and_others_see_it_from_the_boundary()
    {
        // Опора среза E7 (§3.4: «за удержание дают очки только участки, которых касались в этом сезоне»).
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var first = NewArea();
        GoTo(api, SeasonOne - TimeSpan.FromHours(2));
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(first, 0, 0, 100))).RunId);
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100))).RunId);

        await ReopenAsync(1);
        GoTo(api, SeasonOne + TimeSpan.FromMinutes(1));
        Assert.True(await RolloverAsync(api) > 0);
        Assert.InRange(await OwnedAsync(api, annaId, annaId, touchedSince: null), 19_000, 21_000); // сброс землю не отнял
        Assert.Equal(0, await OwnedAsync(api, annaId, annaId, SeasonOne), 1); // но касанием в сезоне не считается

        // Анна снова обегает первый квадрат — освежает своим забегом: он «тронут в этом сезоне».
        GoTo(api, SeasonOne + TimeSpan.FromHours(2));
        var refresh = await WalkAndClaimAsync(Cancel, api, anna, Square(first, 0, 0, 100));
        await ProcessAsync(api, refresh.RunId);
        Assert.InRange(await OwnedAsync(api, annaId, annaId, SeasonOne), 9_500, 10_500); // сама — сразу
        Assert.Equal(0, await OwnedAsync(api, annaId, viewer: null, SeasonOne), 1); // другие — с границы, как карту

        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        Assert.InRange(await OwnedAsync(api, annaId, viewer: null, SeasonOne), 9_500, 10_500);
    }

    // MARK: — вспомогательное

    private static void GoTo(ApiFactory api, DateTimeOffset moment) => api.Time.Advance(moment - api.Time.GetUtcNow());

    private static TileKey TileOf((double X, double Y) area) => TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);

    private static async Task<int> RolloverAsync(ApiFactory api)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SeasonRollover>().RunIfDueAsync(CancellationToken.None);
    }

    /// <summary>
    /// Откат захватов игрока, во время которого проходит смена сезона: задача смены выполняется в первом вызове
    /// <see cref="CaptureRollback.BeforeWrite"/> — между расчётом отката и записью.
    /// </summary>
    private async Task<(CaptureRollbackEntity Job, int HookCalls)> RollBackRacingTheChangeAsync(ApiFactory api, HttpClient admin, Guid userId)
    {
        var requested = await admin.PostAsJsonAsync($"/admin/users/{userId}/rollback", new RollbackRequest("тест: смена сезона во время отката"), Json, Cancel);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var id = (await requested.Content.ReadFromJsonAsync<RollbackResponse>(Json, Cancel))!.Id;
        var hookCalls = 0;
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var rollback = scope.ServiceProvider.GetRequiredService<CaptureRollback>();
            rollback.BeforeWrite = async _ =>
            {
                if (++hookCalls == 1)
                {
                    Assert.True(await RolloverAsync(api) > 0);
                }
            };
            await rollback.ProcessAsync(id, CancellationToken.None);
        }

        await using var db = database.CreateContext();
        return (await db.CaptureRollbacks.AsNoTracking().SingleAsync(r => r.Id == id, Cancel), hookCalls);
    }

    /// <summary>Снимает отметку смены сезона: база общая, и сезон мог уже смениться в другом тесте.</summary>
    private async Task ReopenAsync(int season)
    {
        await using var db = database.CreateContext();
        await db.Seasons.Where(s => s.Number == season).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResetAt, (DateTimeOffset?)null), Cancel);
    }

    private async Task<double> OwnedAsync(ApiFactory api, Guid userId, Guid? viewer, DateTimeOffset? touchedSince)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TerritoryReader>()
            .VisibleOwnedAreaAsync(userId, League.Run, new TerritoryViewer(viewer, false), touchedSince, CancellationToken.None);
    }

    private async Task<Dictionary<long, ParcelState>> ParcelsAsync(params Guid[] owners)
    {
        await using var db = database.CreateContext();
        return (await db.Parcels.AsNoTracking().Where(p => owners.Contains(p.OwnerId)).ToListAsync(Cancel))
            .ToDictionary(p => p.Id, p => StateOf(p.OwnerId, p.Level, p.LastVisitAt, p.LastLevelUpAt, p.ShieldUntil, p.SiegeUntil, p.LossWindowSince, p.LossAttackers, p.TouchedAt));
    }

    private async Task<Dictionary<long, ParcelState>> JournalAsync(Guid captureId)
    {
        await using var db = database.CreateContext();
        return (await db.CaptureJournalPieces.AsNoTracking().Where(p => p.CaptureId == captureId).ToListAsync(Cancel))
            .ToDictionary(p => p.Id, p => StateOf(p.OwnerId, p.Level, p.LastVisitAt, p.LastLevelUpAt, p.ShieldUntil, p.SiegeUntil, p.LossWindowSince, p.LossAttackers, p.TouchedAt));
    }

    /// <summary>Состояние куска из столбцов базы — все поля, как их читает сервер.</summary>
    private static ParcelState StateOf(
        Guid owner,
        short level,
        DateTimeOffset lastVisit,
        DateTimeOffset lastLevelUp,
        DateTimeOffset? shield,
        DateTimeOffset? siege,
        DateTimeOffset? lossWindow,
        Guid[] attackers,
        DateTimeOffset touched) => new()
        {
            OwnerId = owner,
            Level = level,
            LastVisitAt = lastVisit,
            LastLevelUpAt = lastLevelUp,
            ShieldUntil = shield,
            SiegeUntil = siege,
            LossWindowSince = lossWindow,
            LossAttackers = AttackerSet.Of(attackers),
            TouchedAt = touched,
        };

    private async Task<long> VersionAsync(TileKey tile)
    {
        await using var db = database.CreateContext();
        return await db.TileVersions.Where(v => v.League == League.Run && v.TileX == tile.X && v.TileY == tile.Y).Select(v => v.Version).SingleAsync(Cancel);
    }

    private async Task<TileTerritory> TileAsync(HttpClient client, TileKey tile) =>
        Assert.Single((await client.GetFromJsonAsync<TerritoryResponse>($"/territory?league=run&tiles={tile.X}:{tile.Y}", Json, Cancel))!.Tiles);

    /// <summary>Площадь куска с карты (внешнее кольцо из широт и долгот обратно в UTM), м².</summary>
    private static double AreaOf(ParcelView parcel)
    {
        var ring = Enumerable.Range(0, parcel.Exterior.Count / 2)
            .Select(i => Utm34.Forward(parcel.Exterior[2 * i], parcel.Exterior[(2 * i) + 1]))
            .Select(c => new Coordinate(c.Easting, c.Northing))
            .ToArray();
        return GeoOps.Factory.CreatePolygon(ring).Area;
    }
}
