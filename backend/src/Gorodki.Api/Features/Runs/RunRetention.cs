using Gorodki.Api.Features.Config;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Runs;

/// <summary>
/// Хранение забегов (PLAN.md, §3.16: «сырые точки 14 дней»). Раз в час фоновый обработчик закрывает забытые забеги и стирает
/// точки забегов, начатых больше 14 дней назад.
/// </summary>
/// <remarks>
/// Стирается содержимое кусков — координаты и датчики. Номера точек, время первой и последней точки и отпечаток остаются:
/// по ним сервер по-прежнему отвечает, какие точки получены, и узнаёт повтор. Иначе телефон увидел бы, что точек
/// «не хватает», и стал бы досылать то, что сервер уже стёр. Туман, визиты и захваты к этому времени давно посчитаны.
/// </remarks>
public sealed class RunRetention(AppDbContext db, GameConfigStore configs, TimeProvider time)
{
    public static readonly TimeSpan RawPointsRetention = TimeSpan.FromDays(14);

    /// <summary>
    /// Забег, который телефон так и не завершил (приложение удалили, телефон потерялся), закрывается через сутки после
    /// предела длины — иначе он остался бы активным навсегда и не открыл бы туман.
    /// </summary>
    public static readonly TimeSpan ForgottenAfter = TimeSpan.FromDays(1);

    private const int Batch = 200;

    /// <summary>Закрывает забытые забеги: конец — предел длины из версии конфига, с которой забег начат.</summary>
    public async Task<int> CloseForgottenAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var candidates = await db.Runs.AsNoTracking()
            .Where(r => r.Status == RunStatus.Active && r.StartedAt < now - ForgottenAfter)
            .OrderBy(r => r.StartedAt)
            .Select(r => new { r.Id, r.StartedAt, r.ConfigVersion })
            .Take(Batch)
            .ToListAsync(cancellationToken);
        var closed = 0;
        foreach (var run in candidates)
        {
            var config = await configs.GetAsync(run.ConfigVersion, cancellationToken)
                ?? throw new InvalidOperationException($"Нет версии конфига {run.ConfigVersion}, с которой начат забег.");
            var endedAt = run.StartedAt + TimeSpan.FromHours(config.Rules.Capture.MaxRunHours);
            if (endedAt + ForgottenAfter > now)
            {
                continue;
            }

            // Телефон мог завершить забег прямо сейчас — тогда его завершение главнее.
            closed += await db.Runs
                .Where(r => r.Id == run.Id && r.Status == RunStatus.Active)
                .ExecuteUpdateAsync(
                    set => set.SetProperty(r => r.Status, RunStatus.Abandoned).SetProperty(r => r.EndedAt, (DateTimeOffset?)endedAt),
                    cancellationToken);
        }

        return closed;
    }

    /// <summary>Стирает сырые точки забегов, начатых больше 14 дней назад. Возвращает, у скольких забегов стёрты.</summary>
    public async Task<int> PurgeRawPointsAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var before = now - RawPointsRetention;
        var runIds = await db.Runs.AsNoTracking()
            .Where(r => r.PointsPurgedAt == null && r.StartedAt < before)
            .OrderBy(r => r.StartedAt)
            .Select(r => r.Id)
            .Take(Batch)
            .ToListAsync(cancellationToken);
        if (runIds.Count == 0)
        {
            return 0;
        }

        // Сначала отметка у забега: она берёт блокировку строк забегов и ждёт кусок, который принимается прямо сейчас,
        // а приём после неё уже не резервирует место (условие на отметку). Потом стираются все куски, видимые на этот момент.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Runs
            .Where(r => runIds.Contains(r.Id) && r.PointsPurgedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.PointsPurgedAt, now), cancellationToken);
        await db.RunChunks
            .Where(c => runIds.Contains(c.RunId))
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Points, Array.Empty<byte>()), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return runIds.Count;
    }
}
