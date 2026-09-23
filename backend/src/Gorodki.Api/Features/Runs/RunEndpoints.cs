using System.Security.Claims;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Config;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Runs;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Gorodki.Api.Features.Runs;

/// <summary>
/// Забеги и приём точек (PLAN.md, §3.2, §3.9, §7.2). Телефон работает без сети: забег создаётся с его собственным
/// идентификатором, куски точек досылаются позже, любой повтор запроса безопасен. Подробно — docs/architecture/runs.md.
/// </summary>
public static class RunEndpoints
{
    public static IEndpointRouteBuilder MapRunEndpoints(this IEndpointRouteBuilder app)
    {
        var runs = app.MapGroup("/runs").WithTags("Забеги").RequireRateLimiting(RunLimits.RateLimitPolicy);

        runs.MapPost("", StartRun)
            .WithName("startRun")
            .WithSummary("Старт забега (повтор того же запроса безопасен)")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        runs.MapPut("/{runId:guid}/chunks/{firstSeq:int}", UploadChunk)
            .WithName("uploadChunk")
            .WithSummary("Кусок точек и данных датчиков (повтор того же куска безопасен)")
            .WithMetadata(new RequestSizeLimitAttribute(RunLimits.MaxRequestBytes))
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        runs.MapPost("/{runId:guid}/finish", FinishRun)
            .WithName("finishRun")
            .WithSummary("Завершение забега: время конца и номер последней точки")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        runs.MapGet("/{runId:guid}", GetRun)
            .WithName("getRun")
            .WithSummary("Забег: статус, какие точки уже есть и каких не хватает")
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<Results<Created<RunResponse>, Ok<RunResponse>, ProblemHttpResult>> StartRun(
        StartRunRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        GameConfigStore configs,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } userId)
        {
            return Problem(StatusCodes.Status401Unauthorized, "unauthorized", "Нужно войти.");
        }

        var now = time.GetUtcNow();
        var skewMs = request.SentAtMs - now.ToUnixTimeMilliseconds();
        if (Math.Abs(skewMs) > RunLimits.MaxClockSkew.TotalMilliseconds)
        {
            return Problem(StatusCodes.Status400BadRequest, "device_clock_invalid", "Часы телефона сбиты: включите автоматическую установку времени.");
        }

