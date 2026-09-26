using System.Security.Claims;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Gorodki.Api.Features.Admin;

/// <summary>Подозрительный забег для админа.</summary>
/// <param name="Score">Оценка доверия 0–1: чем меньше, тем подозрительнее (<c>TrustSignals</c>).</param>
/// <param name="Signals">Сработавшие признаки (коды, например <c>same_accuracy</c>, <c>perfect_intervals</c>, <c>no_steps</c>).</param>
public sealed record SuspiciousRunResponse(Guid RunId, Guid UserId, long StartedAtMs, double Score, IReadOnlyList<string> Signals);

/// <summary>Бан игрока: причина обязательна — это журнал решений (§3.9, слой 5).</summary>
public sealed record BanRequest(string Reason);

/// <summary>Игрок забанен.</summary>
public sealed record BanResponse(Guid UserId, string Reason, long BannedAtMs);

/// <summary>
/// Оценка доверия и наказания (PLAN.md, §3.9, слои 3 и 5): признаки забега (одинаковая точность у всех точек, идеальные
/// интервалы, нет шагов, повтор чужой формы маршрута, &gt;60 км в день пешком) → оценка и флаги забега; наказания — теневой бан
/// → заморозка 7 дней (уже есть: откат, <see cref="AdminEndpoints"/>) → бан. Адреса администратора — в описание API для
/// приложения не входят, экранов нет. Задача Егора E13 (docs/guides/egor-server.md, раздел 7).
/// </summary>
public static class TrustEndpoints
{
    public static RouteGroupBuilder MapTrustEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapGet("/runs/suspicious", ListSuspiciousRuns);
        admin.MapPost("/users/{userId:guid}/ban", BanUser);
        return admin;
    }

    /// <summary>Подозрительные забеги за 7 дней, самые подозрительные сверху.</summary>
    private static Task<Results<Ok<IReadOnlyList<SuspiciousRunResponse>>, ProblemHttpResult>> ListSuspiciousRuns(
        ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #TBD-E13 (Егор): только админ (AdminEndpoints.AdminIdAsync; иначе 403 admin_only). TrustSignals — чистая
        // функция в Gorodki.Domain по признакам забега (§3.9, слой 3); оценка и флаги пишутся в забег после его приёма.
        // Тесты — TrustSignalsTests (без Docker), AdminTrustTests.
        _ = (principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #TBD-E13");
    }

    /// <summary>Забанить игрока: вход и приём забегов закрыты.</summary>
    private static Task<Results<Ok<BanResponse>, ProblemHttpResult>> BanUser(
        Guid userId, BanRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #TBD-E13 (Егор): только админ (иначе 403 admin_only); причина 3–200 символов (400 reason_required, как в
        // RequestRollback); нет игрока — 404 user_not_found. Бан: вход и обновление токенов закрыты, refresh-токены стёрты
        // (как в DELETE /me), старт забега — 403 account_banned. Откат земли — отдельно, POST /admin/users/{id}/rollback. Тесты —
        // AdminTrustTests; строка в IdorTests.AwaitingTasks.
        _ = (userId, request, principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #TBD-E13");
    }
}
