using Gorodki.Api.Features.Config;
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
/// обновится. В журнале захватов других игроков (земля до/после их захватов) номер удалённого остаётся, пока журнал
/// не сотрётся — через 7 дней, раньше срока закона; откат и публичная проекция такую землю возвращают ничьей.
/// </remarks>
public sealed class AccountDeletion(AppDbContext db, GameConfigStore configs, TimeProvider time, ILogger<AccountDeletion> logger)
{
    /// <summary>Срок по закону; фактически аккаунт стирается при ближайшем часовом проходе.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromDays(15);

    private const int Batch = 20;

    /// <summary>
    /// Стирает аккаунты, удаление которых запрошено. Не раньше чем через 20 минут после запроса и после последнего
    /// захвата (публичная задержка, §3.16): стёртая земля становится ничьей, и контур недавней петли, ещё скрытой
    /// от остальных, иначе проступил бы на карте. Возвращает, сколько стёрто.
    /// </summary>
    public async Task<int> ProcessRequestedAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var delay = TimeSpan.FromMinutes((await configs.GetCurrentAsync(cancellationToken)).Rules.Privacy.PublicEventDelayMinutes);
        var publicBefore = now - delay;
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
        var tiles = await db.Parcels
            .Where(p => p.OwnerId == userId)
            .Select(p => new { p.League, p.TileX, p.TileY })
            .Distinct()
            .ToListAsync(cancellationToken);
        foreach (var tile in tiles.OrderBy(t => t.League).ThenBy(t => new TileKey(t.TileX, t.TileY)))
        {
            var lockSpace = 100 + (int)tile.League;
            var lockKey = new TileKey(tile.TileX, tile.TileY).LockKey;
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({lockSpace}, {lockKey})", cancellationToken);
        }

        await db.Parcels.Where(p => p.OwnerId == userId).ExecuteDeleteAsync(cancellationToken);
        foreach (var tile in tiles)
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO app.tile_versions (league, tile_x, tile_y, version) VALUES ({(short)tile.League}, {tile.TileX}, {tile.TileY}, 1)
                ON CONFLICT (league, tile_x, tile_y) DO UPDATE SET version = app.tile_versions.version + 1
                """,
                cancellationToken);
        }

        // Захваты не удаляются вместе с забегом (история), поэтому — явно; журнал уходит с ними каскадом. Остальное
        // (забеги с точками, туман, токены, задания отката) удаляется каскадом вместе с аккаунтом.
        await db.Captures.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Аккаунт {UserId} удалён: земля в {Tiles} тайлах стала ничьей", userId, tiles.Count);
        return true;
    }
}
