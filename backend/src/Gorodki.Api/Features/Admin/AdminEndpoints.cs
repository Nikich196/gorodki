using System.Security.Claims;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Admin;

/// <summary>Просьба откатить захваты игрока: причина обязательна — это журнал решений (PLAN.md, §3.9, слой 5).</summary>
public sealed record RollbackRequest(string Reason);

/// <summary>Задание отката и его итог.</summary>
public sealed record RollbackResponse(
    Guid Id,
    Guid UserId,
    string Reason,
    DateTimeOffset RequestedAt,
    CaptureRollbackStatus Status,
    DateTimeOffset? FinishedAt,
    int RolledBack,
    int WithoutJournal,
    int Failed,
    double RestoredArea,
    double SkippedArea,
    DateTimeOffset? FrozenUntil);

/// <summary>
/// Адреса администратора. Не входят в описание API для приложения (openapi.v1.json): ими пользуется только админ.
/// Роль проверяется по базе, а не по токену: снятая роль действует сразу.
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin").ExcludeFromDescription();
        admin.MapPost("/users/{userId:guid}/rollback", RequestRollback);
        admin.MapGet("/rollbacks/{rollbackId:guid}", GetRollback);
        return app;
    }

    /// <summary>
    /// Заморозить игрока на 7 дней и откатить его захваты (за неделю — дальше журнала нет). Откат выполняет фоновый
    /// обработчик; ответ — задание, его итог — по адресу <c>/admin/rollbacks/{id}</c>. Повтор, пока задание ждёт, вернёт его же.
    /// </summary>
    private static async Task<Results<Accepted<RollbackResponse>, Ok<RollbackResponse>, ProblemHttpResult>> RequestRollback(
        Guid userId,
        RollbackRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        CaptureSignal signal,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (await AdminIdAsync(principal, db, cancellationToken) is not { } adminId)
        {
            return Problem(StatusCodes.Status403Forbidden, "admin_only", "Только для администратора.");
        }

        var reason = request.Reason?.Trim() ?? "";
        if (reason.Length is < 3 or > 200)
        {
            return Problem(StatusCodes.Status400BadRequest, "reason_required", "Нужна причина — от 3 до 200 символов.");
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return Problem(StatusCodes.Status404NotFound, "user_not_found", "Игрок не найден.");
        }

        var now = time.GetUtcNow();
        var frozenUntil = now + CaptureRollback.FreezeFor;
        if (user.FrozenUntil is null || user.FrozenUntil < frozenUntil)
        {
            user.FrozenUntil = frozenUntil; // заморозка — до постановки задания: новые захваты игрока на карту не лягут
        }

        var pending = await db.CaptureRollbacks.AsNoTracking()
            .SingleOrDefaultAsync(r => r.UserId == userId && r.Status == CaptureRollbackStatus.Pending, cancellationToken);
        if (pending is not null)
        {
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok(ToResponse(pending, user.FrozenUntil));
        }

        var job = new CaptureRollbackEntity
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            RequestedBy = adminId,
            Reason = reason,
            RequestedAt = now,
            Status = CaptureRollbackStatus.Pending,
        };
        db.CaptureRollbacks.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        signal.Notify(Guid.Empty); // разбудить обработчик
        return TypedResults.Accepted($"/admin/rollbacks/{job.Id}", ToResponse(job, user.FrozenUntil));
    }

    private static async Task<Results<Ok<RollbackResponse>, ProblemHttpResult>> GetRollback(
        Guid rollbackId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        if (await AdminIdAsync(principal, db, cancellationToken) is null)
        {
            return Problem(StatusCodes.Status403Forbidden, "admin_only", "Только для администратора.");
        }

        var job = await db.CaptureRollbacks.AsNoTracking().SingleOrDefaultAsync(r => r.Id == rollbackId, cancellationToken);
        if (job is null)
        {
            return Problem(StatusCodes.Status404NotFound, "rollback_not_found", "Задание не найдено.");
        }

        var frozenUntil = await db.Users.Where(u => u.Id == job.UserId).Select(u => u.FrozenUntil).SingleOrDefaultAsync(cancellationToken);
        return TypedResults.Ok(ToResponse(job, frozenUntil));
    }

    private static async Task<Guid?> AdminIdAsync(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } userId)
        {
            return null;
        }

        var isAdmin = await db.Users.AnyAsync(u => u.Id == userId && u.Role == UserRole.Admin, cancellationToken);
        return isAdmin ? userId : null;
    }

    private static RollbackResponse ToResponse(CaptureRollbackEntity job, DateTimeOffset? frozenUntil) => new(
        job.Id,
        job.UserId,
        job.Reason,
        job.RequestedAt,
        job.Status,
        job.FinishedAt,
        job.RolledBack,
        job.WithoutJournal,
        job.Failed,
        job.RestoredArea,
        job.SkippedArea,
        frozenUntil);

    private static ProblemHttpResult Problem(int status, string code, string title) =>
        TypedResults.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
