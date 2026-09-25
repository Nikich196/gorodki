using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Admin;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Me;
using Gorodki.Api.Features.Scoring;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Очки сезона на настоящей базе (PLAN.md, §3.5; docs/architecture/scoring-and-seasons.md): начисление пишется в транзакции
/// захвата и визитов, другим видно только с границы публичности, откат его стирает.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ScoringTests(DatabaseFixture database)
{
    /// <summary>Середина Сезона 1 (30.11–13.12): 1 декабря, 12:00 по Минску.</summary>
    private static readonly DateTimeOffset InSeasonOne = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);

    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Capture_points_are_written_with_the_capture_and_others_see_them_only_from_the_public_boundary()
    {
        // §3.16: рейтинг, который показал бы очки Анны раньше карты, выдал бы её свежий захват — где она сейчас.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        GoTo(api, InSeasonOne); // игроки заведены раньше: токен «из будущего» не прошёл бы проверку

        var claim = await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100));
        await ProcessAsync(api, claim.RunId);

        var (capture, score) = await CaptureAndScoreAsync(claim.CaptureId);
        Assert.Equal((ScoreKind.Capture, annaId, League.Run, 1), (score.Kind, score.UserId, score.League, score.Season));
        Assert.Equal((claim.RunId, GameClock.GameDayOf(capture.EffectiveAt!.Value), capture.EffectiveAt.Value), (score.RunId!.Value, score.GameDay, score.EffectiveAt));
        Assert.InRange(score.Points, 95, 105); // 1 га ничьей земли — 100 соток, по сотке за очко
        var publicAt = TerritoryReader.PublicAt(capture.AppliedAt!.Value, TerritoryReader.PublicDelay);
        Assert.Equal(publicAt, score.VisibleAt);

        // Граница — та же, что у карты: за миллисекунду до неё очков Анны в сезоне не видно никому, на ней — видно.
        GoTo(api, publicAt - TimeSpan.FromMilliseconds(1));
        Assert.False((await SeasonTotalsAsync(api)).ContainsKey(annaId));
        Assert.Empty((await ExportAsync(anna)).Scores); // и самой Анне: очки выдали бы разбивку итога (C1)

        GoTo(api, publicAt);
        Assert.Equal(score.Points, (await SeasonTotalsAsync(api))[annaId]);
        var exported = Assert.Single((await ExportAsync(anna)).Scores);
        Assert.Equal((claim.CaptureId, score.Points, "capture", 1), (exported.CaptureId!.Value, exported.Points, exported.Kind, exported.Season!.Value));
    }

    [Fact]
    public async Task Demo_captures_score_at_once_and_points_outside_seasons_have_no_season()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (demo, _) = await api.CreatePlayerClientAsync(UserRole.Demo);

        var claim = await WalkAndClaimAsync(Cancel, api, demo, Square(NewArea(), 0, 0, 100)); // сейчас — предсезонье
        await ProcessAsync(api, claim.RunId);

        var (capture, score) = await CaptureAndScoreAsync(claim.CaptureId);
        Assert.Equal(capture.AppliedAt, score.VisibleAt); // демо на показе видно сразу, как и на карте
        Assert.Null(score.Season);
    }

    [Fact]
    public async Task Enemy_land_gives_a_bonus_and_untaken_land_gives_nothing()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var annaClaim = await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100));
        await ProcessAsync(api, annaClaim.RunId);
        api.Time.Advance(TimeSpan.FromMinutes(30));

        // Половина петли Бориса — L1 Анны: 50 соток ничьей + 50 соток чужой × 1,5 = 125 взвешенных соток,
        // по ступеням захвата 100 + 25 × 0,5 = 112,5 зачётных.
        var borisClaim = await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100));
        await ProcessAsync(api, borisClaim.RunId);
        var (_, bonus) = await CaptureAndScoreAsync(borisClaim.CaptureId);
        Assert.Equal(borisId, bonus.UserId);
        Assert.InRange(bonus.Basis, 107, 118);
        Assert.InRange(bonus.Points, 107, 118);

        // Та же петля Бориса ещё раз: земля уже его — только освежение, очков нет (это удержание, а не захват).
        api.Time.Advance(TimeSpan.FromMinutes(30));
        var again = await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100));
        await ProcessAsync(api, again.RunId);
        await using var db = database.CreateContext();
        Assert.Equal(CaptureStatus.Applied, (await db.Captures.AsNoTracking().SingleAsync(c => c.Id == again.CaptureId, Cancel)).Status);
        Assert.False(await db.ScoreEvents.AnyAsync(e => e.CaptureId == again.CaptureId, Cancel));
    }

    [Fact]
    public async Task Daily_tiers_count_what_the_player_already_scored_that_game_day()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        GoTo(api, InSeasonOne);

        var first = await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100));
        await ProcessAsync(api, first.RunId);
        var (_, full) = await CaptureAndScoreAsync(first.CaptureId);

        // Будто за эти сутки уже зачтено 5 000 соток — дальше третья ступень суток: четверть.
        await using (var db = database.CreateContext())
        {
            await db.ScoreEvents.Where(e => e.CaptureId == first.CaptureId).ExecuteUpdateAsync(s => s.SetProperty(e => e.Basis, 5_000), Cancel);
        }

        api.Time.Advance(TimeSpan.FromMinutes(30));
        var second = await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100));
        await ProcessAsync(api, second.RunId);
        var (_, quarter) = await CaptureAndScoreAsync(second.CaptureId);

        // Следующие игровые сутки — ступени суток с нуля.
        api.Time.Advance(TimeSpan.FromDays(1));
        var nextDay = await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100));
        await ProcessAsync(api, nextDay.RunId);
        var (_, fresh) = await CaptureAndScoreAsync(nextDay.CaptureId);

        Assert.InRange(full.Points, 95, 105);
        Assert.Equal(full.GameDay, quarter.GameDay);
        Assert.InRange(quarter.Points, 23, 27);
        Assert.Equal(full.GameDay.AddDays(1), fresh.GameDay);
        Assert.InRange(fresh.Points, 95, 105);
    }

    [Fact]
    public async Task Rolled_back_capture_loses_its_points_and_others_keep_theirs()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (admin, _) = await api.CreatePlayerClientAsync(UserRole.Admin);
        var annaClaim = await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100));
        await ProcessAsync(api, annaClaim.RunId);
        var borisClaim = await WalkAndClaimAsync(Cancel, api, boris, Square(NewArea(), 0, 0, 100));
        await ProcessAsync(api, borisClaim.RunId);
        await CaptureAndScoreAsync(borisClaim.CaptureId);

        var requested = await admin.PostAsJsonAsync($"/admin/users/{borisId}/rollback", new RollbackRequest("тест: очки"), Json, Cancel);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var job = (await requested.Content.ReadFromJsonAsync<RollbackResponse>(Json, Cancel))!;
            await scope.ServiceProvider.GetRequiredService<CaptureRollback>().ProcessAsync(job.Id, CancellationToken.None);
        }

        await using var db = database.CreateContext();
        Assert.NotNull((await db.Captures.AsNoTracking().SingleAsync(c => c.Id == borisClaim.CaptureId, Cancel)).RolledBackAt);
        Assert.False(await db.ScoreEvents.AnyAsync(e => e.UserId == borisId, Cancel));
        Assert.True(await db.ScoreEvents.AnyAsync(e => e.UserId == annaId && e.CaptureId == annaClaim.CaptureId, Cancel));
    }

    [Fact]
    public async Task Distance_scores_with_the_visits_trimmed_and_up_to_the_daily_cap()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        GoTo(api, InSeasonOne);

        // Прямоугольник 500 × 100 м — 1 200 м пути; без первых и последних 200 м (§3.16) — 800 м, 8 очков.
        var walk = await WalkAndFinishAsync(Cancel, api, anna, Rectangle(NewArea(), 0, 0, 500, 100));
        Assert.Null(await VisitAsync(api, walk.Id)); // конец забега ещё не публичен — ни визитов, ни дистанции
        await using (var db = database.CreateContext())
        {
            Assert.False(await db.ScoreEvents.AnyAsync(e => e.RunId == walk.Id, Cancel));
        }

        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        Assert.NotNull(await VisitAsync(api, walk.Id));
        Assert.Null(await VisitAsync(api, walk.Id)); // повтор ничего не начисляет

        ScoreEventEntity distance;
        await using (var db = database.CreateContext())
        {
            distance = await db.ScoreEvents.AsNoTracking().SingleAsync(e => e.RunId == walk.Id, Cancel);
            // Будто за эти сутки уже пройдено 19,9 км: следующему забегу до потолка 20 км остаётся 100 м.
            await db.ScoreEvents.Where(e => e.Id == distance.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Basis, 19_900), Cancel);
        }

        Assert.Equal((ScoreKind.Distance, annaId, 1, 8), (distance.Kind, distance.UserId, distance.Season, distance.Points));
        Assert.InRange(distance.Basis, 790, 810);
        Assert.Equal(api.Time.GetUtcNow(), distance.VisibleAt); // считается уже после границы — видна сразу
        Assert.True((await SeasonTotalsAsync(api)).ContainsKey(annaId));

        var second = await WalkAndFinishAsync(Cancel, api, anna, Rectangle(NewArea(), 0, 0, 500, 100));
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        Assert.NotNull(await VisitAsync(api, second.Id));
        await using (var db = database.CreateContext())
        {
            var capped = await db.ScoreEvents.AsNoTracking().SingleAsync(e => e.RunId == second.Id, Cancel);
            Assert.Equal((distance.GameDay, 1, 100.0), (capped.GameDay, capped.Points, capped.Basis));
        }
    }

    // MARK: — вспомогательное

    private static void GoTo(ApiFactory api, DateTimeOffset moment) => api.Time.Advance(moment - api.Time.GetUtcNow());

    private async Task<(CaptureEntity Capture, ScoreEventEntity Score)> CaptureAndScoreAsync(Guid captureId)
    {
        await using var db = database.CreateContext();
        var capture = await db.Captures.AsNoTracking().SingleAsync(c => c.Id == captureId, Cancel);
        Assert.Equal(CaptureStatus.Applied, capture.Status);
        return (capture, await db.ScoreEvents.AsNoTracking().SingleAsync(e => e.CaptureId == captureId, Cancel));
    }

    private async Task<Dictionary<Guid, int>> SeasonTotalsAsync(ApiFactory api)
    {
        await using var db = database.CreateContext();
        return await ScoreBook.SeasonTotalsAsync(db, League.Run, 1, api.Time.GetUtcNow(), Cancel);
    }

    private async Task<AccountExportResponse> ExportAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<AccountExportResponse>("/me/export", Json, Cancel))!;
}
