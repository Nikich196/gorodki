using Gorodki.Domain.Leagues;
using Microsoft.AspNetCore.SignalR;

namespace Gorodki.Api.Features.Realtime;

/// <summary>
/// Отправка подсказок в хаб — своя фоновая служба, не поток захватов: отправка может ждать медленное соединение.
/// Подсказки за секунду склеиваются: тайлы одной лиги для одного адресата уходят одним сообщением.
/// </summary>
public sealed class RealtimePump(RealtimeHints hints, IHubContext<GameHub> hub, TimeProvider time, ILogger<RealtimePump> logger)
    : BackgroundService
{
    public const string EnabledSetting = "Realtime:Pump";

    private static readonly TimeSpan Coalesce = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await hints.Reader.WaitToReadAsync(stoppingToken))
            {
                await Task.Delay(Coalesce, time, stoppingToken);
                await DrainAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // остановка сервера
        }
    }

    /// <summary>Отправляет всё, что накопилось. Возвращает число отправленных сообщений (тесты зовут напрямую).</summary>
    public async Task<int> DrainAsync(CancellationToken cancellationToken)
    {
        var batch = new List<RealtimeHint>();
        while (hints.Reader.TryRead(out var hint))
        {
            batch.Add(hint);
        }

        var sent = 0;
        foreach (var group in batch.OfType<TilesChangedHint>().GroupBy(h => (h.League, h.UserId)))
        {
            var tiles = group.SelectMany(h => h.Tiles).Distinct().Order().Select(t => new[] { t.X, t.Y }).ToArray();
            var (league, userId) = group.Key;
            var target = userId is { } user ? hub.Clients.User(user.ToString()) : hub.Clients.Group(GameHub.Group(league));
            sent += await SendAsync(target, GameHub.TilesChanged, [Name(league), tiles], cancellationToken);
        }

        foreach (var decided in batch.OfType<CaptureDecidedHint>())
        {
            sent += await SendAsync(
                hub.Clients.User(decided.UserId.ToString()),
                GameHub.CaptureDecided,
                [decided.RunId, decided.CaptureId, decided.Status],
                cancellationToken);
        }

        return sent;
    }

    private async Task<int> SendAsync(IClientProxy target, string method, object?[] args, CancellationToken cancellationToken)
    {
        try
        {
            await target.SendCoreAsync(method, args, cancellationToken);
            return 1;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Подсказка потеряна — приложение пересинхронизируется само; остальные подсказки уходят.
            logger.LogWarning(e, "Подсказка {Method} не отправлена", method);
            return 0;
        }
    }

    private static string Name(League league) => league.ToString().ToLowerInvariant();
}
