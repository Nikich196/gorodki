using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Fog;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Features.Seasons;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Segments;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Segments;

/// <summary>
/// Засчитывание проходов отрезков «Королей участков» на сервере (PLAN.md, §3.13; §7.3 «Конвейер забега», шаг 5) — стадия
/// фонового обработчика <see cref="CaptureWorker"/>, как туман: один раз за забег, когда он завершён и все точки на месте
/// (или давно закрыт сервером). Путь — только принятый судьёй отрезков той версии правил, с которой забег начат
/// (<see cref="RunJudgements"/>), проход — <see cref="SegmentEffortDetector"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Граница публичности.</b> Проход записывается сразу (себе он виден сразу — <c>myBestTimeMs</c>), а другим — с
/// <see cref="SegmentEffortEntity.VisibleAt"/>: границы публичности конца забега по часам сервера (как визиты и пост о
/// забеге, <see cref="TerritoryReader.PublicAt"/>). Не по моменту прохода: иначе таблица отрезка показала бы «прошёл 20 минут
/// назад» про игрока, который ещё бежит рядом. У демо-аккаунта — сразу, как его захваты.
/// </para>
/// <para>
/// <b>«Тебя свергли с трона».</b> В той же транзакции, под блокировкой лиги (пространство <see cref="LockSpace"/>), каждый
/// новый проход сравнивается с лучшим проходом другого игрока на отрезке в лиге — сезона прохода и за всё время. Лучше —
/// строка <see cref="SegmentDethronementEntity"/> прежнему королю, видимая не раньше самого прохода; её прочитают
/// «Входящие» (#139). Блокировка нужна, чтобы два забега, посчитанные одновременно (второй экземпляр сервера во время
/// деплоя), не свергли одного короля дважды и не пропустили друг друга.
/// </para>
/// <para>
/// <b>Повтор.</b> Забег помечается <c>runs.segments_processed_at</c> в той же транзакции; второй проход видит отметку и
/// ничего не пишет, а уникальный индекс (забег, отрезок, момент старта) не даст записать проход дважды и при ошибке в коде.
/// Повтор записанного забега (демо) проходов не даёт — как дистанцию и пост.
/// </para>
/// </remarks>
public sealed class SegmentEffortProcessor(
    AppDbContext db, RunJudgements judgements, GameConfigStore configs, SeasonStore seasons, TimeProvider time)
{
    /// <summary>Пространство блокировок PostgreSQL: ключ — лига (docs/architecture/jobs.md).</summary>
    public const int LockSpace = 8;

    /// <summary>
    /// Забеги, готовые к засчитыванию проходов, — сначала закончившиеся раньше. Забег, проходы которого не посчитались из-за
    /// ошибки, ждёт своей паузы (<see cref="PostponeAsync"/>).
    /// </summary>
    public Task<List<Guid>> RunsReadyAsync(int take, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var closedBefore = now - FogProcessor.ClosedRunGrace;
        return db.Runs.AsNoTracking()
            .Where(r => r.SegmentsProcessedAt == null
                && r.Source == RunSource.Live
                && r.PointsPurgedAt == null
                && r.PrefixEndSeq >= 0
                && (r.SegmentsRetryAt == null || r.SegmentsRetryAt <= now)
                && ((r.Status == RunStatus.Finished && r.LastSeq != null && r.PrefixEndSeq >= r.LastSeq)
                    || (r.Status != RunStatus.Active && r.EndedAt < closedBefore)))
            .OrderBy(r => r.EndedAt)
            .Select(r => r.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Проходы забега не посчитались из-за ошибки: забег откладывается на <see cref="CaptureWorker.RetryDelay"/>.</summary>
    public async Task PostponeAsync(Guid runId, CancellationToken cancellationToken)
    {
        var failures = await db.Runs.Where(r => r.Id == runId).Select(r => r.SegmentsFailures).SingleAsync(cancellationToken) + 1;
        var retryAt = time.GetUtcNow() + CaptureWorker.RetryDelay(failures);
        await db.Runs
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(
                set => set.SetProperty(r => r.SegmentsFailures, failures).SetProperty(r => r.SegmentsRetryAt, retryAt),
                cancellationToken);
    }

    /// <summary>Засчитывает проходы забега. Возвращает их число или null — забег уже обработан (или его точки стёрты).</summary>
    public async Task<int?> ProcessRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null || run.SegmentsProcessedAt is not null || run.PrefixEndSeq < 0 || run.PointsPurgedAt is not null)
        {
            return null;
        }

        var found = new List<(Guid SegmentId, SegmentEffort Effort)>();
        if (run.Source == RunSource.Live)
        {
            var (_, judgement) = await judgements.JudgeAsync(run, cancellationToken);
            var stretches = SegmentEffortDetector.Stretches(judgement.Points, judgement.Verdicts);
            var segments = await db.Segments.AsNoTracking()
                .Where(s => s.League == null || s.League == run.League)
                .ToListAsync(cancellationToken);
            found = [.. segments
                .SelectMany(s => SegmentEffortDetector.Detect(SegmentSeed.Course(s.Line), stretches, run.League, SegmentRules.Default)
                    .Select(e => (s.Id, e)))
                .OrderBy(f => f.e.StartMs)
                .ThenBy(f => f.Id)];
        }

        var now = time.GetUtcNow();
        var calendar = await seasons.CalendarAsync(cancellationToken);
        var visibleAt = await VisibleAtAsync(run, found.Select(f => f.Effort), now, cancellationToken);

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '15s'", cancellationToken);
        var league = (int)run.League;
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockSpace}, {league})", cancellationToken);
        if (await db.Runs.AnyAsync(r => r.Id == runId && r.SegmentsProcessedAt != null, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken); // другой проход успел раньше
            return null;
        }

        foreach (var (segmentId, effort) in found)
        {
            var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(effort.StartMs - run.ClockSkewMs); // по часам сервера
            var entity = new SegmentEffortEntity
            {
                SegmentId = segmentId,
                UserId = run.UserId,
                RunId = run.Id,
                League = run.League,
                Season = calendar.At(startedAt)?.Number,
                ElapsedMs = effort.ElapsedMs,
                StartedAt = startedAt,
                VisibleAt = visibleAt,
            };
            var dethroned = await DethronedAsync(entity, cancellationToken);
            db.SegmentEfforts.Add(entity);
            await db.SaveChangesAsync(cancellationToken); // номер прохода нужен событию
            foreach (var (userId, season, allTime) in dethroned)
            {
                db.SegmentDethronements.Add(new SegmentDethronementEntity
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    SegmentId = segmentId,
                    League = run.League,
                    EffortId = entity.Id,
                    Season = season,
                    AllTime = allTime,
                    VisibleAt = visibleAt > now ? visibleAt : now, // событие не «из прошлого»: оно появилось сейчас
                });
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        await db.Runs
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.SegmentsProcessedAt, now), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return found.Count;
    }

    /// <summary>
    /// С какого момента проходы забега видят другие: граница публичности конца забега по часам сервера (и не раньше конца
    /// последнего прохода — на случай, если телефон назвал конец раньше своих точек); демо-аккаунт — сейчас.
    /// </summary>
    private async Task<DateTimeOffset> VisibleAtAsync(
        RunEntity run, IEnumerable<SegmentEffort> efforts, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await db.Users.AsNoTracking().AnyAsync(u => u.Id == run.UserId && u.Role == UserRole.Demo, cancellationToken))
        {
            return now;
        }

        var endMs = Math.Max(
            (run.EndedAt ?? now.AddMilliseconds(run.ClockSkewMs)).ToUnixTimeMilliseconds(),
            efforts.Select(e => e.FinishMs).DefaultIfEmpty(long.MinValue).Max());
        var end = DateTimeOffset.FromUnixTimeMilliseconds(endMs - run.ClockSkewMs);
        var delay = TimeSpan.FromMinutes((await configs.GetCurrentAsync(cancellationToken)).Rules.Privacy.PublicEventDelayMinutes);
        return TerritoryReader.PublicAt(end, delay);
    }

    /// <summary>
    /// Кого свергает новый проход: лучший проход другого игрока на отрезке в лиге — в сезоне прохода и за всё время, если
    /// новый быстрее (при равном времени корона остаётся у первого). Игроки, удаляющие аккаунт, короны уже не держат.
    /// </summary>
    private async Task<List<(Guid UserId, int? Season, bool AllTime)>> DethronedAsync(SegmentEffortEntity effort, CancellationToken cancellationToken)
    {
        var board = db.SegmentEfforts.AsNoTracking()
            .Where(e => e.SegmentId == effort.SegmentId && e.League == effort.League
                && db.Users.Any(u => u.Id == e.UserId && u.DeletionRequestedAt == null));
        var allTime = await board.OrderBy(e => e.ElapsedMs).ThenBy(e => e.Id)
            .Select(e => new { e.UserId, e.ElapsedMs })
            .FirstOrDefaultAsync(cancellationToken);
        var seasonal = effort.Season is { } number
            ? await board.Where(e => e.Season == number).OrderBy(e => e.ElapsedMs).ThenBy(e => e.Id)
                .Select(e => new { e.UserId, e.ElapsedMs })
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        var losers = new List<(Guid UserId, int? Season, bool AllTime)>();
        var lostSeason = seasonal is not null && seasonal.UserId != effort.UserId && effort.ElapsedMs < seasonal.ElapsedMs;
        var lostAllTime = allTime is not null && allTime.UserId != effort.UserId && effort.ElapsedMs < allTime.ElapsedMs;
        if (lostSeason && lostAllTime && seasonal!.UserId == allTime!.UserId)
        {
            losers.Add((allTime.UserId, effort.Season, true));
            return losers;
        }

        if (lostSeason)
        {
            losers.Add((seasonal!.UserId, effort.Season, false));
        }

        if (lostAllTime)
        {
            losers.Add((allTime!.UserId, null, true));
        }

        return losers;
    }
}
