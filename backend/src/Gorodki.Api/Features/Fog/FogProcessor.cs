using Gorodki.Api.Features.Runs;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Fog;
using Gorodki.Domain.Leagues;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Fog;

/// <summary>
/// Туман «Исследования» на сервере (PLAN.md, §3.10, §7.3): забег открывает клетки один раз — когда он завершён и все его точки
/// на месте (или давно закрыт сервером). Клетки — по проверенным судьёй точкам (<see cref="FogStamp"/>), запись — одной
/// короткой транзакцией под блокировкой игрока; новые клетки считаются ровно один раз.
/// </summary>
public sealed class FogProcessor(AppDbContext db, RunJudgements judgements, TimeProvider time)
{
    /// <summary>Закрытый сервером забег, который телефон так и не завершил, открывает туман через сутки — тем, что успело прийти.</summary>
    public static readonly TimeSpan ClosedRunGrace = TimeSpan.FromDays(1);

    /// <summary>Забеги, готовые открыть туман, — сначала закончившиеся раньше.</summary>
    public Task<List<Guid>> RunsReadyAsync(int take, CancellationToken cancellationToken)
    {
        var closedBefore = time.GetUtcNow() - ClosedRunGrace;
        return db.Runs.AsNoTracking()
            .Where(r => r.FogStampedAt == null
                && r.PrefixEndSeq >= 0
                && ((r.Status == RunStatus.Finished && r.LastSeq != null && r.PrefixEndSeq >= r.LastSeq)
                    || (r.Status != RunStatus.Active && r.EndedAt < closedBefore)))
            .OrderBy(r => r.EndedAt)
            .Select(r => r.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Открывает туман забега. Возвращает число новых клеток или null, если забег уже открывал туман.</summary>
    public async Task<int?> StampRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null || run.FogStampedAt is not null || run.PrefixEndSeq < 0)
        {
            return null;
        }

        var (rules, judgement) = await judgements.JudgeAsync(run, cancellationToken);
        var stamp = FogStamp.Of(judgement.Points, judgement.Verdicts, run.League, rules.Exploration);
        var layer = run.League == League.Bike ? FogLayerKind.Bike : FogLayerKind.Foot;
        var now = time.GetUtcNow();

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '15s'", cancellationToken);
        var userKey = run.UserId.ToString();
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(2, hashtext({userKey}))", cancellationToken);

        var keys = stamp.Tiles.Keys.ToList();
        var stored = new List<FogTileEntity>();
        if (keys.Count > 0)
        {
            int minX = keys.Min(k => k.X), maxX = keys.Max(k => k.X), minY = keys.Min(k => k.Y), maxY = keys.Max(k => k.Y);
            stored = await db.FogTiles
                .Where(f => f.UserId == run.UserId
                    && f.Layer == layer
                    && f.Season == 0
                    && f.TileX >= minX && f.TileX <= maxX
                    && f.TileY >= minY && f.TileY <= maxY)
                .ToListAsync(cancellationToken);
        }

        var newCells = 0;
        foreach (var (key, bits) in stamp.Tiles)
        {
            var entity = stored.SingleOrDefault(f => f.TileX == key.X && f.TileY == key.Y);
            if (entity is null)
            {
                newCells += bits.Count;
                db.FogTiles.Add(new FogTileEntity
                {
                    UserId = run.UserId,
                    Layer = layer,
                    Season = 0,
                    TileX = key.X,
                    TileY = key.Y,
                    Bits = FogTileCodec.Compress(bits),
                    CellCount = bits.Count,
                    Version = 1,
                    UpdatedAt = now,
                });
                continue;
            }

            var old = FogTileCodec.Decompress(entity.Bits);
            var added = bits.NewCount(old);
            if (added == 0)
            {
                continue; // тайл не изменился — версию не трогаем, приложение его не перекачает
            }

            newCells += added;
            old.UnionWith(bits);
            entity.Bits = FogTileCodec.Compress(old);
            entity.CellCount = old.Count;
            entity.Version++;
            entity.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        var marked = await db.Runs
            .Where(r => r.Id == runId && r.FogStampedAt == null)
            .ExecuteUpdateAsync(
                set => set.SetProperty(r => r.FogStampedAt, now).SetProperty(r => r.FogNewCells, newCells),
                cancellationToken);
        if (marked == 0)
        {
            await transaction.RollbackAsync(cancellationToken); // другой проход успел раньше
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        return newCells;
    }
}
