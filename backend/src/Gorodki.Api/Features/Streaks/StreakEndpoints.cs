using System.Security.Claims;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Gorodki.Api.Features.Streaks;

/// <summary>Серия (PLAN.md, §3.7).</summary>
/// <param name="Days">Длина текущей серии, дней; 0 — серии нет.</param>
/// <param name="TodayCounted">Сегодняшние игровые сутки (по Минску) уже засчитаны в серию.</param>
/// <param name="FreezeActive">
/// Активирована «Заморозка серии» (Рюкзак, §3.11): следующий пропущенный день серию не сожжёт.
/// </param>
public sealed record StreakResponse(int Days, bool TodayCounted, bool FreezeActive);

/// <summary>
/// Серия дней с забегами (PLAN.md, §3.7: «серия с „заморозкой“», без ночных требований; §3.5: «серия (+10)»). День серии —
/// игровые сутки по Минску, в которые завершён хотя бы один живой забег с засчитанным путём (повтор демо не считается);
/// порог пути, если нужен, — уточнит Никита. Правило — чистая функция в <c>Gorodki.Domain</c> (<c>GameClock</c>). Задача
/// Егора E22 (docs/guides/egor-server.md, раздел 7).
/// </summary>
public static class StreakEndpoints
{
    public static IEndpointRouteBuilder MapStreakEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/me/streak", GetStreak)
            .WithName("getStreak")
            .WithTags("Профиль")
            .WithSummary("Своя серия: дней подряд с забегом, засчитан ли сегодняшний день, действует ли «Заморозка серии»")
            .RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy);
        return app;
    }

    private static Task<Results<Ok<StreakResponse>, NotFound>> GetStreak(
        ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #TBD-E22 (Егор): правило серии — чистая функция (Gorodki.Domain, например Streaks/StreakRules): по игровым
        // суткам своих забегов (GameClock.GameDayOf(StartedAt), Source = Live, Status != Active, AcceptedMeters > 0) и дням,
        // закрытым «Заморозкой серии» (фишка StreakFreeze из E11), — длина серии на сегодня. Вчерашний день ещё не пропущен,
        // пока идут сегодняшние сутки. Игрока нет — 404. Тесты — StreakRulesTests (без Docker) и StreakTests.
        _ = (principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #TBD-E22");
    }
}
