using System.Security.Claims;
using System.Text.Json;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Runs;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Gorodki.Api.Features.Captures;

/// <summary>Заявка петли: телефон замкнул петлю и просит сервер её засчитать.</summary>
/// <param name="ClaimNo">Номер заявки в забеге (0, 1, 2…): заявки забега обрабатываются по порядку.</param>
/// <param name="StartSeq">Первая точка петли.</param>
/// <param name="EndSeq">Последняя точка петли — по ней считается идентификатор заявки.</param>
/// <param name="EstimatedArea">Грубая площадь на телефоне, м².</param>
/// <param name="SentAtMs">Момент отправки по часам телефона.</param>
public sealed record LoopClaimRequest(int ClaimNo, int StartSeq, int EndSeq, LoopClosure Closure, double EstimatedArea, long SentAtMs);

/// <summary>Тайл UTM 1×1 км.</summary>
public sealed record TileRef(int X, int Y);

/// <summary>Заявка и её итог.</summary>
/// <param name="WaitingFor">
/// Для ожидающей: чего она ждёт — <c>points</c> (дослать точки до конца петли), <c>sensors</c> (дослать датчики),
/// <c>previous_claim</c> (обрабатывается предыдущая заявка), <c>queue</c> (всё есть, очередь сервера).
/// </param>
/// <param name="RejectCode">Для отказа — причина: стабильный код (docs/architecture/captures.md).</param>
/// <param name="AreaByOutcome">
/// Площадь по видам последствий, м²: <c>claimedNeutral</c>, <c>transferred</c>, <c>cracked</c>… Приходит только после границы
/// публичности (≈20–25 минут после применения), до неё — <c>null</c>: разбивка выдала бы ещё скрытые чужие захваты.
/// </param>
/// <param name="ChangedTiles">Тайлы, которые изменились — их нужно перезапросить.</param>
public sealed record CaptureResponse(
    Guid Id,
    int ClaimNo,
    int StartSeq,
    int EndSeq,
    CaptureStatus Status,
    string? WaitingFor,
    string? RejectCode,
    double AreaSquareMeters,
    IReadOnlyDictionary<string, double>? AreaByOutcome,
    IReadOnlyList<TileRef>? ChangedTiles,
    long? EffectiveAtMs);

/// <summary>Заявки петель (PLAN.md, §3.2, §7.2: «петля → немедленная отправка чанка и /loops»). Подробно — docs/architecture/captures.md.</summary>
public static class CaptureEndpoints
{
    public const string ReadRateLimitPolicy = "reads";

    /// <summary>Заявок в одном забеге — не больше (применённых за сутки всё равно не больше 30).</summary>
    public const int MaxClaimsPerRun = 100;

    public const int MaxClaimsPerDay = 60;

