using System.Threading.Channels;
using Gorodki.Api.Features.Fog;
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
/// Фоновый обработчик захватов и тумана: один поток (ADR 0003), опрос раз в 5 секунд или по сигналу. Каждый забег — в своей области DI.
/// Два экземпляра сервера во время деплоя не мешают друг другу: заявки берутся в аренду.
/// </summary>
public sealed class CaptureWorker(IServiceScopeFactory scopes, CaptureSignal signal, TimeProvider time, ILogger<CaptureWorker> logger)
    : BackgroundService
{
    public const string EnabledSetting = "Captures:BackgroundWorker";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var runId in await RunsWithWorkAsync(stoppingToken))
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<CaptureProcessor>().ProcessRunAsync(runId, stoppingToken);
                }

                // Туман — после захватов: забег открывает его один раз, когда завершён и все точки на месте.
                await using (var fogScope = scopes.CreateAsyncScope())
                {
                    var fog = fogScope.ServiceProvider.GetRequiredService<FogProcessor>();
                    foreach (var runId in await fog.RunsReadyAsync(20, stoppingToken))
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<FogProcessor>().StampRunAsync(runId, stoppingToken);
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Проход обработчика захватов не завершился");
            }

            await signal.WaitAsync(PollInterval, time, stoppingToken);
        }
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
