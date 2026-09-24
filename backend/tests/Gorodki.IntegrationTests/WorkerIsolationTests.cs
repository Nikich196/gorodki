using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Admin;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Изоляция фонового обработчика на настоящей базе: забег, который не судится (испорченный кусок), стоит первым во всех
/// очередях — и всё равно не мешает ни туману других забегов, ни откатам, а сам откладывается и не держит очередь.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class WorkerIsolationTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Poisoned_run_is_postponed_and_the_rest_of_the_pass_still_runs()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        var (_, veraId) = await api.CreatePlayerClientAsync();
        var (admin, _) = await api.CreatePlayerClientAsync(UserRole.Admin);

        // У Анны — завершённый забег с заявкой петли, кусок которого испорчен (ручная правка базы, смена формата).
        var poisoned = await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100));
        await FinishAsync(anna, poisoned.RunId, poisoned.EndMs, api);
        // У Бориса — обычный завершённый забег; у Веры — задание отката (захватов у неё нет — выполнится сразу).
        var healthy = await WalkAndFinishAsync(Cancel, api, boris, Square(NewArea(), 0, 0, 100));
        var rollback = await admin.PostAsJsonAsync($"/admin/users/{veraId}/rollback", new RollbackRequest("тест: изоляция"), Json, Cancel);
        Assert.Equal(HttpStatusCode.Accepted, rollback.StatusCode);
        var job = (await rollback.Content.ReadFromJsonAsync<RollbackResponse>(Json, Cancel))!;

        // Испорченный забег — первый во всех очередях (раньше всех закончился, его заявка ждёт дольше всех).
        var first = api.Time.GetUtcNow() - TimeSpan.FromDays(13);
        await using (var db = database.CreateContext())
        {
            await db.RunChunks
                .Where(c => c.RunId == poisoned.RunId)
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.Points, new byte[] { 0xFF, 0xFF }), Cancel);
            await db.Runs.Where(r => r.Id == poisoned.RunId).ExecuteUpdateAsync(set => set.SetProperty(r => r.EndedAt, first), Cancel);
            await db.Runs.Where(r => r.Id == healthy.Id).ExecuteUpdateAsync(set => set.SetProperty(r => r.EndedAt, first.AddSeconds(1)), Cancel);
            await db.Captures.Where(c => c.Id == poisoned.CaptureId).ExecuteUpdateAsync(set => set.SetProperty(c => c.ReceivedAt, first), Cancel);
            await db.CaptureRollbacks.Where(r => r.Id == job.Id).ExecuteUpdateAsync(set => set.SetProperty(r => r.RequestedAt, first), Cancel);
        }

        var worker = ActivatorUtilities.CreateInstance<CaptureWorker>(api.Services);
        await worker.RunPassAsync(Cancel);

        // Остальной проход прошёл: туман другого забега открыт, откат выполнен.
        var now = api.Time.GetUtcNow();
        await using (var db = database.CreateContext())
        {
            Assert.NotNull((await RunAsync(db, healthy.Id)).FogStampedAt);
            Assert.Equal(CaptureRollbackStatus.Done, (await db.CaptureRollbacks.AsNoTracking().SingleAsync(r => r.Id == job.Id, Cancel)).Status);

            // Испорченный забег отложен: туман и визиты — на паузе, заявка — в аренде с записанной ошибкой.
            var run = await RunAsync(db, poisoned.RunId);
            Assert.Equal((1, 1), (run.FogFailures, run.VisitsFailures));
            Assert.True(run.FogRetryAt > now && run.VisitsRetryAt > now);
            Assert.Null(run.FogStampedAt);
            var claim = await ClaimAsync(db, poisoned.CaptureId);
            Assert.Equal((CaptureStatus.Pending, 1), (claim.Status, claim.Attempts));
            Assert.True(claim.LeaseUntil > now);
            Assert.NotNull(claim.LastError);
        }

        // Следующий проход его не трогает: пауза ещё не прошла.
        await worker.RunPassAsync(Cancel);
        await using (var db = database.CreateContext())
        {
            var run = await RunAsync(db, poisoned.RunId);
            Assert.Equal((1, 1), (run.FogFailures, run.VisitsFailures));
            Assert.Equal(1, (await ClaimAsync(db, poisoned.CaptureId)).Attempts);
        }

        // Заявка не висит вечно: после пяти аренд она получает окончательный итог.
        for (var lease = 2; lease <= CaptureProcessor.MaxAttempts + 1; lease++)
        {
            api.Time.Advance(CaptureProcessor.Lease + TimeSpan.FromSeconds(1));
            await worker.RunPassAsync(Cancel);
        }

        await using (var db = database.CreateContext())
        {
            var claim = await ClaimAsync(db, poisoned.CaptureId);
            Assert.Equal((CaptureStatus.Failed, "too_many_attempts"), (claim.Status, claim.RejectCode));
            Assert.True((await RunAsync(db, poisoned.RunId)).FogFailures > 1); // туман пробуется снова, всё реже
        }
    }

    private async Task FinishAsync(HttpClient client, Guid runId, long endMs, ApiFactory api)
    {
        await using var db = database.CreateContext();
        var lastSeq = await db.Runs.Where(r => r.Id == runId).Select(r => r.PrefixEndSeq).SingleAsync(Cancel);
        var finish = await client.PostAsJsonAsync(
            $"/runs/{runId}/finish", new FinishRunRequest(endMs, lastSeq, api.Time.GetUtcNow().ToUnixTimeMilliseconds()), Json, Cancel);
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
    }

    private Task<RunEntity> RunAsync(AppDbContext db, Guid runId) =>
        db.Runs.AsNoTracking().SingleAsync(r => r.Id == runId, Cancel);

    private Task<CaptureEntity> ClaimAsync(AppDbContext db, Guid captureId) =>
        db.Captures.AsNoTracking().SingleAsync(c => c.Id == captureId, Cancel);
}