    public static IEndpointRouteBuilder MapCaptureEndpoints(this IEndpointRouteBuilder app)
    {
        var runs = app.MapGroup("/runs/{runId:guid}").WithTags("Захваты");

        runs.MapPost("/loops", ClaimLoop)
            .WithName("claimLoop")
            .RequireRateLimiting(RunLimits.RateLimitPolicy)
            .WithSummary("Заявка петли: сервер засчитает её, когда получит точки и датчики (повтор безопасен)")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        runs.MapGet("/captures", ListCaptures)
            .WithName("listCaptures")
            .RequireRateLimiting(ReadRateLimitPolicy)
            .WithSummary("Все заявки забега: статус, итог, чего ждёт ожидающая")
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<Results<Accepted<CaptureResponse>, Ok<CaptureResponse>, ProblemHttpResult>> ClaimLoop(
        Guid runId,
        LoopClaimRequest request,
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
                r.League,
                r.ConfigVersion,
                r.LastSeq,
                r.CreatedAt,
                Purged = r.PointsPurgedAt != null,
                Deleting = db.Users.Any(u => u.Id == userId && u.DeletionRequestedAt != null),
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (run is null)
        {
            // Правило для телефона — как у кусков: повторить POST /runs и отправить заявку снова.
            return Problem(StatusCodes.Status404NotFound, "run_not_found", "Забег не найден.");
        }

        if (run.Deleting)
        {
            return Problem(StatusCodes.Status403Forbidden, "account_deleting", "Аккаунт удаляется — заявки не принимаются.");
        }

        var config = await configs.GetAsync(run.ConfigVersion, cancellationToken)
            ?? throw new InvalidOperationException($"Нет версии конфига {run.ConfigVersion}, с которой начат забег.");
        if (request.ClaimNo < 0
            || request.ClaimNo >= MaxClaimsPerRun
            || request.StartSeq < 0
            || request.EndSeq - request.StartSeq < 3
            || request.EndSeq > (run.LastSeq ?? RunLimits.MaxSeq(config.Rules))
            || !Enum.IsDefined(request.Closure)
            || !double.IsFinite(request.EstimatedArea)
            || request.EstimatedArea < 0)
        {
            return Problem(StatusCodes.Status400BadRequest, "claim_invalid", "Неверная заявка петли.");
        }

        var now = time.GetUtcNow();
        var horizon = await PublicHorizonAsync(configs, now, cancellationToken);
        var id = CaptureIds.For(runId, request.EndSeq);
        var existing = await db.Captures.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (existing is not null)
        {
            // Повтор той же заявки (ответ мог потеряться) — отдаём её текущее состояние.
            return IsSameClaim(existing, runId, request)
                ? TypedResults.Ok(await DescribeAsync(db, existing, now, horizon, cancellationToken))
                : Problem(StatusCodes.Status409Conflict, "claim_conflict", "Эта петля уже заявлена иначе.");
        }

        // Окно приёма — как у кусков: после него (и после стирания точек через 14 дней) петлю не по чему судить — судья
        // получил бы пустой след, а заявка лишь занимала бы обработчик до too_many_attempts.
        if (now > run.CreatedAt + RunLimits.UploadWindow || run.Purged)
        {
            return Problem(StatusCodes.Status409Conflict, "upload_window_closed", "Заявки этого забега больше не принимаются.");
        }

        var claimsInRun = await db.Captures.CountAsync(c => c.RunId == runId, cancellationToken);
        var claimsToday = await db.Captures.CountAsync(c => c.UserId == userId && c.ReceivedAt > now.AddDays(-1), cancellationToken);
        if (claimsInRun >= MaxClaimsPerRun || claimsToday >= MaxClaimsPerDay)
        {
            return Problem(StatusCodes.Status429TooManyRequests, "claim_limit", "Слишком много заявок петель.");
        }

        var capture = new CaptureEntity
        {
            Id = id,
            RunId = runId,
            UserId = userId,
            League = run.League,
            ClaimNo = request.ClaimNo,
            StartSeq = request.StartSeq,
            EndSeq = request.EndSeq,
            Closure = request.Closure,
            EstimatedArea = request.EstimatedArea,
            Status = CaptureStatus.Pending,
            ReceivedAt = now,
        };
        db.Captures.Add(capture);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Одновременно пришла та же заявка — или другая с тем же номером в забеге.
            db.ChangeTracker.Clear();
            var stored = await db.Captures.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
            return stored is not null && IsSameClaim(stored, runId, request)
                ? TypedResults.Ok(await DescribeAsync(db, stored, now, horizon, cancellationToken))
                : Problem(StatusCodes.Status409Conflict, "claim_conflict", "Заявка с этим номером уже есть.");
        }

        signal.Notify(runId);
        return TypedResults.Accepted($"/runs/{runId}/captures", await DescribeAsync(db, capture, now, horizon, cancellationToken));
    }

    private static async Task<Results<Ok<IReadOnlyList<CaptureResponse>>, ProblemHttpResult>> ListCaptures(
        Guid runId,
        ClaimsPrincipal principal,
        AppDbContext db,
        GameConfigStore configs,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var userId = principal.UserId();
        if (!await db.Runs.AnyAsync(r => r.Id == runId && r.UserId == userId, cancellationToken))
        {
            return Problem(StatusCodes.Status404NotFound, "run_not_found", "Забег не найден.");
        }

        var captures = await db.Captures.AsNoTracking()
            .Where(c => c.RunId == runId)
            .OrderBy(c => c.ClaimNo)
            .ToListAsync(cancellationToken);
        var horizon = await PublicHorizonAsync(configs, now, cancellationToken);
        var described = new List<CaptureResponse>(captures.Count);
        foreach (var capture in captures)
        {
            described.Add(await DescribeAsync(db, capture, now, horizon, cancellationToken));
        }

        return TypedResults.Ok<IReadOnlyList<CaptureResponse>>(described);
    }

