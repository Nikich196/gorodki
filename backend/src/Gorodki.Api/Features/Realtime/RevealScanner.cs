using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Realtime;

/// <summary>
/// Раскрытие чужих захватов в реальном времени: когда граница публичности (<see cref="TerritoryReader.PublicHorizon"/>,
/// «сейчас − 20 минут» вниз до 5 минут) сдвигается, всем, кто смотрит лигу, уходит «тайлы изменились» — по тем захватам,
/// что стали публичными. Раньше этой границы о чужом захвате не сообщает ничто (PLAN.md, §3.16).
/// </summary>
/// <remarks>
/// Источник — журнал захватов (там лига, тайл и время применения), отдельная таблица событий не нужна. Граница хранится
/// в памяти: после перезапуска сервера раскрытия за время простоя подсказкой не придут — приложение пересинхронизируется
/// по версиям тайлов при подключении и изредка опрашивает карту само.
/// </remarks>
public sealed class RevealScanner(IServiceScopeFactory scopes, RealtimeHints hints, TimeProvider time)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _previous;

    /// <summary>Сообщает о раскрытиях с прошлой границы. Возвращает число тайлов (тесты зовут напрямую).</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var configs = scope.ServiceProvider.GetRequiredService<GameConfigStore>();
            var delay = TimeSpan.FromMinutes((await configs.GetCurrentAsync(cancellationToken)).Rules.Privacy.PublicEventDelayMinutes);
            var horizon = TerritoryReader.PublicHorizon(time.GetUtcNow(), delay);
            if (_previous is not { } previous || horizon <= previous)
            {
                _previous ??= horizon;
                return 0;
            }

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var revealed = await db.CaptureJournal.AsNoTracking()
                .Where(j => j.AppliedAt > previous && j.AppliedAt <= horizon)
                .Select(j => new { j.League, j.TileX, j.TileY })
                .Distinct()
                .ToListAsync(cancellationToken);
            foreach (var league in revealed.GroupBy(r => r.League))
            {
                hints.TilesChanged(league.Key, league.Select(r => new TileKey(r.TileX, r.TileY)));
            }

            _previous = horizon;
            return revealed.Count;
        }
        finally
        {
            _gate.Release();
        }
    }
}
