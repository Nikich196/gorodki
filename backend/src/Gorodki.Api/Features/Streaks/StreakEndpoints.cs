using System.Security.Claims;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Streaks;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

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
/// игровые сутки по Минску, в которые живые забеги игрока засчитали не меньше километра пути (повтор демо не считается).
/// Правило — чистая функция <see cref="StreakRules"/> (<c>Gorodki.Domain</c>); здесь — её данные из базы. Ничего не
/// хранится: серия считается по забегам при каждом запросе. Очков за серию пока нет (§3.5, «серия (+10)» — после показа).
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

    /// <summary>
    /// Серия игрока на момент <paramref name="now"/>; <c>null</c> — игрока нет. Для адреса и для того, кому серия нужна
    /// целиком (например, Рюкзаку #140 — какие заморозки уже потрачены: <see cref="StreakState.FrozenDays"/>).
    /// </summary>
    public static async Task<StreakState?> StateAsync(AppDbContext db, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            return null;
        }

        // Свои живые забеги — одним запросом. С путём 0 день серии не наберётся — их не читаем. Без пути (идёт или ждёт
        // границы публичности) — читаем: такой забег держит вчерашний день, пока путь не посчитан (StreakRules). Сутки — по
        // часам сервера, как у очков за дистанцию: у телефона с отстающими часами забег в 00:10 иначе ушёл бы во вчера.
        var runs = await db.Runs.AsNoTracking()
            .Where(r => r.UserId == userId && r.Source == RunSource.Live && (r.AcceptedMeters == null || r.AcceptedMeters > 0))
            .Select(r => new { r.StartedAt, r.ClockSkewMs, r.AcceptedMeters })
            .ToListAsync(cancellationToken);
        var freezes = await FreezesActivatedAtAsync(db, userId, cancellationToken);
        return StreakRules.Compute(
            runs.Select(r => new StreakRun(r.StartedAt.AddMilliseconds(-r.ClockSkewMs), r.AcceptedMeters)), freezes, now);
    }

    private static async Task<Results<Ok<StreakResponse>, NotFound>> GetStreak(
        ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } userId || await StateAsync(db, userId, time.GetUtcNow(), cancellationToken) is not { } streak)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(new StreakResponse(streak.Days, streak.TodayCounted, streak.FreezeActive));
    }

    /// <summary>
    /// Когда игрок активировал «Заморозки серии» — единственное место, откуда серия их берёт. Заморозки выдаёт Рюкзак
    /// (§3.11), его ещё нет (задача #140): пока заморозок нет ни у кого, <c>freezeActive</c> — всегда <c>false</c>, а серия
    /// работает без них.
    /// </summary>
    private static Task<IReadOnlyList<DateTimeOffset>> FreezesActivatedAtAsync(
        AppDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #140: моменты активации своих фишек ItemKind.StreakFreeze из inventory_items — одним запросом, например
        // db.InventoryItems.Where(i => i.UserId == userId && i.Kind == ItemKind.StreakFreeze && i.ActivatedAt != null)
        //     .Select(i => i.ActivatedAt!.Value).ToListAsync(cancellationToken).
        // Тратит их StreakRules: первую по порядку активации — на первый пропуск при живой серии; потраченные —
        // StreakState.FrozenDays (их число — сколько первых активаций израсходовано). Активированная заморозка ждёт
        // пропуска: 7 дней срока Рюкзака идут до активации (решено 15.10, можно пересмотреть). Тесты — StreakRulesTests
        // (без Docker) и StreakTests: добавить «заморозка спасает пропуск» через настоящую активацию.
        _ = (db, userId, cancellationToken);
        return Task.FromResult<IReadOnlyList<DateTimeOffset>>([]);
    }
}