    /// <summary>Граница публичности (§3.16) — та же, что у карты: «сейчас − 20 минут» вниз до 5 минут.</summary>
    private static async Task<DateTimeOffset> PublicHorizonAsync(GameConfigStore configs, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMinutes((await configs.GetCurrentAsync(cancellationToken)).Rules.Privacy.PublicEventDelayMinutes);
        return TerritoryReader.PublicHorizon(now, delay);
    }

    /// <summary>Заявка для приложения; у ожидающей — чего она ждёт (те же правила, что у обработчика).</summary>
    /// <param name="publicHorizon">Граница публичности: разбивка площади — только у захватов, применённых не позже неё.</param>
    internal static async Task<CaptureResponse> DescribeAsync(
        AppDbContext db, CaptureEntity capture, DateTimeOffset now, DateTimeOffset publicHorizon, CancellationToken cancellationToken)
    {
        string? waitingFor = null;
        if (capture.Status == CaptureStatus.Pending)
        {
            var run = await db.Runs.AsNoTracking()
                .Where(r => r.Id == capture.RunId)
                .Select(r => new { r.PrefixEndSeq, r.PrefixSensorsMs, r.LastSeq, r.Status })
                .SingleAsync(cancellationToken);
            var endChunkLastPointMs = await db.RunChunks.AsNoTracking()
                .Where(c => c.RunId == capture.RunId && c.FirstSeq <= capture.EndSeq && c.LastSeq >= capture.EndSeq)
                .Select(c => (long?)c.LastPointMs)
                .SingleOrDefaultAsync(cancellationToken);
            var earlierWaiting = now - capture.ReceivedAt < ClaimReadiness.PreviousClaimPatience
                && await db.Captures.AnyAsync(
                    c => c.RunId == capture.RunId && c.ClaimNo < capture.ClaimNo && c.Status == CaptureStatus.Pending,
                    cancellationToken);
            var wait = ClaimReadiness.Check(
                capture.EndSeq,
                run.PrefixEndSeq,
                run.PrefixSensorsMs,
                endChunkLastPointMs,
                runComplete: run.LastSeq is { } last && run.PrefixEndSeq >= last,
                earlierWaiting);
            waitingFor = ClaimReadiness.Code(wait);
        }

        // Разбивка по видам — только когда граница публичности дошла до применения захвата (docs/architecture/run-hud.md,
        // «Приватность итога заявки»). CaptureRules.Decide решает по настоящей земле, и до границы `transferred` вместо
        // `claimedNeutral`, `shielded`, `superseded`… выдали бы автору ещё скрытый чужой захват. Прячем всегда, а не только
        // когда петля задела скрытое, — иначе его выдаёт сама разница «есть / нет». Граница — момент применения, как у
        // TerritoryReader.HiddenJournal: опоздавшую петлю применяют по сегодняшней земле. «Взятое» (AreaSquareMeters) —
        // сразу: это ровно земля, ставшая его, а её автор и так видит на своей карте без задержки.
        var areas = capture.AreaByOutcome is { } json && capture.AppliedAt is { } appliedAt && appliedAt <= publicHorizon
            ? JsonSerializer.Deserialize<Dictionary<string, double>>(json)
            : null;

        return new CaptureResponse(
            capture.Id,
            capture.ClaimNo,
            capture.StartSeq,
            capture.EndSeq,
            capture.Status,
            waitingFor,
            capture.RejectCode,
            capture.AreaSquareMeters,
            areas,
            capture.ChangedTiles is { } tiles
                ? JsonSerializer.Deserialize<int[][]>(tiles)!.Select(t => new TileRef(t[0], t[1])).ToList()
                : null,
            capture.EffectiveAt?.ToUnixTimeMilliseconds());
    }

    private static bool IsSameClaim(CaptureEntity capture, Guid runId, LoopClaimRequest request) =>
        capture.RunId == runId
        && capture.ClaimNo == request.ClaimNo
        && capture.StartSeq == request.StartSeq
        && capture.EndSeq == request.EndSeq;

    private static ProblemHttpResult Problem(int status, string code, string title) =>
        TypedResults.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
