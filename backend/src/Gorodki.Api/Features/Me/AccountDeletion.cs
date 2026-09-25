using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Realtime;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Me;

/// <summary>
/// Удаление аккаунта (PLAN.md, §3.16, закон 99-З: «удаление ≤15 дней»). Игрок просит удалить аккаунт (<c>DELETE /me</c>) —
/// с этого момента его данные не принимаются, а фоновый обработчик при ближайшем часовом проходе стирает всё: землю,
/// забеги с точками, захваты с журналом, туман, токены и сам аккаунт.
/// </summary>
/// <remarks>
/// Земля удаляется под блокировками тайлов (как захват) и становится ничьей; версии тайлов растут — у соседей карта
/// обновится, и там, где его землю отнял ещё скрытый чужой захват (остальным проекция её показывала). В журнале захватов
/// других игроков (земля до/после их захватов) номер удалённого остаётся, пока журнал не сотрётся — через 7 дней, раньше
/// срока закона; откат и публичная проекция такую землю возвращают ничьей. Строки точного отката
/// (<c>capture_journal_parcels</c>) чистятся сразу, как земля: иначе точный откат скрытого чужого захвата не сошёлся бы с
/// кусками.
/// </remarks>
public sealed class AccountDeletion(
    AppDbContext db, GameConfigStore configs, RealtimeHints hints, TimeProvider time, ILogger<AccountDeletion> logger)
{
    /// <summary>Срок по закону; фактически аккаунт стирается при ближайшем часовом проходе.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromDays(15);

    private const int Batch = 20;

    /// <summary>
    /// Стирает аккаунты, удаление которых запрошено. Не раньше, чем запрос и последний захват станут публичными
    /// (<see cref="TerritoryReader.PublicHorizon"/>: «сейчас − 20 минут» вниз до 5 минут, §3.16): стёртая земля становится
    /// ничьей, и контур недавней петли, ещё скрытой от остальных, иначе проступил бы на карте. Граница — та же, что у
    /// публичной проекции: «просто 20 минут» опережали бы её до 5 минут. Возвращает, сколько стёрто.
    /// </summary>
    public async Task<int> ProcessRequestedAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var delay = TimeSpan.FromMinutes((await configs.GetCurrentAsync(cancellationToken)).Rules.Privacy.PublicEventDelayMinutes);
        var publicBefore = TerritoryReader.PublicHorizon(now, delay);
        var requested = await db.Users.AsNoTracking()
            .Where(u => u.DeletionRequestedAt != null && u.DeletionRequestedAt <= publicBefore
                && !db.Captures.Any(c => c.UserId == u.Id && c.AppliedAt > publicBefore))
            .OrderBy(u => u.DeletionRequestedAt)
            .Select(u => u.Id)
            .Take(Batch)
            .ToListAsync(cancellationToken);
        var deleted = 0;
        foreach (var userId in requested)
        {
            try
            {
                deleted += await DeleteAsync(userId, cancellationToken) ? 1 : 0;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Следующий часовой проход попробует снова; до срока в 15 дней попыток сотни.
                logger.LogError(e, "Аккаунт {UserId} не удалён", userId);
            }
        }

        return deleted;
    }

    /// <summary>Стирает один аккаунт целиком одной транзакцией. False — аккаунта уже нет.</summary>
    public async Task<bool> DeleteAsync(Guid userId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '60s'", cancellationToken);

        // Та же блокировка игрока, что у захвата и отката: его захват не применится посреди удаления.
        var userKey = userId.ToString();
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(1, hashtext({userKey}))", cancellationToken);
        if (!await db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            return false;
        }

        // Земля: блокировки тайлов по порядку (как у захвата), куски — прочь, версии тайлов — вперёд.
        var owned = (await db.Parcels
                .Where(p => p.OwnerId == userId)
                .Select(p => new { p.League, p.TileX, p.TileY })
                .Distinct()
                .ToListAsync(cancellationToken))
            .Select(t => (t.League, Tile: new TileKey(t.TileX, t.TileY)))
            .ToList();

        // Тайлы, где его землю отнял ещё скрытый чужой захват: кусков его в parcels там нет, но проекция возвращает их
        // остальным — по строкам точного отката или по земле «до» в журнале. После удаления она их не вернёт, и тайл у
        // остальных изменится — версия должна вырасти, как выросла бы без захвата, где его земля лежала бы в parcels.
        // Иначе клиенты с кэшем до раскрытия показывали бы землю удалённого аккаунта, свежие запросы — нет, а этот тайл —
        // единственный его тайл без новой версии — выдал бы, где скрытый захват. Подъём версии скрытое не выдаёт: без
        // захвата его земля там была видна всем, и удаление подняло бы версию так же. Искать хватает земли «до» в журнале:
        // она есть у каждого его куска, который захват изменил в следе; кусок, переписанный вне следа (сосед с изломом,
        // аудит BE-01), остаётся его и лежит в parcels — этот тайл уже среди owned.
        var delay = TimeSpan.FromMinutes((await configs.GetCurrentAsync(cancellationToken)).Rules.Privacy.PublicEventDelayMinutes);
        var horizon = TerritoryReader.PublicHorizon(time.GetUtcNow(), delay);
        var restored = (await db.CaptureJournalPieces
                .Where(p => p.OwnerId == userId && !p.After)
                .Join(
                    db.CaptureJournal.Where(j => j.AppliedAt > horizon),
                    p => new { p.CaptureId, p.TileX, p.TileY },
                    j => new { j.CaptureId, j.TileX, j.TileY },
                    (p, j) => new { j.League, j.TileX, j.TileY })
                .Distinct()
                .ToListAsync(cancellationToken))
            .Select(t => (t.League, Tile: new TileKey(t.TileX, t.TileY)));
        var tiles = owned.Concat(restored).Distinct().ToList();

        // Под блокировку — и тайлы, где его номер есть только в чужих кусках (в списке снявших уровень,
        // ParcelState.LossAttackers). Иначе захват в таком тайле, прочитавший кусок с его номером до чистки ниже, записал бы
        // новые куски и строки журнала с этим номером уже после неё — номер удалённого вернулся бы в базу. Под блокировкой
        // захват и удаление в тайле идут строго по очереди. Номер переходит только в куски того же тайла — других нет.
        var listed = (await db.Parcels
                .Where(p => p.LossAttackers.Contains(userId))
                .Select(p => new { p.League, p.TileX, p.TileY })
                .Distinct()
                .ToListAsync(cancellationToken))
            .Select(t => (t.League, Tile: new TileKey(t.TileX, t.TileY)));
        foreach (var (league, tile) in tiles.Concat(listed).Distinct().OrderBy(t => t.League).ThenBy(t => t.Tile))
        {
            var lockSpace = 100 + (int)league;
            var lockKey = tile.LockKey;
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({lockSpace}, {lockKey})", cancellationToken);
        }

        await db.Parcels.Where(p => p.OwnerId == userId).ExecuteDeleteAsync(cancellationToken);
        // Номер игрока хранится и в чужих участках — в списке снявших уровень за окно (ParcelState.LossAttackers) и в его
        // копиях в журнале захватов: закон 99-З требует стереть и его.
        await db.Database.ExecuteSqlAsync(
            $"UPDATE app.parcels SET loss_attackers = array_remove(loss_attackers, {userId}) WHERE {userId} = ANY(loss_attackers)",
            cancellationToken);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE app.capture_journal_pieces SET loss_attackers = array_remove(loss_attackers, {userId}) WHERE {userId} = ANY(loss_attackers)",
            cancellationToken);

        // Строки точного отката (публичная проекция скрытых чужих захватов, аудит BE-01) — так же, как земля: строки его
        // кусков прочь, его номер — из списков. Обмен строк остаётся согласованным: его кусков нет ни в parcels, ни среди
        // удалённых захватом строк, и проекция вернёт на их месте ничью землю — как в мире без захвата. Иначе номер
        // вставленного куска совпал бы, а состояние (список снявших) — нет, и точный откат ушёл бы в запасной путь.
        await db.CaptureJournalParcels.Where(r => r.OwnerId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE app.capture_journal_parcels SET loss_attackers = array_remove(loss_attackers, {userId}) WHERE {userId} = ANY(loss_attackers)",
            cancellationToken);
        foreach (var (league, tile) in tiles)
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO app.tile_versions (league, tile_x, tile_y, version) VALUES ({(short)league}, {tile.X}, {tile.Y}, 1)
                ON CONFLICT (league, tile_x, tile_y) DO UPDATE SET version = app.tile_versions.version + 1
                """,
                cancellationToken);
        }

        // Захваты не удаляются вместе с забегом (история), поэтому — явно; журнал уходит с ними каскадом. Остальное
        // (забеги с точками, туман, токены, задания отката) удаляется каскадом вместе с аккаунтом.
        await db.Captures.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        foreach (var league in tiles.GroupBy(t => t.League))
        {
            hints.TilesChanged(league.Key, league.Select(t => t.Tile));
        }

        logger.LogInformation("Аккаунт {UserId} удалён: земля в {Tiles} тайлах стала ничьей", userId, owned.Count);
        return true;
    }
}