        if (request.Id == Guid.Empty
            || request.DeviceId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.AppVersion)
            || request.AppVersion.Length > 32
            || !Enum.IsDefined(request.League)
            || !Enum.IsDefined(request.Source))
        {
            return Problem(StatusCodes.Status400BadRequest, "run_invalid", "Неполные данные забега.");
        }

        if (request.StartedAtMs > request.SentAtMs + TrackChunkRules.FutureToleranceMs)
        {
            return Problem(StatusCodes.Status400BadRequest, "start_in_future", "Забег не может начаться в будущем.");
        }

        // Начало по часам сервера: startedAt − сдвиг. «Старше недели» — то же, что startedAt < sentAt − неделя.
        // Проверка до перевода в дату: иначе безумное число от клиента уронило бы сервер вместо ответа 400.
        if (request.StartedAtMs < request.SentAtMs - (long)RunLimits.UploadWindow.TotalMilliseconds)
        {
            return Problem(StatusCodes.Status400BadRequest, "run_too_old", "Забег старше недели — он уже не будет засчитан.");
        }

        var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(request.StartedAtMs);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Строка игрока блокируется до конца транзакции: старты одного игрока идут по очереди.
        var user = await db.Users
            .FromSql($"SELECT * FROM app.users WHERE id = {userId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            return Problem(StatusCodes.Status401Unauthorized, "unauthorized", "Нужно войти заново.");
        }

        if (user.DeletionRequestedAt is not null)
        {
            return Problem(StatusCodes.Status403Forbidden, "account_deleting", "Аккаунт удаляется — новые забеги не принимаются.");
        }

        // Повтор того же старта (ответ потерялся) — ничего не меняем.
        var existing = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == request.Id, cancellationToken);
        if (existing is not null)
        {
            return existing.UserId == userId && IsSameStart(existing, request)
                ? TypedResults.Ok(await ToResponseAsync(db, existing, cancellationToken))
                : Problem(StatusCodes.Status409Conflict, "run_conflict", "Забег с таким идентификатором уже есть.");
        }

        if (request.Source == RunSource.Replay && user.Role == UserRole.Player)
        {
            return Problem(StatusCodes.Status403Forbidden, "replay_forbidden", "Повтор забега — только для демо-аккаунта.");
        }

        var config = await configs.GetForRunAsync(
            request.ConfigVersion, startedAt.AddMilliseconds(-skewMs), RunLimits.ConfigGrace, cancellationToken);
        if (config is null)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "config_invalid", "Эта версия правил не подходит — обновите конфиг.");
        }

        var runsToday = await db.Runs.CountAsync(r => r.UserId == userId && r.CreatedAt > now.AddDays(-1), cancellationToken);
        if (runsToday >= RunLimits.MaxRunsPerDay)
        {
            return Problem(StatusCodes.Status429TooManyRequests, "daily_run_limit", "Слишком много забегов за сутки.");
        }

        var maxLength = TimeSpan.FromHours(config.Rules.Capture.MaxRunHours);
        var run = new RunEntity
        {
            Id = request.Id,
            UserId = userId,
            League = request.League,
            Source = request.Source,
            ConfigVersion = config.Version,
            StartedAt = startedAt,
            Status = RunStatus.Active,
            CreatedAt = now,
            ClockSkewMs = skewMs,
            DeviceId = request.DeviceId,
            AppVersion = request.AppVersion,
            MotionAuthorized = request.MotionAuthorized,
            Newcomer = !await db.Captures.AnyAsync(
                c => c.UserId == userId && c.Status == CaptureStatus.Applied, cancellationToken),
        };

        // Активен только самый поздний забег игрока. Если уже есть забег, начатый позже (этот пришёл с опозданием
        // из офлайна), новый сразу закрыт: он закончился не позже, чем начался следующий.
        var laterStart = await db.Runs
            .Where(r => r.UserId == userId && r.StartedAt > startedAt)
            .MinAsync(r => (DateTimeOffset?)r.StartedAt, cancellationToken);
        if (laterStart is { } next)
        {
            run.Status = RunStatus.Abandoned;
            run.EndedAt = Min(next, startedAt + maxLength);
        }
        else
        {
            // Прежние активные забеги закрываются отдельной командой до вставки нового: база допускает один активный.
            await db.Runs
                .Where(r => r.UserId == userId && r.Status == RunStatus.Active)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(r => r.Status, RunStatus.Abandoned)
                        .SetProperty(
                            r => r.EndedAt,
                            r => (DateTimeOffset?)(r.StartedAt + maxLength < startedAt ? r.StartedAt + maxLength : startedAt)),
                    cancellationToken);
        }

        // Забеги одного игрока не пересекаются по времени: закрытый сервером забег, начатый раньше нового,
        // закончился не позже начала нового (так бывает, когда из офлайна приходит забег «между» двумя другими).
        await db.Runs
            .Where(r => r.UserId == userId && r.Status == RunStatus.Abandoned && r.StartedAt < startedAt && r.EndedAt > startedAt)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.EndedAt, (DateTimeOffset?)startedAt), cancellationToken);

        db.Runs.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Created($"/runs/{run.Id}", await ToResponseAsync(db, run, cancellationToken));
    }

    private static async Task<Results<Created<ChunkReceipt>, Ok<ChunkReceipt>, ProblemHttpResult>> UploadChunk(
        Guid runId,
        int firstSeq,
        UploadChunkRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        GameConfigStore configs,
        CaptureSignal signal,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } userId)
        {
            return Problem(StatusCodes.Status401Unauthorized, "unauthorized", "Нужно войти.");
        }

        var run = await db.Runs.AsNoTracking()
            .Where(r => r.Id == runId && r.UserId == userId)
            .Select(r => new
            {
                r.StartedAt,
                r.CreatedAt,
                r.ConfigVersion,
                r.LastSeq,
                Purged = r.PointsPurgedAt != null,
                Deleting = db.Users.Any(u => u.Id == userId && u.DeletionRequestedAt != null),
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (run is null)
        {
            // Правило для телефона: повторить POST /runs и отправить кусок снова, а не выбрасывать его.
            return Problem(StatusCodes.Status404NotFound, "run_not_found", "Забег не найден.");
        }

        if (run.Deleting)
        {
            return Problem(StatusCodes.Status403Forbidden, "account_deleting", "Аккаунт удаляется — данные не принимаются.");
        }

        var now = time.GetUtcNow();
        if (now > run.CreatedAt + RunLimits.UploadWindow || run.Purged)
        {
            return UploadWindowClosed();
        }

        var config = await configs.GetAsync(run.ConfigVersion, cancellationToken)
            ?? throw new InvalidOperationException($"Нет версии конфига {run.ConfigVersion}, с которой начат забег.");
        var maxSeq = RunLimits.MaxSeq(config.Rules);

        var (chunk, problems) = ToChunk(firstSeq, request, maxSeq);
        if (chunk is not null)
        {
            problems.AddRange(TrackChunkRules.Check(chunk, new ChunkLimits(
                run.StartedAt.ToUnixTimeMilliseconds(),
                request.SentAtMs,
                config.Rules.Capture.MaxRunHours,
                maxSeq,
                RunLimits.MaxPointsPerChunk,
                RunLimits.MaxSamplesPerChunk)));
            if (run.LastSeq is { } lastSeq && chunk.LastSeq > lastSeq)
            {
                problems.Add(new ChunkProblem("points", "after_last_seq"));
            }
        }

        if (chunk is null || problems.Count > 0)
        {
            return TypedResults.Problem(
                title: "Кусок забега не прошёл проверку.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "chunk_invalid",
                    ["problems"] = problems.Take(TrackChunkRules.MaxProblems).ToArray(),
                });
        }

        var bytes = TrackChunkCodec.Encode(chunk);
        var hash = TrackChunkCodec.Hash(bytes);
        var receipt = new ChunkReceipt(chunk.FirstSeq, chunk.LastSeq, Duplicate: false);

        // Сначала — точный повтор: тот же номер и то же содержимое.
        var existing = await db.RunChunks.AsNoTracking()
            .Where(c => c.RunId == runId && c.FirstSeq == firstSeq)
            .Select(c => c.ContentHash)
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing.AsSpan().SequenceEqual(hash)
                ? TypedResults.Ok(receipt with { Duplicate = true })
                : await ChunkConflictAsync(db, runId, chunk, cancellationToken);
        }

        var bytesToday = await db.Runs
            .Where(r => r.UserId == userId && r.CreatedAt > now.AddDays(-1))
            .SumAsync(r => (long)r.StoredBytes, cancellationToken);
        if (bytesToday + bytes.Length > RunLimits.MaxBytesPerUserPerDay)
        {
            return Problem(StatusCodes.Status429TooManyRequests, "daily_storage_limit", "Слишком много данных за сутки.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Место под кусок резервируется одной командой с условием — лимиты забега не обойти параллельными запросами.
        // Условие на отметку стирания: кусок, пришедший, пока стираются точки забега, не ляжет после стирания (RunRetention).
        var reserved = await db.Runs
            .Where(r => r.Id == runId
                && r.PointsPurgedAt == null
                && r.ChunkCount < RunLimits.MaxChunksPerRun
                && r.StoredBytes + bytes.Length <= RunLimits.MaxBytesPerRun)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(r => r.ChunkCount, r => r.ChunkCount + 1)
                    .SetProperty(r => r.StoredBytes, r => r.StoredBytes + bytes.Length),
                cancellationToken);
        if (reserved == 0)
        {
            return await db.Runs.AnyAsync(r => r.Id == runId && r.PointsPurgedAt != null, cancellationToken)
                ? UploadWindowClosed()
                : Problem(StatusCodes.Status413PayloadTooLarge, "run_storage_limit", "Забег слишком большой.");
        }

        db.RunChunks.Add(new RunChunkEntity
        {
            RunId = runId,
            FirstSeq = chunk.FirstSeq,
            LastSeq = chunk.LastSeq,
            ContentHash = hash,
            Points = bytes,
            ReceivedAt = now,
            FirstPointMs = chunk.Points[0].TimeMs,
            LastPointMs = chunk.Points[^1].TimeMs,
            SensorsCompleteThroughMs = chunk.SensorsCompleteThroughMs,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await UpdatePrefixAsync(db, runId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ExclusionViolation,
        })
        {
            // Одновременно записался другой кусок на те же номера — например, этот же повтор.
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var same = await db.RunChunks.AnyAsync(
                c => c.RunId == runId && c.FirstSeq == firstSeq && c.ContentHash == hash, cancellationToken);
            return same
                ? TypedResults.Ok(receipt with { Duplicate = true })
                : await ChunkConflictAsync(db, runId, chunk, cancellationToken);
        }

        signal.Notify(runId); // у забега могли стать готовыми заявки петель
        return TypedResults.Created($"/runs/{runId}", receipt);
    }

    private static async Task<Results<Ok<RunResponse>, ProblemHttpResult>> FinishRun(
        Guid runId,
        FinishRunRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        GameConfigStore configs,
        CaptureSignal signal,
        CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } userId)
        {
            return Problem(StatusCodes.Status401Unauthorized, "unauthorized", "Нужно войти.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var run = await db.Runs
            .FromSql($"SELECT * FROM app.runs WHERE id = {runId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (run is null || run.UserId != userId)
        {
            return Problem(StatusCodes.Status404NotFound, "run_not_found", "Забег не найден.");
        }

        // Повтор завершения ничего не меняет.
        if (run.Status == RunStatus.Finished)
        {
            return TypedResults.Ok(await ToResponseAsync(db, run, cancellationToken));
        }

        var config = await configs.GetAsync(run.ConfigVersion, cancellationToken)
            ?? throw new InvalidOperationException($"Нет версии конфига {run.ConfigVersion}, с которой начат забег.");
        if (request.LastSeq < -1 || request.LastSeq > RunLimits.MaxSeq(config.Rules))
        {
            return Problem(StatusCodes.Status400BadRequest, "finish_invalid", "Неверный номер последней точки.");
        }

        if (request.EndedAtMs > request.SentAtMs + TrackChunkRules.FutureToleranceMs)
        {
            return Problem(StatusCodes.Status400BadRequest, "ended_in_future", "Забег не может закончиться в будущем.");
        }

        var storedLast = await db.RunChunks.Where(c => c.RunId == runId).MaxAsync(c => (int?)c.LastSeq, cancellationToken) ?? -1;
        if (request.LastSeq < storedLast)
        {
            return Problem(StatusCodes.Status409Conflict, "last_seq_too_small", "Сервер уже получил точки дальше последней.");
        }

        // Конец — не раньше начала, не позже предела длины и не позже начала следующего забега этого игрока.
        // Зажимаем в миллисекундах до перевода в дату: число от клиента может быть любым.
        var startMs = run.StartedAt.ToUnixTimeMilliseconds();
        var maxLengthMs = (long)TimeSpan.FromHours(config.Rules.Capture.MaxRunHours).TotalMilliseconds;
        var endedAt = DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(request.EndedAtMs, startMs, startMs + maxLengthMs));
        var nextStart = await db.Runs
            .Where(r => r.UserId == userId && r.StartedAt > run.StartedAt)
            .MinAsync(r => (DateTimeOffset?)r.StartedAt, cancellationToken);
        if (nextStart is { } next)
        {
            endedAt = Min(endedAt, next);
        }

        run.Status = RunStatus.Finished;
        run.EndedAt = endedAt;
        run.LastSeq = request.LastSeq;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        signal.Notify(runId); // забег завершён — заявки, ждавшие датчиков, могут стать готовыми
        return TypedResults.Ok(await ToResponseAsync(db, run, cancellationToken));
    }

    private static async Task<Results<Ok<RunResponse>, ProblemHttpResult>> GetRun(
        Guid runId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId && r.UserId == userId, cancellationToken);
        return run is null
            ? Problem(StatusCodes.Status404NotFound, "run_not_found", "Забег не найден.")
            : TypedResults.Ok(await ToResponseAsync(db, run, cancellationToken));
    }

    /// <summary>
    /// Точки из запроса в доменный кусок. Здесь — только то, без чего кусок не построить (номера, диапазоны координат);
    /// остальное проверяет <see cref="TrackChunkRules"/>. В проблемах нет значений — только поле и правило.
    /// </summary>
    private static (TrackChunk? Chunk, List<ChunkProblem> Problems) ToChunk(int firstSeq, UploadChunkRequest request, int maxSeq)
    {
        var problems = new List<ChunkProblem>();
        if (firstSeq < 0 || firstSeq > maxSeq)
        {
            problems.Add(new("firstSeq", "seq_limit"));
            return (null, problems);
        }

        var points = request.Points ?? [];
        if (points.Count == 0 || points.Count > RunLimits.MaxPointsPerChunk)
        {
            problems.Add(new("points", "count"));
            return (null, problems);
        }

        for (var i = 0; i < points.Count && problems.Count < TrackChunkRules.MaxProblems; i++)
        {
            var point = points[i];
            var field = $"points[{i}]";
            if (point.Seq != firstSeq + i)
            {
                problems.Add(new(field, "seq_order"));
            }

            if (!double.IsFinite(point.Lat) || point.Lat is < -90 or > 90)
            {
                problems.Add(new(field, "lat_range"));
            }

            if (!double.IsFinite(point.Lon) || point.Lon is < -180 or > 180)
            {
                problems.Add(new(field, "lon_range"));
            }

            if (!double.IsFinite(point.Acc) || point.Acc < 0)
            {
                problems.Add(new(field, "acc_range"));
            }

            if ((point.Flags & ~(int)(PointFlags.Simulated | PointFlags.ProducedByAccessory)) != 0)
            {
                problems.Add(new(field, "flags"));
            }
        }

        if (problems.Count > 0)
        {
            return (null, problems);
        }

        var chunk = new TrackChunk(
            firstSeq,
            request.SensorsCompleteThroughMs,
            points.Select(p => TrackPoint.FromMeasurements(p.Seq, p.T, p.Lat, p.Lon, p.Acc, p.Speed, (PointFlags)p.Flags)).ToArray(),
            (request.Motion ?? []).Select(m => new MotionSample(m.T, m.Activity)).ToArray(),
            (request.Steps ?? []).Select(s => new StepSample(s.Start, s.End, s.Steps)).ToArray());
        return (chunk, problems);
    }

    private static ProblemHttpResult UploadWindowClosed() =>
        Problem(StatusCodes.Status409Conflict, "upload_window_closed", "Точки этого забега больше не принимаются.");

    private static async Task<ProblemHttpResult> ChunkConflictAsync(
        AppDbContext db, Guid runId, TrackChunk chunk, CancellationToken cancellationToken)
    {
        // Какие уже принятые куски мешают: телефон дошлёт только номера, которых у сервера нет.
        var overlaps = await db.RunChunks.AsNoTracking()
            .Where(c => c.RunId == runId && c.FirstSeq <= chunk.LastSeq && c.LastSeq >= chunk.FirstSeq)
            .OrderBy(c => c.FirstSeq)
            .Select(c => new SeqRange(c.FirstSeq, c.LastSeq))
            .Take(20)
            .ToArrayAsync(cancellationToken);
        return TypedResults.Problem(
            title: "Эти номера точек уже заняты другим куском.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["code"] = "chunk_conflict", ["overlaps"] = overlaps });
    }

    /// <summary>
    /// Пересчитывает непрерывное начало следа и отметку полноты датчиков по нему. Вызывается под той же блокировкой
    /// строки забега, что и резервирование места под кусок, поэтому одновременные куски считаются по очереди.
    /// </summary>
    private static async Task UpdatePrefixAsync(AppDbContext db, Guid runId, CancellationToken cancellationToken)
    {
        var chunks = await db.RunChunks.AsNoTracking()
            .Where(c => c.RunId == runId)
            .Select(c => new { c.FirstSeq, c.LastSeq, c.SensorsCompleteThroughMs })
            .ToListAsync(cancellationToken);
        var prefixEnd = SeqRange.ContiguousPrefixEnd(chunks.Select(c => new SeqRange(c.FirstSeq, c.LastSeq)));
        var sensors = chunks.Where(c => c.LastSeq <= prefixEnd).Select(c => c.SensorsCompleteThroughMs).DefaultIfEmpty(0).Max();
        await db.Runs
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(
                set => set.SetProperty(r => r.PrefixEndSeq, prefixEnd).SetProperty(r => r.PrefixSensorsMs, sensors),
                cancellationToken);
    }

    private static async Task<RunResponse> ToResponseAsync(AppDbContext db, RunEntity run, CancellationToken cancellationToken)
    {
        var chunks = await db.RunChunks.AsNoTracking()
            .Where(c => c.RunId == run.Id)
            .Select(c => new SeqRange(c.FirstSeq, c.LastSeq))
            .ToListAsync(cancellationToken);
        var received = SeqRange.Merge(chunks);
        return new RunResponse(
            run.Id,
            run.League,
            run.Source,
            run.ConfigVersion,
            run.StartedAt.ToUnixTimeMilliseconds(),
            run.EndedAt?.ToUnixTimeMilliseconds(),
            run.Status,
            run.LastSeq,
            run.ProcessedSeq,
            received,
            run.LastSeq is { } last ? SeqRange.Missing(received, last) : [],
            run.Newcomer,
            run.FogNewCells);
    }

    private static bool IsSameStart(RunEntity run, StartRunRequest request) =>
        run.League == request.League
        && run.Source == request.Source
        && run.ConfigVersion == request.ConfigVersion
        && run.StartedAt.ToUnixTimeMilliseconds() == request.StartedAtMs
        && run.DeviceId == request.DeviceId;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static ProblemHttpResult Problem(int status, string code, string title) =>
        TypedResults.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
