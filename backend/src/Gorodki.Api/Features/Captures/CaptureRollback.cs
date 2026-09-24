using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Realtime;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Territory;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Gorodki.Api.Features.Captures;

/// <summary>
/// Откат захватов игрока (PLAN.md, §3.9, слой 5: «откатываются только куски, которых после этого никто не трогал»;
/// docs/architecture/captures.md). Выполняется фоновым обработчиком захватов — движок участков работает в одном потоке (ADR 0003).
/// </summary>
/// <remarks>
/// Игрок заморожен до постановки задания, а список его захватов составляется под его блокировкой — той же, под которой
/// применяются захваты: поэтому ни один его захват не ляжет на карту после составления списка. Захваты откатываются
/// от новых к старым, каждый отдельно: откат считается без блокировок, а записывается короткой транзакцией под
/// блокировками тайлов — и только если версии тайлов не изменились с момента чтения (иначе всё считается заново).
/// </remarks>
public sealed class CaptureRollback(
    AppDbContext db, GameConfigStore configs, RealtimeHints hints, TimeProvider time, ILogger<CaptureRollback> logger)
{
    /// <summary>Заморозка при откате — как в PLAN.md, §3.9, слой 5.</summary>
    public static readonly TimeSpan FreezeFor = TimeSpan.FromDays(7);

    /// <summary>Сколько раз пересчитывать откат захвата, если тайлы успели измениться.</summary>
    public const int MaxWriteAttempts = 5;

    /// <summary>Для тестов: вызывается между расчётом отката и записью (проверка «тайлы изменились — пересчитать»).</summary>
    public Func<CancellationToken, Task>? BeforeWrite { get; set; }

    private enum Outcome
    {
        RolledBack,
        AlreadyRolledBack,
        Conflict,
    }

    /// <summary>
    /// Выполняет ожидающие задания отката по порядку. Возвращает, сколько взято. Ошибка одного задания не мешает
    /// остальным: оно остаётся ожидающим и повторится в следующем проходе.
    /// </summary>
    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await db.CaptureRollbacks.AsNoTracking()
            .Where(r => r.Status == CaptureRollbackStatus.Pending)
            .OrderBy(r => r.RequestedAt)
            .Select(r => r.Id)
            .Take(5)
            .ToListAsync(cancellationToken);
        foreach (var id in pending)
        {
            try
            {
                await ProcessAsync(id, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Сюда доходят только ошибки базы, в том числе временный сбой при откате захвата (постоянная ошибка
                // отката одного захвата считается внутри задания).
                db.ChangeTracker.Clear();
                logger.LogError(e, "Задание отката {RollbackId} не выполнено — повтор в следующем проходе", id);
            }
        }

        return pending.Count;
    }

    /// <summary>Выполняет задание отката: все применённые и ещё не откаченные захваты игрока, от новых к старым.</summary>
    public async Task ProcessAsync(Guid rollbackId, CancellationToken cancellationToken)
    {
        var job = await db.CaptureRollbacks.AsNoTracking().SingleAsync(r => r.Id == rollbackId, cancellationToken);
        if (job.Status != CaptureRollbackStatus.Pending)
        {
            return;
        }

        var rolledBack = 0;
        var withoutJournal = 0;
        var failed = 0;
        var restoredArea = 0.0;
        var skippedArea = 0.0;
        string? lastError = null;

        foreach (var (captureId, league) in await CapturesOfAsync(job.UserId, cancellationToken))
        {
            try
            {
                // Журнал читается внутри try: испорченная запись (FormatException из TWKB) — неудача этого захвата,
                // а не всего задания, которое иначе стояло бы первым в очереди откатов навсегда.
                var changes = await CaptureJournal.LoadAsync(db, captureId, cancellationToken);
                if (changes.Count == 0)
                {
                    withoutJournal++; // старше недели или применён до журнала — откатывать не по чему
                    continue;
                }

                var (outcome, restored, skipped) = await RollBackAsync(captureId, league, changes, rollbackId, job.UserId, cancellationToken);
                switch (outcome)
                {
                    case Outcome.RolledBack:
                        rolledBack++;
                        restoredArea += restored;
                        skippedArea += skipped;
                        break;
                    case Outcome.Conflict:
                        failed++;
                        lastError = $"Захват {captureId}: тайлы менялись {MaxWriteAttempts} раз подряд.";
                        break;
                }
            }
            catch (Exception e) when (!DatabaseFailures.IsTransient(e)
                                      && (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
            {
                // Ошибка отката захвата — его неудача в итоге задания (failed, last_error): остальные захваты
                // откатываются, задание завершается, и администратор видит, что поставить откат нужно снова.
                // Временный сбой базы (соединение, пул, взаимоблокировка) — не неудача захвата: иначе минутный обрыв
                // связи завершил бы задание с неоткаченными захватами. Он уходит наверх, задание остаётся ожидающим и
                // повторяется в следующем проходе; уже откаченные захваты список второй раз не вернёт. Предела попыток
                // нет: чтение, которое на этих данных всегда упирается в тайм-аут клиента (30 с, вне statement_timeout
                // записи), повторялось бы каждый проход — для редкого задания администратора это приемлемо, в логе видно.
                db.ChangeTracker.Clear();
                failed++;
                lastError = Truncate($"Захват {captureId}: {e.Message}");
                logger.LogError(e, "Откат захвата {CaptureId} (задание {RollbackId}) не удался", captureId, rollbackId);
            }
        }

        // «Откачено» и «возвращено» — по базе, а не по счётчикам этого прохода: после временного сбоя задание повторяется,
        // и захваты, откаченные в прежних попытках, этот проход уже не видит. «Не тронуто» (skipped) отдельно у захвата
        // не хранится — оно за последнюю попытку.
        var ours = db.Captures.Where(c => c.RollbackId == rollbackId);
        rolledBack = await ours.CountAsync(cancellationToken);
        restoredArea = await ours.SumAsync(c => c.RolledBackArea ?? 0, cancellationToken);
        var finishedAt = time.GetUtcNow();
        await db.CaptureRollbacks
            .Where(r => r.Id == rollbackId && r.Status == CaptureRollbackStatus.Pending)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(r => r.Status, CaptureRollbackStatus.Done)
                    .SetProperty(r => r.FinishedAt, finishedAt)
                    .SetProperty(r => r.RolledBack, rolledBack)
                    .SetProperty(r => r.WithoutJournal, withoutJournal)
                    .SetProperty(r => r.Failed, failed)
                    .SetProperty(r => r.RestoredArea, Math.Round(restoredArea, 1))
                    .SetProperty(r => r.SkippedArea, Math.Round(skippedArea, 1))
                    .SetProperty(r => r.LastError, lastError),
                cancellationToken);
        logger.LogInformation(
            "Откат {RollbackId}: откачено {RolledBack}, без журнала {WithoutJournal}, не удалось {Failed}, возвращено {Restored:0} м²",
            rollbackId, rolledBack, withoutJournal, failed, restoredArea);
    }

    /// <summary>
    /// Применённые и не откаченные захваты игрока, от новых к старым. Список — под блокировкой игрока: захват, который
    /// применялся в этот момент, уже записан, а следующие увидят заморозку.
    /// </summary>
    private async Task<List<(Guid Id, League League)>> CapturesOfAsync(Guid userId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var userKey = userId.ToString();
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(1, hashtext({userKey}))", cancellationToken);
        var captures = await db.Captures.AsNoTracking()
            .Where(c => c.UserId == userId && c.Status == CaptureStatus.Applied && c.RolledBackAt == null)
            .OrderByDescending(c => c.AppliedSeq)
            .Select(c => new { c.Id, c.League })
            .ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return captures.Select(c => (c.Id, c.League)).ToList();
    }

    private async Task<(Outcome Outcome, double Restored, double Skipped)> RollBackAsync(
        Guid captureId, League league, IReadOnlyList<TileChange> changes, Guid rollbackId, Guid userId, CancellationToken cancellationToken)
    {
        var tiles = changes.Select(c => c.Tile).Distinct().Order().ToList();
        int minX = tiles.Min(t => t.X), maxX = tiles.Max(t => t.X), minY = tiles.Min(t => t.Y), maxY = tiles.Max(t => t.Y);

        // Землю нельзя вернуть тому, чьего аккаунта уже нет, — она становится ничьей (внешний ключ на владельца).
        var owners = changes.SelectMany(c => c.Before).Select(p => p.State.OwnerId).Distinct().ToList();
        var existing = (await db.Users.AsNoTracking().Where(u => owners.Contains(u.Id)).Select(u => u.Id).ToListAsync(cancellationToken))
            .ToHashSet();
        var rules = (await configs.GetCurrentAsync(cancellationToken)).Rules.Territory.ToRules(); // визиты — по тем же правилам

        for (var attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            // Расчёт — без блокировок: только чтение.
            db.ChangeTracker.Clear();
            var versions = await VersionsAsync(league, tiles, cancellationToken);
            var stored = (await db.Parcels.AsNoTracking()
                    .Where(p => p.League == league && p.TileX >= minX && p.TileX <= maxX && p.TileY >= minY && p.TileY <= maxY)
                    .ToListAsync(cancellationToken))
                .Where(p => tiles.Contains(new TileKey(p.TileX, p.TileY)))
                .ToList();
            var map = new TerritoryMap(rules, new SliverSettings());
            map.Load(stored.Select(CaptureProcessor.ToParcel));
            // Свои визиты нарушителя на отнятую землю касанием не считаются: побегав по ней, он бы её «отмыл». Визиты
            // других владельцев — тоже: жертва, пробежавшая по треснувшей части, получает её назад целой, с этим визитом
            // (иначе трещина и осада оставались бы у неё и после отката нарушителя).
            var result = map.Restore(
                changes,
                state => existing.Contains(state.OwnerId) ? state : null,
                (current, after) => current == after || (current?.OwnerId == userId && after?.OwnerId == userId),
                replayVisits: true);

            if (BeforeWrite is { } hook)
            {
                await hook(cancellationToken);
            }

            try
            {
                var outcome = await WriteAsync(captureId, league, tiles, versions, stored, map, result, rollbackId, cancellationToken);
                if (outcome != Outcome.Conflict)
                {
                    return outcome == Outcome.RolledBack ? (outcome, result.RestoredArea, result.SkippedArea) : (outcome, 0, 0);
                }
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                db.ChangeTracker.Clear(); // тайлы заняты дольше 5 с — посчитаем заново
            }
        }

        return (Outcome.Conflict, 0, 0);
    }

    /// <summary>
    /// Запись отката — короткая транзакция под блокировками тайлов, если тайлы не изменились с момента чтения.
    /// <see cref="Outcome.Conflict"/> — изменились (или куски пропали без новой версии, например с удалённым аккаунтом).
    /// </summary>
    private async Task<Outcome> WriteAsync(
        Guid captureId,
        League league,
        IReadOnlyList<TileKey> tiles,
        List<long> versions,
        List<ParcelEntity> stored,
        TerritoryMap map,
        RestoreResult result,
        Guid rollbackId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '15s'", cancellationToken);
        var lockSpace = 100 + (int)league;
        foreach (var tile in tiles)
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({lockSpace}, {tile.LockKey})", cancellationToken);
        }

        if (!(await VersionsAsync(league, tiles, cancellationToken)).SequenceEqual(versions))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Outcome.Conflict;
        }

        var changedTiles = new List<TileKey>();
        foreach (var tile in result.ChangedTiles)
        {
            var before = stored.Where(p => p.TileX == tile.X && p.TileY == tile.Y).ToList();
            var diff = ParcelDiff.Compute([.. before.Select(p => (p.Id, CaptureProcessor.ToParcel(p)))], map.ParcelsIn(tile));
            if (diff.IsEmpty)
            {
                continue;
            }

            changedTiles.Add(tile);
            var removed = diff.Removed.ToList();
            if (await db.Parcels.Where(p => removed.Contains(p.Id)).ExecuteDeleteAsync(cancellationToken) != removed.Count)
            {
                await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
                return Outcome.Conflict;
            }

            db.Parcels.AddRange(diff.Added.Select(p => CaptureProcessor.ToEntity(p, league)));
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var tile in changedTiles)
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO app.tile_versions (league, tile_x, tile_y, version) VALUES ({(short)league}, {tile.X}, {tile.Y}, 1)
                ON CONFLICT (league, tile_x, tile_y) DO UPDATE SET version = app.tile_versions.version + 1
                """,
                cancellationToken);
        }

        var now = time.GetUtcNow();
        var restored = Math.Round(result.RestoredArea, 1);
        var marked = await db.Captures
            .Where(c => c.Id == captureId && c.RolledBackAt == null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(c => c.RolledBackAt, now)
                    .SetProperty(c => c.RolledBackArea, restored)
                    .SetProperty(c => c.RollbackId, rollbackId),
                cancellationToken);
        if (marked == 0)
        {
            // Этот захват уже откатило другое задание (второй экземпляр сервера во время деплоя).
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return Outcome.AlreadyRolledBack;
        }

        await transaction.CommitAsync(cancellationToken);
        hints.TilesChanged(league, changedTiles); // откат — решение администратора, публичен сразу
        return Outcome.RolledBack;
    }

    /// <summary>Версии тайлов в порядке <paramref name="tiles"/> (у тайла без записи — 0).</summary>
    private async Task<List<long>> VersionsAsync(League league, IReadOnlyList<TileKey> tiles, CancellationToken cancellationToken)
    {
        var xs = tiles.Select(t => t.X).Distinct().ToList();
        var rows = await db.TileVersions.AsNoTracking()
            .Where(v => v.League == league && xs.Contains(v.TileX))
            .ToListAsync(cancellationToken);
        return tiles
            .Select(t => rows.FirstOrDefault(v => v.TileX == t.X && v.TileY == t.Y)?.Version ?? 0)
            .ToList();
    }

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500];
}
