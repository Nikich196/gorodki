using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Realtime;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Features.Seasons;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Fog;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Time;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Fog;

/// <summary>
/// Туман «Исследования» на сервере (PLAN.md, §3.10, §7.3): забег открывает клетки один раз — когда он завершён и все его точки
/// на месте (или давно закрыт сервером). Клетки — по проверенным судьёй точкам (<see cref="FogStamp"/>), запись — одной
/// короткой транзакцией под блокировкой игрока; новые клетки считаются ровно один раз.
/// Слоя два: за всё время и сезонный — сезон забега определяется по времени его начала (по часам сервера), а не по моменту
/// обработки: забег, начатый в 23:50 последнего дня сезона, весь относится к нему. Забеги до первого сезона — только «за всё время».
/// </summary>
public sealed class FogProcessor(
    AppDbContext db, RunJudgements judgements, SeasonStore seasons, RealtimeHints hints, TimeProvider time)
{
    /// <summary>Закрытый сервером забег, который телефон так и не завершил, открывает туман через сутки — тем, что успело прийти.</summary>
    public static readonly TimeSpan ClosedRunGrace = TimeSpan.FromDays(1);

    /// <summary>
    /// Следующая версия тайла: момент изменения по часам сервера (мс Unix), но не меньше прежней + 1. Версия не начинается
    /// заново с 1, когда стёртый тайл (<see cref="FogHistory"/>) открывается снова: у телефона, который ещё не узнал об
    /// очистке, в кэше осталась версия стёртого тайла, и новую, меньшую, он счёл бы старым ответом — новый туман не
    /// показался бы. У заново созданной строки прежней версии нет, поэтому это верно, пока часы сервера не идут назад.
    /// </summary>
    public static long NextVersion(long? previous, DateTimeOffset now) =>
        Math.Max((previous ?? 0) + 1, now.ToUnixTimeMilliseconds());

    /// <summary>
    /// Забеги, готовые открыть туман, — сначала закончившиеся раньше. Забег, туман которого не открылся из-за ошибки,
    /// ждёт своей паузы (<see cref="PostponeAsync"/>).
    /// </summary>
    public Task<List<Guid>> RunsReadyAsync(int take, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var closedBefore = now - ClosedRunGrace;
        return db.Runs.AsNoTracking()
            .Where(r => r.FogStampedAt == null
                && r.PointsPurgedAt == null
                && r.PrefixEndSeq >= 0
                && (r.FogRetryAt == null || r.FogRetryAt <= now)
                && ((r.Status == RunStatus.Finished && r.LastSeq != null && r.PrefixEndSeq >= r.LastSeq)
                    || (r.Status != RunStatus.Active && r.EndedAt < closedBefore)))
            .OrderBy(r => r.EndedAt)
            .Select(r => r.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Туман забега не открылся из-за ошибки (испорченный кусок, неверный календарь сезонов): забег откладывается на
    /// <see cref="CaptureWorker.RetryDelay"/>, чтобы не стоять первым в очереди и не обрывать проход.
    /// </summary>
    public async Task PostponeAsync(Guid runId, CancellationToken cancellationToken)
    {
        var failures = await db.Runs.Where(r => r.Id == runId).Select(r => r.FogFailures).SingleAsync(cancellationToken) + 1;
        var retryAt = time.GetUtcNow() + CaptureWorker.RetryDelay(failures);
        await db.Runs
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(
                set => set.SetProperty(r => r.FogFailures, failures).SetProperty(r => r.FogRetryAt, retryAt),
                cancellationToken);
    }

    /// <summary>Открывает туман забега. Возвращает число новых клеток или null, если забег уже открывал туман.</summary>
    public async Task<int?> StampRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null || run.FogStampedAt is not null || run.PrefixEndSeq < 0 || run.PointsPurgedAt is not null)
        {
            return null;
        }

        var (rules, judgement) = await judgements.JudgeAsync(run, cancellationToken);
        var stamp = FogStamp.Of(judgement.Points, judgement.Verdicts, run.League, rules.Exploration);
        var layer = run.League == League.Bike ? FogLayerKind.Bike : FogLayerKind.Foot;
        var now = time.GetUtcNow();
        var calendar = await seasons.CalendarAsync(cancellationToken);
        var season = calendar.At(run.StartedAt.AddMilliseconds(-run.ClockSkewMs))?.Number;

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
                    && (f.Season == SeasonCalendar.AllTime || f.Season == season)
                    && f.TileX >= minX && f.TileX <= maxX
                    && f.TileY >= minY && f.TileY <= maxY)
                .ToListAsync(cancellationToken);
        }

        int Merge(int seasonNumber)
        {
            var added = 0;
            foreach (var (key, bits) in stamp.Tiles)
            {
                var entity = stored.SingleOrDefault(f => f.Season == seasonNumber && f.TileX == key.X && f.TileY == key.Y);
                if (entity is null)
                {
                    added += bits.Count;
                    db.FogTiles.Add(new FogTileEntity
                    {
                        UserId = run.UserId,
                        Layer = layer,
                        Season = seasonNumber,
                        TileX = key.X,
                        TileY = key.Y,
                        Bits = FogTileCodec.Compress(bits),
                        CellCount = bits.Count,
                        Version = NextVersion(null, now),
                        UpdatedAt = now,
                    });
                    continue;
                }

                var old = FogTileCodec.Decompress(entity.Bits);
                var fresh = bits.NewCount(old);
                if (fresh == 0)
                {
                    continue; // тайл не изменился — версию не трогаем, приложение его не перекачает
                }

                added += fresh;
                old.UnionWith(bits);
                entity.Bits = FogTileCodec.Compress(old);
                entity.CellCount = old.Count;
                entity.Version = NextVersion(entity.Version, now);
                entity.UpdatedAt = now;
            }

            return added;
        }

        // «+N га» в итоге забега — новое за всё время; сезонный слой пополняется теми же клетками.
        var newCells = Merge(SeasonCalendar.AllTime);
        var seasonCells = season is { } seasonNumber ? Merge(seasonNumber) : 0;

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
        if (newCells > 0 || seasonCells > 0)
        {
            // После фиксации: подсказки о несостоявшемся изменении не бывает (docs/architecture/realtime.md).
            hints.FogChanged(run.UserId);
        }

        return newCells;
    }
}
