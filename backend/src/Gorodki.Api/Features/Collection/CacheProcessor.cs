using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Fog;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Caches;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Collection;

/// <summary>
/// Находки тайников (PLAN.md, §3.12): «найти — пройти в 30 м в валидном забеге; подбор автоматический». Стадия фонового
/// обработчика по образцу тумана: один раз за забег — когда он завершён и все точки на месте (или давно закрыт сервером);
/// путь — принятый судьёй отрезков (<see cref="CacheRules"/>). Повтор забег не находит второй раз: отметка
/// <c>runs.caches_processed_at</c> ставится в той же транзакции, что и находки, а находка одна на (тайник, игрок).
/// </summary>
/// <remarks>
/// Находят только живые забеги: повтор записанного забега (демо) — нет, как и «Припасы» и дистанция. Другим находка видна
/// (номер «№ N нашедших») с <see cref="CacheFindEntity.VisibleAt"/> — границы публичности момента записи, у демо-аккаунта
/// сразу (<see cref="TerritoryReader.VisibleAtAsync"/>): иначе игрок у тайника по скачку номера узнал бы, что здесь
/// только что был другой. Своя находка видна сразу.
/// </remarks>
public sealed class CacheProcessor(AppDbContext db, RunJudgements judgements, TerritoryReader territory, TimeProvider time)
{
    /// <summary>Забеги, готовые к проверке тайников, — сначала закончившиеся раньше; упавший ждёт своей паузы.</summary>
    public Task<List<Guid>> RunsReadyAsync(int take, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var closedBefore = now - FogProcessor.ClosedRunGrace;
        return db.Runs.AsNoTracking()
            .Where(r => r.CachesProcessedAt == null
                && r.PointsPurgedAt == null
                && r.PrefixEndSeq >= 0
                && (r.CachesRetryAt == null || r.CachesRetryAt <= now)
                && ((r.Status == RunStatus.Finished && r.LastSeq != null && r.PrefixEndSeq >= r.LastSeq)
                    || (r.Status != RunStatus.Active && r.EndedAt < closedBefore)))
            .OrderBy(r => r.EndedAt)
            .Select(r => r.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Проверка забега не удалась: пауза <see cref="CaptureWorker.RetryDelay"/>, чтобы не стоять первым в очереди.</summary>
    public async Task PostponeAsync(Guid runId, CancellationToken cancellationToken)
    {
        var failures = await db.Runs.Where(r => r.Id == runId).Select(r => r.CachesFailures).SingleAsync(cancellationToken) + 1;
        var retryAt = time.GetUtcNow() + CaptureWorker.RetryDelay(failures);
        await db.Runs
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(
                set => set.SetProperty(r => r.CachesFailures, failures).SetProperty(r => r.CachesRetryAt, retryAt),
                cancellationToken);
    }

    /// <summary>Находит тайники забега. Возвращает, сколько новых, или null — забег уже проверен.</summary>
    public async Task<int?> ProcessRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null || run.CachesProcessedAt is not null || run.PrefixEndSeq < 0 || run.PointsPurgedAt is not null)
        {
            return null;
        }

        var found = new Dictionary<Guid, long>();
        var caches = new List<CacheEntity>();
        if (run.Source == RunSource.Live)
        {
            // Тайники, которых у игрока ещё нет: свои найденные второй раз не ищем.
            caches = await db.Caches.AsNoTracking()
                .Where(c => !db.CacheFinds.Any(f => f.CacheId == c.Id && f.UserId == run.UserId))
                .ToListAsync(cancellationToken);
            var (_, judgement) = await judgements.JudgeAsync(run, cancellationToken);
            found = CacheRules.Finds(
                judgement.Points, judgement.Verdicts, [.. caches.Select(c => new CacheSpot(c.Id, c.Latitude, c.Longitude))])
                .ToDictionary();
        }

        var now = time.GetUtcNow();
        var visibleAt = found.Count > 0 ? await territory.VisibleAtAsync(run.UserId, now, cancellationToken) : now;
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '15s'", cancellationToken);
        foreach (var (cacheId, timeMs) in found)
        {
            db.CacheFinds.Add(new CacheFindEntity
            {
                Id = Guid.NewGuid(),
                CacheId = cacheId,
                UserId = run.UserId,
                RunId = run.Id,
                FoundAt = DateTimeOffset.FromUnixTimeMilliseconds(timeMs - run.ClockSkewMs), // по часам сервера
                VisibleAt = visibleAt,
                SetVersion = caches.Single(c => c.Id == cacheId).SetVersion,
            });
        }

        // Находка одна на (тайник, игрок) — уникальный индекс. Если тот же тайник успел засчитать другой забег игрока (два
        // экземпляра сервера во время деплоя), запись упадёт, забег отложится и при повторе этого тайника уже не найдёт.
        await db.SaveChangesAsync(cancellationToken);
        var marked = await db.Runs
            .Where(r => r.Id == runId && r.CachesProcessedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.CachesProcessedAt, now), cancellationToken);
        if (marked == 0)
        {
            await transaction.RollbackAsync(cancellationToken); // другой проход успел раньше
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        return found.Count;
    }
}
