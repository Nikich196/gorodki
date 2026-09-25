using System.Threading.Channels;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Fog;
using Gorodki.Api.Features.Leaderboards;
using Gorodki.Api.Features.Me;
using Gorodki.Api.Features.Realtime;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Captures;

/// <summary>Сигнал «у забега что-то изменилось» (кусок, заявка, завершение) — обработчик проснётся раньше очередного опроса.</summary>
public sealed class CaptureSignal
{
    private readonly Channel<Guid> _runs = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public void Notify(Guid runId) => _runs.Writer.TryWrite(runId);

    /// <summary>Ждёт сигнала не дольше <paramref name="timeout"/>; накопившиеся сигналы сбрасывает — проход всё равно смотрит все забеги.</summary>
    public async Task WaitAsync(TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await _runs.Reader.WaitToReadAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // вышло время — обычный опрос
        }

        while (_runs.Reader.TryRead(out _))
        {
        }
    }
}

/// <summary>
/// Фоновый обработчик захватов, визитов, тумана и откатов: один поток (ADR 0003), опрос раз в 5 секунд или по сигналу. Каждый забег — в своей области DI.
/// Два экземпляра сервера во время деплоя не мешают друг другу: заявки берутся в аренду.
/// </summary>
/// <remarks>
/// Ошибка одного забега, заявки или задания не останавливает остальное: каждая стадия прохода и каждый забег в ней
/// обрабатываются отдельно. Иначе один испорченный забег стоял бы первым в очереди и каждые 5 секунд обрывал проход —
/// туман, визиты, раскрытия и откаты не делались бы ни у кого. Упавший забег откладывается: у заявок пауза — их аренда
/// (<see cref="CaptureProcessor.Lease"/>) со счётчиком попыток, у тумана и визитов — <see cref="RetryDelay"/>.
/// </remarks>
public sealed class CaptureWorker(
    IServiceScopeFactory scopes, CaptureSignal signal, RevealScanner reveals, TimeProvider time, ILogger<CaptureWorker> logger)
    : BackgroundService
{
    public const string EnabledSetting = "Captures:BackgroundWorker";

    /// <summary>Самая длинная пауза перед повтором забега, который не посчитался.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(1);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Раз в час: стирается журнал захватов старше недели (строки точного отката — через три-четыре часа, когда захват уже
    /// не может быть скрыт, <see cref="CaptureProcessor.ExactUndoRetention"/>), сырые точки старше 14 дней и истёкшие
    /// токены входа, закрываются
    /// забытые забеги, стираются аккаунты, удаление которых запрошено, раз в игровые сутки — срез рейтингов.
    /// </summary>
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var prunedAt = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (time.GetUtcNow() - prunedAt >= PruneInterval)
            {
                await HousekeepingAsync(stoppingToken);
                // И после ошибки: упавшая чистка повторится через час, а не будет начинать каждый проход раз в 5 секунд.
                prunedAt = time.GetUtcNow();
            }

            await RunPassAsync(stoppingToken);
            await signal.WaitAsync(PollInterval, time, stoppingToken);
        }
    }

    /// <summary>Часовая чистка. Каждый шаг — отдельно: ошибка одного не отменяет остальные.</summary>
    public async Task HousekeepingAsync(CancellationToken cancellationToken)
    {
        await StageAsync("журнал захватов", () => InScopeAsync<CaptureProcessor>(p => p.PruneJournalAsync(cancellationToken)), logger, cancellationToken);
        // Забытые забеги — до тумана и визитов прохода: закрытый забег сразу готов к ним.
        await StageAsync("забытые забеги", () => InScopeAsync<RunRetention>(r => r.CloseForgottenAsync(cancellationToken)), logger, cancellationToken);
        await StageAsync("сырые точки", () => InScopeAsync<RunRetention>(r => r.PurgeRawPointsAsync(cancellationToken)), logger, cancellationToken);
        await StageAsync("токены входа", () => InScopeAsync<RefreshTokenRetention>(r => r.PurgeExpiredAsync(cancellationToken)), logger, cancellationToken);
        await StageAsync("удаление аккаунтов", () => InScopeAsync<AccountDeletion>(d => d.ProcessRequestedAsync(cancellationToken)), logger, cancellationToken);
        await StageAsync("срез рейтингов", () => InScopeAsync<LeaderboardSnapshots>(s => s.TakeIfDueAsync(cancellationToken)), logger, cancellationToken);
    }

    /// <summary>Один проход обработчика (тесты зовут напрямую): стадии по порядку, каждая отдельно.</summary>
    public async Task RunPassAsync(CancellationToken cancellationToken)
    {
        // Захваты. Пауза упавшей заявки — её аренда, поэтому отдельно откладывать забег не нужно.
        await StageAsync(
            "захваты",
            async () => await EachRunAsync(
                "захваты",
                await RunsWithWorkAsync(cancellationToken),
                id => InScopeAsync<CaptureProcessor>(p => p.ProcessRunAsync(id, cancellationToken)),
                _ => Task.CompletedTask,
                logger,
                cancellationToken),
            logger,
            cancellationToken);

        // Визиты — после захватов: забег освежает свою землю один раз, когда завершён и все точки на месте.
        await StageAsync(
            "визиты",
            async () => await EachRunAsync(
                "визиты",
                await InScopeAsync<VisitProcessor, List<Guid>>(v => v.RunsReadyAsync(20, cancellationToken)),
                id => InScopeAsync<VisitProcessor>(v => v.ProcessRunAsync(id, cancellationToken)),
                id => InScopeAsync<VisitProcessor>(v => v.PostponeAsync(id, cancellationToken)),
                logger,
                cancellationToken),
            logger,
            cancellationToken);

        // Туман — после захватов: забег открывает его один раз, когда завершён и все точки на месте.
        await StageAsync(
            "туман",
            async () => await EachRunAsync(
                "туман",
                await InScopeAsync<FogProcessor, List<Guid>>(f => f.RunsReadyAsync(20, cancellationToken)),
                id => InScopeAsync<FogProcessor>(f => f.StampRunAsync(id, cancellationToken)),
                id => InScopeAsync<FogProcessor>(f => f.PostponeAsync(id, cancellationToken)),
                logger,
                cancellationToken),
            logger,
            cancellationToken);

        // Чужие захваты, ставшие публичными, — подсказка «тайлы изменились» (реальное время, §3.16).
        await StageAsync("раскрытия", () => reveals.RunOnceAsync(cancellationToken), logger, cancellationToken);

        // Откаты — в том же потоке, что и захваты: движок участков однопоточный (ADR 0003).
        await StageAsync("откаты", () => InScopeAsync<CaptureRollback>(r => r.ProcessPendingAsync(cancellationToken)), logger, cancellationToken);
    }

    /// <summary>
    /// Стадия прохода: её ошибка пишется в лог и дальше не идёт — следующие стадии выполняются. Остановку сервера
    /// (отмену <paramref name="cancellationToken"/>) не глотает.
    /// </summary>
    public static async Task StageAsync(string stage, Func<Task> work, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await work();
        }
        catch (Exception e) when (!IsStopping(e, cancellationToken))
        {
            logger.LogError(e, "Стадия «{Stage}» фонового обработчика не завершилась", stage);
        }
    }

    /// <summary>
    /// Забеги стадии — по одному: ошибка забега пишется в лог, забег откладывается (<paramref name="postpone"/>), остальные
    /// обрабатываются в этом же проходе. Возвращает, сколько забегов не обработано.
    /// </summary>
    public static async Task<int> EachRunAsync(
        string stage,
        IReadOnlyList<Guid> runIds,
        Func<Guid, Task> process,
        Func<Guid, Task> postpone,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var failed = 0;
        foreach (var runId in runIds)
        {
            try
            {
                await process(runId);
            }
            catch (Exception e) when (!IsStopping(e, cancellationToken))
            {
                failed++;
                logger.LogError(e, "Стадия «{Stage}»: забег {RunId} не обработан и отложен", stage, runId);
                try
                {
                    await postpone(runId);
                }
                catch (Exception postponeError) when (!IsStopping(postponeError, cancellationToken))
                {
                    // Отложить не вышло (например, база недоступна) — забег просто придёт в следующий проход.
                    logger.LogError(postponeError, "Стадия «{Stage}»: забег {RunId} не удалось отложить", stage, runId);
                }
            }
        }

        return failed;
    }

    /// <summary>
    /// Пауза перед повтором забега, который не посчитался <paramref name="failures"/> раз подряд: 1, 2, 4… минуты, не больше
    /// часа. Постоянная ошибка не занимает каждый проход, а временная (сбой базы) проходит сама за минуты. Из очереди
    /// такой забег уходит сам, когда через 14 дней стираются его точки.
    /// </summary>
    public static TimeSpan RetryDelay(int failures)
    {
        var delay = TimeSpan.FromMinutes(Math.Pow(2, Math.Clamp(failures, 1, 16) - 1));
        return delay < MaxRetryDelay ? delay : MaxRetryDelay;
    }

    /// <summary>Отмена при остановке сервера — не ошибка забега: её пропускаем наверх.</summary>
    private static bool IsStopping(Exception e, CancellationToken cancellationToken) =>
        e is OperationCanceledException && cancellationToken.IsCancellationRequested;

    private async Task InScopeAsync<TService>(Func<TService, Task> work)
        where TService : notnull
    {
        await using var scope = scopes.CreateAsyncScope();
        await work(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private async Task<TResult> InScopeAsync<TService, TResult>(Func<TService, Task<TResult>> work)
        where TService : notnull
    {
        await using var scope = scopes.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<TService>());
    }

    /// <summary>Забеги с ожидающими заявками, которые никто не держит, — сначала те, чья заявка ждёт дольше всех.</summary>
    private async Task<List<Guid>> RunsWithWorkAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow();
        return await db.Captures.AsNoTracking()
            .Where(c => c.Status == CaptureStatus.Pending && (c.LeaseUntil == null || c.LeaseUntil < now))
            .GroupBy(c => c.RunId)
            .OrderBy(g => g.Min(c => c.ReceivedAt))
            .Select(g => g.Key)
            .Take(20)
            .ToListAsync(cancellationToken);
    }
}
