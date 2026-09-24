using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Fog;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Хранение забегов на настоящей базе (PLAN.md, §3.16): сырые точки стираются через 14 дней, а сервер по-прежнему знает,
/// какие точки получал; забытый активный забег закрывается и открывает туман.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class RunRetentionTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Raw_points_are_erased_after_14_days_and_the_run_still_knows_what_it_received()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync(); // до сдвига часов: токен «из будущего» не прошёл бы проверку
        var run = await WalkAndFinishAsync(Cancel, api, client, Square(NewArea(), 0, 0, 100));
        var before = await client.GetFromJsonAsync<RunResponse>($"/runs/{run.Id}", Json, Cancel);
        await PurgeAllAsync(api);
        Assert.Null(await PurgedAtAsync(run.Id)); // забегу 20 минут — точки на месте

        api.Time.Advance(TimeSpan.FromDays(14));
        await PurgeAllAsync(api);

        Assert.NotNull(await PurgedAtAsync(run.Id));
        await using (var db = database.CreateContext())
        {
            var chunks = await db.RunChunks.AsNoTracking().Where(c => c.RunId == run.Id).ToListAsync(Cancel);
            Assert.NotEmpty(chunks);
            Assert.All(chunks, c => Assert.Empty(c.Points));
        }

        // Для телефона ничего не изменилось: те же полученные точки, ничего не «не хватает» — досылать нечего.
        var after = await client.GetFromJsonAsync<RunResponse>($"/runs/{run.Id}", Json, Cancel);
        Assert.Equal(before!.Received, after!.Received);
        Assert.Empty(after.Missing);

        // Туман и визиты по стёртым точкам не считаются, судья по пустому забегу не падает.
        await using var scope = api.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        Assert.DoesNotContain(run.Id, await services.GetRequiredService<FogProcessor>().RunsReadyAsync(1_000, Cancel));
        Assert.DoesNotContain(run.Id, await services.GetRequiredService<VisitProcessor>().RunsReadyAsync(1_000, Cancel));
        Assert.Null(await services.GetRequiredService<FogProcessor>().StampRunAsync(run.Id, Cancel));
        var entity = await services.GetRequiredService<AppDbContext>().Runs.AsNoTracking().SingleAsync(r => r.Id == run.Id, Cancel);
        var (_, judgement) = await services.GetRequiredService<RunJudgements>().JudgeAsync(entity, Cancel);
        Assert.Empty(judgement.Points);
    }

    [Fact]
    public async Task Run_with_erased_points_accepts_no_more_chunks()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();
        var start = await StartWalkAsync(Cancel, api, client);
        var chunks = WalkChunks(api, WalkPoints(start, Square(NewArea(), 0, 0, 100)));
        var first = await client.PutAsJsonAsync($"/runs/{start.Id}/chunks/0", chunks[0], Json, Cancel);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await using (var db = database.CreateContext())
        {
            await db.Runs
                .Where(r => r.Id == start.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.PointsPurgedAt, api.Time.GetUtcNow()), Cancel);
        }

        var next = await client.PutAsJsonAsync($"/runs/{start.Id}/chunks/{chunks[1].Points![0].Seq}", chunks[1], Json, Cancel);

        Assert.Equal(HttpStatusCode.Conflict, next.StatusCode);
        Assert.Contains("upload_window_closed", await next.Content.ReadAsStringAsync(Cancel));
        await using (var db = database.CreateContext())
        {
            Assert.Equal(1, await db.RunChunks.CountAsync(c => c.RunId == start.Id, Cancel));
        }
    }

    [Fact]
    public async Task Loop_claims_are_refused_once_points_are_erased_or_the_upload_window_is_closed()
    {
        // Судить петлю не по чему: иначе заявка на старый забег роняла бы обработчик, пока не кончатся её попытки.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();
        var (erased, erasedPoints) = await WalkAsync(Cancel, api, client, Square(NewArea(), 0, 0, 100));
        await using (var db = database.CreateContext())
        {
            await db.Runs
                .Where(r => r.Id == erased.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.PointsPurgedAt, api.Time.GetUtcNow()), Cancel);
        }

        var (late, latePoints) = await WalkAsync(Cancel, api, client, Square(NewArea(), 0, 0, 100));
        var onErased = await ClaimAsync(api, client, erased.Id, erasedPoints.Count - 1);
        api.Time.Advance(RunLimits.UploadWindow + TimeSpan.FromMinutes(1));
        var afterWindow = await ClaimAsync(api, client, late.Id, latePoints.Count - 1);

        foreach (var response in new[] { onErased, afterWindow })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("upload_window_closed", await response.Content.ReadAsStringAsync(Cancel));
        }

        await using var check = database.CreateContext();
        Assert.False(await check.Captures.AnyAsync(c => c.RunId == erased.Id || c.RunId == late.Id, Cancel));
    }

    [Fact]
    public async Task Claim_still_waiting_when_the_points_are_erased_ends_as_stale()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();
        var claim = await WalkAndClaimAsync(Cancel, api, client, Square(NewArea(), 0, 0, 100)); // заявка ещё не обработана
        await using (var db = database.CreateContext())
        {
            await db.Runs
                .Where(r => r.Id == claim.RunId)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.PointsPurgedAt, api.Time.GetUtcNow()), Cancel);
            await db.RunChunks
                .Where(c => c.RunId == claim.RunId)
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.Points, Array.Empty<byte>()), Cancel);
        }

        Assert.Equal(1, await Walks.ProcessAsync(api, claim.RunId));

        await using var check = database.CreateContext();
        var capture = await check.Captures.AsNoTracking().SingleAsync(c => c.Id == claim.CaptureId, Cancel);
        Assert.Equal((CaptureStatus.Stale, "stale"), (capture.Status, capture.RejectCode));
    }

    private static Task<HttpResponseMessage> ClaimAsync(ApiFactory api, HttpClient client, Guid runId, int endSeq) =>
        client.PostAsJsonAsync(
            $"/runs/{runId}/loops",
            new LoopClaimRequest(0, 0, endSeq, LoopClosure.Proximity, 10_000, api.Time.GetUtcNow().ToUnixTimeMilliseconds()),
            Json,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Forgotten_run_is_closed_a_day_after_its_length_limit_and_opens_fog()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();
        var (start, _) = await WalkAsync(Cancel, api, client, Square(NewArea(), 0, 0, 100)); // телефон так и не завершил забег
        var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(start.StartedAtMs);

        // Начат 20 минут назад: предел длины (4 ч) и сутки сверху истекают через 27 ч 40 мин.
        api.Time.Advance(TimeSpan.FromHours(27));
        await CloseForgottenAsync(api);
        Assert.Equal(RunStatus.Active, (await RunAsync(start.Id)).Status);

        api.Time.Advance(TimeSpan.FromHours(1));
        await CloseForgottenAsync(api);

        var run = await RunAsync(start.Id);
        Assert.Equal(RunStatus.Abandoned, run.Status);
        Assert.Equal(startedAt + TimeSpan.FromHours(4), run.EndedAt);
        await using var scope = api.Services.CreateAsyncScope();
        var fog = scope.ServiceProvider.GetRequiredService<FogProcessor>();
        Assert.Contains(start.Id, await fog.RunsReadyAsync(1_000, Cancel));
        Assert.True(await fog.StampRunAsync(start.Id, Cancel) > 0);
    }

    private static async Task PurgeAllAsync(ApiFactory api)
    {
        // Куски других тестов в той же базе тоже могли состариться — стираем пачками до конца.
        while (true)
        {
            await using var scope = api.Services.CreateAsyncScope();
            if (await scope.ServiceProvider.GetRequiredService<RunRetention>().PurgeRawPointsAsync(CancellationToken.None) == 0)
            {
                return;
            }
        }
    }

    private static async Task CloseForgottenAsync(ApiFactory api)
    {
        // Незавершённые забеги других тестов тоже «забыты» — закрываем пачками до конца.
        while (true)
        {
            await using var scope = api.Services.CreateAsyncScope();
            if (await scope.ServiceProvider.GetRequiredService<RunRetention>().CloseForgottenAsync(CancellationToken.None) == 0)
            {
                return;
            }
        }
    }

    private async Task<DateTimeOffset?> PurgedAtAsync(Guid runId)
    {
        await using var db = database.CreateContext();
        return await db.Runs.Where(r => r.Id == runId).Select(r => r.PointsPurgedAt).SingleAsync(Cancel);
    }

    private async Task<RunEntity> RunAsync(Guid runId)
    {
        await using var db = database.CreateContext();
        return await db.Runs.AsNoTracking().SingleAsync(r => r.Id == runId, Cancel);
    }
}
