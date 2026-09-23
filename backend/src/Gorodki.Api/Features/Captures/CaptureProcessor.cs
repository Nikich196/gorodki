using System.Text.Json;
using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Config;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Runs;
using Gorodki.Domain.Territory;
using Gorodki.Domain.Time;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Npgsql;

namespace Gorodki.Api.Features.Captures;

/// <summary>
/// Обработка заявок петель одного забега (PLAN.md, §7.3; docs/architecture/captures.md). Проход по забегу: судья отрезков
/// один раз, готовые заявки — по порядку номеров. Заявка берётся в аренду короткой транзакцией, тяжёлое (судья, контур)
/// считается вне транзакций, земля пишется одной короткой транзакцией под блокировками игрока и тайлов.
/// </summary>
public sealed class CaptureProcessor(
    AppDbContext db, GameConfigStore configs, RunJudgements judgements, TimeProvider time, ILogger<CaptureProcessor> logger)
{
    /// <summary>Петля старше этого к моменту, когда у сервера появилось всё нужное, не засчитывается (§7.3).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(3);

    /// <summary>Аренда заявки: если обработчик упал, через это время заявку возьмёт другой проход.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    /// <summary>Повторов после ошибок движка — не больше, потом заявка помечается как неудачная (земля не меняется).</summary>
    public const int MaxEngineAttempts = 2;

    public const int MaxAttempts = 5;

    /// <summary>Столько хранится журнал захватов — и столько после захвата доступен его откат (PLAN.md, §7.3, шаг B.5).</summary>
    public static readonly TimeSpan JournalRetention = TimeSpan.FromDays(7);

    /// <summary>Обрабатывает готовые заявки забега. Возвращает, сколько заявок получили итог.</summary>
    public async Task<int> ProcessRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        var pending = await db.Captures.AsNoTracking()
            .Where(c => c.RunId == runId && c.Status == CaptureStatus.Pending)
            .OrderBy(c => c.ClaimNo)
            .ToListAsync(cancellationToken);
        if (run is null || pending.Count == 0)
        {
            return 0;
        }

        var now = time.GetUtcNow();
        var chunks = await db.RunChunks.AsNoTracking()
            .Where(c => c.RunId == runId)
            .Select(c => new { c.FirstSeq, c.LastSeq, c.LastPointMs, c.ReceivedAt })
            .ToListAsync(cancellationToken);
        var runComplete = run.LastSeq is { } last && run.PrefixEndSeq >= last;

        // Какие заявки готовы — по тем же правилам, что видит телефон в «чего ждёт».
        var decided = 0;
        var ready = new List<CaptureEntity>();
        var earlierWaiting = false;
        foreach (var claim in pending)
        {
            var endChunk = chunks.FirstOrDefault(c => c.FirstSeq <= claim.EndSeq && claim.EndSeq <= c.LastSeq);
            var wait = ClaimReadiness.Check(
                claim.EndSeq,
                run.PrefixEndSeq,
                run.PrefixSensorsMs,
                endChunk?.LastPointMs,
                runComplete,
                earlierWaiting && now - claim.ReceivedAt < ClaimReadiness.PreviousClaimPatience);
            if (wait == ClaimWait.Nothing)
            {
                ready.Add(claim);
                continue;
            }

            if (wait is ClaimWait.Points or ClaimWait.Sensors && now - claim.ReceivedAt > StaleAfter)
            {
                // Любое более позднее решение всё равно было бы «старше 3 часов».
                var code = wait == ClaimWait.Points ? "points_not_received" : "sensors_not_received";
                decided += await FinishAsync(claim.Id, null, CaptureStatus.Stale, code, cancellationToken);
                continue;
            }

            earlierWaiting = true;
        }

        if (ready.Count == 0)
        {
            return decided;
        }

        // Судья отрезков — один раз на проход, по всему непрерывному началу следа.
        var (rules, judgement) = await judgements.JudgeAsync(run, cancellationToken);

        foreach (var claim in ready)
        {
            var token = Guid.NewGuid();
            var attempts = await LeaseAsync(claim.Id, token, now, cancellationToken);
            if (attempts is null)
            {
                continue; // заявку уже обрабатывает другой проход
            }

            if (attempts > MaxAttempts)
            {
                decided += await FinishAsync(claim.Id, token, CaptureStatus.Failed, "too_many_attempts", cancellationToken);
                continue;
            }

            var evidenceAt = new[] { claim.ReceivedAt }
                .Concat(chunks.Where(c => c.FirstSeq <= claim.EndSeq).Select(c => c.ReceivedAt))
                .Max();
            try
            {
                decided += await DecideAsync(run, claim, rules, judgement, evidenceAt, token, cancellationToken);
            }
            catch (Exception e) when (IsEngineFailure(e))
            {
                // Движок не сошёлся или база отвергла геометрию: земля не изменилась. Повторим, потом — «не удалось».
                db.ChangeTracker.Clear();
                logger.LogError(e, "Захват {CaptureId} не применён (попытка {Attempt})", claim.Id, attempts);
                if (attempts >= MaxEngineAttempts)
                {
                    decided += await FinishAsync(claim.Id, token, CaptureStatus.Failed, "engine_failed", cancellationToken, e.Message);
                }
                else
                {
                    await ReleaseAsync(claim.Id, token, e.Message, cancellationToken);
                }
            }
        }

        await db.Runs
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.ProcessedSeq, r => Math.Max(r.ProcessedSeq, run.PrefixEndSeq)), cancellationToken);
        return decided;
    }

    /// <summary>Проверки заявки, контур и применение к карте.</summary>
    private async Task<int> DecideAsync(
        RunEntity run,
        CaptureEntity claim,
        GameConfig rules,
        TrackJudging.RunJudgement judgement,
        DateTimeOffset evidenceAt,
        Guid token,
        CancellationToken cancellationToken)
    {
        // Время петли по часам сервера: конец петли с поправкой на сдвиг часов телефона, но не позже прихода заявки.
        // Целые миллисекунды: время уходит в состояние кусков, и в памяти и в базе оно должно быть одинаковым.
        var endPoint = judgement.Points[claim.EndSeq];
        var effectiveAt = DateTimeOffset.FromUnixTimeMilliseconds(
            Math.Min(endPoint.TimeMs - run.ClockSkewMs, claim.ReceivedAt.ToUnixTimeMilliseconds()));
        var timing = (effectiveAt, evidenceAt);

        if (!run.MotionAuthorized)
        {
            return await FinishAsync(claim.Id, token, CaptureStatus.Rejected, "motion_not_authorized", cancellationToken, timing: timing);
        }

        var ring = LoopRing.Build(
            judgement.Points, judgement.Verdicts, claim.StartSeq, claim.EndSeq, claim.Closure == LoopClosure.Crossing, rules.Capture.LoopDetector);
        if (!ring.IsAccepted)
        {
            return await FinishAsync(claim.Id, token, CaptureStatus.Rejected, ring.RejectCode, cancellationToken, timing: timing);
        }

        if (evidenceAt - effectiveAt > StaleAfter)
        {
            return await FinishAsync(claim.Id, token, CaptureStatus.Stale, "stale", cancellationToken, timing: timing);
        }

        // Шаг A — вне транзакций: контур P (маски — когда появится osm-pipeline).
        var shape = CaptureShapeBuilder.Build(ring.Ring, ring.ClosingTolerance, masks: null, rules.Capture.Shape);
        if (!shape.IsAccepted)
        {
            return await FinishAsync(claim.Id, token, CaptureStatus.Rejected, LoopRing.Code(shape.Rejection), cancellationToken, timing: timing);
        }

        var canRemoveLevels = await CanRemoveLevelsAsync(run, claim, rules, judgement, effectiveAt, cancellationToken);
        return await ApplyAsync(claim, run.DeviceId, shape.Area, effectiveAt, evidenceAt, canRemoveLevels, token, cancellationToken);
    }

    /// <summary>
    /// Защита от мультиаккаунтов (PLAN.md, §3.3): аккаунт моложе 48 ч или с пробегом меньше 3 км чужие уровни не снимает.
    /// Пробег — засчитанный путь прежних забегов (все лиги) и этого забега до конца петли. Забег, у которого путь ещё не
    /// посчитан (визиты не прошли), в пробег не входит — в худшем случае игрок на минуты дольше считается новым.
    /// </summary>
    private async Task<bool> CanRemoveLevelsAsync(
        RunEntity run,
        CaptureEntity claim,
        GameConfig rules,
        TrackJudging.RunJudgement judgement,
        DateTimeOffset effectiveAt,
        CancellationToken cancellationToken)
    {
        var territory = rules.Territory;
        var createdAt = await db.Users.Where(u => u.Id == claim.UserId).Select(u => u.CreatedAt).SingleAsync(cancellationToken);
        if (effectiveAt - createdAt < TimeSpan.FromHours(territory.NewAccountHours))
        {
            return false;
        }

        var earlier = await db.Runs
            .Where(r => r.UserId == claim.UserId && r.Id != run.Id)
            .SumAsync(r => r.AcceptedMeters ?? 0, cancellationToken);
        if (earlier >= territory.NewAccountMinMeters)
        {
            return true;
        }

        var thisRun = JudgedPath.Length(
            JudgedPath.Segments(judgement.Points, judgement.Verdicts).Where(s => s.To.Seq <= claim.EndSeq));
        return earlier + thisRun >= territory.NewAccountMinMeters;
    }

    /// <summary>
    /// Шаг B — одна короткая транзакция: блокировки игрока (суточный лимит) и устройства, тайлов по порядку, движок, разница.
    /// </summary>
    private async Task<int> ApplyAsync(
        CaptureEntity claim,
        Guid deviceId,
        Geometry area,
        DateTimeOffset effectiveAt,
        DateTimeOffset evidenceAt,
        bool canRemoveLevels,
        Guid token,
        CancellationToken cancellationToken)
    {
        var current = await configs.GetCurrentAsync(cancellationToken); // карта общая — правила земли на момент применения
        db.ChangeTracker.Clear(); // куски прошлой заявки прохода не должны попасть в эту запись
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '15s'", cancellationToken);

        var userKey = claim.UserId.ToString();
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(1, hashtext({userKey}))", cancellationToken);

        // Заморозка проверяется под той же блокировкой, что берёт откат: после неё захваты игрока на карту не ложатся.
        var frozenUntil = await db.Users.Where(u => u.Id == claim.UserId).Select(u => u.FrozenUntil).SingleOrDefaultAsync(cancellationToken);
        if (frozenUntil > time.GetUtcNow())
        {
            await transaction.RollbackAsync(cancellationToken);
            return await FinishAsync(claim.Id, token, CaptureStatus.Rejected, "account_frozen", cancellationToken, timing: (effectiveAt, evidenceAt));
        }

        if (await AppliedOnGameDayAsync(claim.UserId, effectiveAt, cancellationToken) >= current.Rules.Capture.MaxCapturesPerDay)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await FinishAsync(claim.Id, token, CaptureStatus.Rejected, "daily_limit", cancellationToken, timing: (effectiveAt, evidenceAt));
        }

        // «Захват засчитывается одному аккаунту на устройство в сутки» (§3.3). Блокировка устройства — после блокировки
        // игрока: два аккаунта одного телефона не проскочат одновременно, а взаимной блокировки нет (порядок всегда один).
        var deviceKey = deviceId.ToString();
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(3, hashtext({deviceKey}))", cancellationToken);
        if (await DeviceUsedByOtherOnGameDayAsync(deviceId, claim.UserId, effectiveAt, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return await FinishAsync(claim.Id, token, CaptureStatus.Rejected, "device_shared", cancellationToken, timing: (effectiveAt, evidenceAt));
        }

        var tiles = TileKey.Covering(area.EnvelopeInternal);
        var lockSpace = 100 + (int)claim.League; // у каждой лиги своя карта — свои блокировки
        foreach (var tile in tiles)
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({lockSpace}, {tile.LockKey})", cancellationToken);
        }

        int minX = tiles[0].X, maxX = tiles[^1].X, minY = tiles.Min(t => t.Y), maxY = tiles.Max(t => t.Y);
        var stored = await db.Parcels
            .Where(p => p.League == claim.League && p.TileX >= minX && p.TileX <= maxX && p.TileY >= minY && p.TileY <= maxY)
            .ToListAsync(cancellationToken);

        var map = new TerritoryMap(current.Rules.Territory.ToRules(), new SliverSettings());
        map.Load(stored.Select(ToParcel));
        var result = map.Apply(area, new CaptureContext(claim.UserId, effectiveAt, new HashSet<Guid>(), canRemoveLevels));

        var now = time.GetUtcNow();
        var changedTiles = new List<TileKey>();
        foreach (var tile in result.ChangedTiles)
        {
            var before = stored.Where(p => p.TileX == tile.X && p.TileY == tile.Y).ToList();
            var diff = ParcelDiff.Compute([.. before.Select(p => (p.Id, ToParcel(p)))], map.ParcelsIn(tile));
            if (diff.IsEmpty)
            {
                continue;
            }

            changedTiles.Add(tile);
            db.Parcels.RemoveRange(before.Where(p => diff.Removed.Contains(p.Id)));
            db.Parcels.AddRange(diff.Added.Select(p => ToEntity(p, claim.League)));
        }

        // Журнал — в той же транзакции: земля без записи для отката (или запись без земли) не сохраняется никогда.
        // Только тайлы, версия которых выросла: публичная проекция считает скрытые захваты по журналу и вычитает их
        // из версии тайла — запись без роста версии дала бы зрителю «провал» версии.
        CaptureJournal.Add(db, claim.Id, claim.League, now, [.. result.Changes.Where(c => changedTiles.Contains(c.Tile))]);

        await db.SaveChangesAsync(cancellationToken);
        foreach (var tile in changedTiles)
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO app.tile_versions (league, tile_x, tile_y, version) VALUES ({(short)claim.League}, {tile.X}, {tile.Y}, 1)
                ON CONFLICT (league, tile_x, tile_y) DO UPDATE SET version = app.tile_versions.version + 1
                """,
                cancellationToken);
        }

        var appliedSeq = await db.Database.SqlQuery<long>($"SELECT nextval('app.capture_apply_seq') AS \"Value\"").SingleAsync(cancellationToken);
        var areas = result.AreaByOutcome.ToDictionary(kv => JsonNamingPolicy.CamelCase.ConvertName(kv.Key.ToString()), kv => Math.Round(kv.Value, 1));
        var taken = result.Area(PieceOutcome.ClaimedNeutral) + result.Area(PieceOutcome.Transferred);
        var updated = await db.Captures
            .Where(c => c.Id == claim.Id && c.LeaseToken == token && c.Status == CaptureStatus.Pending)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(c => c.Status, CaptureStatus.Applied)
                    .SetProperty(c => c.AreaSquareMeters, Math.Round(taken, 1))
                    .SetProperty(c => c.AreaByOutcome, JsonSerializer.Serialize(areas, (JsonSerializerOptions?)null))
                    .SetProperty(c => c.ChangedTiles, JsonSerializer.Serialize(changedTiles.Select(t => new[] { t.X, t.Y }), (JsonSerializerOptions?)null))
                    .SetProperty(c => c.Shape, area)
                    .SetProperty(c => c.EffectiveAt, effectiveAt)
                    .SetProperty(c => c.EvidenceAt, evidenceAt)
                    .SetProperty(c => c.AppliedSeq, appliedSeq)
                    .SetProperty(c => c.AppliedAt, now)
                    .SetProperty(c => c.TerritoryConfigVersion, current.Version)
                    .SetProperty(c => c.LeaseUntil, (DateTimeOffset?)null),
                cancellationToken);
        if (updated == 0)
        {
            // Аренду забрал другой проход (мы работали дольше её срока) — ничего не пишем.
            await transaction.RollbackAsync(cancellationToken);
            return 0;
        }

        await transaction.CommitAsync(cancellationToken);
        return 1;
    }

    /// <summary>Стирает журнал захватов старше <see cref="JournalRetention"/> (куски — каскадом). Возвращает, сколько записей стёрто.</summary>
    public Task<int> PruneJournalAsync(CancellationToken cancellationToken)
    {
        var before = time.GetUtcNow() - JournalRetention;
        return db.CaptureJournal.Where(j => j.AppliedAt < before).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Сколько захватов игрока уже применено в те же игровые сутки (по Минску).</summary>
    private async Task<int> AppliedOnGameDayAsync(Guid userId, DateTimeOffset effectiveAt, CancellationToken cancellationToken)
    {
        var day = GameClock.GameDayOf(effectiveAt);
        var nearby = await db.Captures.AsNoTracking()
            .Where(c => c.UserId == userId
                && c.Status == CaptureStatus.Applied
                && c.EffectiveAt > effectiveAt.AddDays(-2)
                && c.EffectiveAt < effectiveAt.AddDays(2))
            .Select(c => c.EffectiveAt)
            .ToListAsync(cancellationToken);
        return nearby.Count(e => e is { } at && GameClock.GameDayOf(at) == day);
    }

    /// <summary>Был ли в те же игровые сутки применён захват другого аккаунта с этого устройства.</summary>
    private async Task<bool> DeviceUsedByOtherOnGameDayAsync(
        Guid deviceId, Guid userId, DateTimeOffset effectiveAt, CancellationToken cancellationToken)
    {
        var day = GameClock.GameDayOf(effectiveAt);
        var nearby = await db.Runs.AsNoTracking()
            .Where(r => r.DeviceId == deviceId && r.UserId != userId)
            .Join(
                db.Captures.Where(c => c.Status == CaptureStatus.Applied
                    && c.EffectiveAt > effectiveAt.AddDays(-2)
                    && c.EffectiveAt < effectiveAt.AddDays(2)),
                r => r.Id,
                c => c.RunId,
                (r, c) => c.EffectiveAt)
            .ToListAsync(cancellationToken);
        return nearby.Any(e => e is { } at && GameClock.GameDayOf(at) == day);
    }

    /// <summary>Берёт заявку в аренду. Возвращает номер попытки или null, если заявку уже держит другой проход.</summary>
    private async Task<int?> LeaseAsync(Guid captureId, Guid token, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var leased = await db.Captures
            .Where(c => c.Id == captureId && c.Status == CaptureStatus.Pending && (c.LeaseUntil == null || c.LeaseUntil < now))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(c => c.LeaseUntil, now + Lease)
                    .SetProperty(c => c.LeaseToken, token)
                    .SetProperty(c => c.Attempts, c => c.Attempts + 1),
                cancellationToken);
        return leased == 0
            ? null
            : await db.Captures.Where(c => c.Id == captureId).Select(c => c.Attempts).SingleAsync(cancellationToken);
    }

    private Task ReleaseAsync(Guid captureId, Guid token, string error, CancellationToken cancellationToken) =>
        db.Captures
            .Where(c => c.Id == captureId && c.LeaseToken == token)
            .ExecuteUpdateAsync(
                set => set.SetProperty(c => c.LeaseUntil, (DateTimeOffset?)null).SetProperty(c => c.LastError, Truncate(error)),
                cancellationToken);

    /// <summary>Окончательный итог без изменения карты (отказ, устаревание, неудача). С арендой — только если она наша.</summary>
    private async Task<int> FinishAsync(
        Guid captureId,
        Guid? token,
        CaptureStatus status,
        string? code,
        CancellationToken cancellationToken,
        string? error = null,
        (DateTimeOffset EffectiveAt, DateTimeOffset EvidenceAt)? timing = null)
    {
        return await db.Captures
            .Where(c => c.Id == captureId && c.Status == CaptureStatus.Pending && (token == null || c.LeaseToken == token))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(c => c.Status, status)
                    .SetProperty(c => c.RejectCode, code)
                    .SetProperty(c => c.LastError, error == null ? null : Truncate(error))
                    .SetProperty(c => c.EffectiveAt, timing == null ? null : timing.Value.EffectiveAt)
                    .SetProperty(c => c.EvidenceAt, timing == null ? null : timing.Value.EvidenceAt)
                    .SetProperty(c => c.LeaseUntil, (DateTimeOffset?)null),
                cancellationToken);
    }

    private static bool IsEngineFailure(Exception e) =>
        e is TerritoryEngineException or TopologyException
        || (e is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.CheckViolation } })
        || e is PostgresException { SqlState: PostgresErrorCodes.CheckViolation };

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500];

    internal static Parcel ToParcel(ParcelEntity p) => new(
        new TileKey(p.TileX, p.TileY),
        p.Geometry,
        new ParcelState
        {
            OwnerId = p.OwnerId,
            Level = p.Level,
            LastVisitAt = p.LastVisitAt,
            LastLevelUpAt = p.LastLevelUpAt,
            ShieldUntil = p.ShieldUntil,
            SiegeUntil = p.SiegeUntil,
            LossWindowSince = p.LossWindowSince,
            LossAttackers = AttackerSet.Of(p.LossAttackers),
        });

    internal static ParcelEntity ToEntity(Parcel p, Gorodki.Domain.Leagues.League league) => new()
    {
        League = league,
        TileX = p.Tile.X,
        TileY = p.Tile.Y,
        OwnerId = p.State.OwnerId,
        Level = (short)p.State.Level,
        LastVisitAt = p.State.LastVisitAt,
        LastLevelUpAt = p.State.LastLevelUpAt,
        ShieldUntil = p.State.ShieldUntil,
        SiegeUntil = p.State.SiegeUntil,
        LossWindowSince = p.State.LossWindowSince,
        LossAttackers = [.. p.State.LossAttackers.Ids],
        Geometry = p.Geometry,
    };
}
