using System.Security.Claims;
using System.Text.Json.Serialization;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Gorodki.Api.Features.Inbox;

/// <summary>Вид события — в порядке приоритета уведомлений (PLAN.md, §3.17).</summary>
public enum InboxKind
{
    /// <summary>Нападение и осада: твою землю взяли или треснули.</summary>
    Attack,

    /// <summary>Тебя свергли с трона на отрезке («Короли участков», §3.13).</summary>
    Dethroned,

    /// <summary>Дуэль: вызов, смена лидера, итог (§3.14).</summary>
    Duel,

    /// <summary>Рейд (§3.6).</summary>
    Raid,

    /// <summary>Конец сезона.</summary>
    SeasonEnd,

    /// <summary>Угасание земли.</summary>
    Decay,

    /// <summary>Серия.</summary>
    Streak,
}

/// <summary>Событие во «Входящих».</summary>
/// <param name="Id">Номер события: случайный (<c>Guid.NewGuid()</c>), не по времени — по нему не узнать, когда оно случилось.</param>
/// <param name="Text">Готовый текст без рода (§3.17) — телефон показывает как есть.</param>
/// <param name="AtMs">
/// Когда событие стало видно (мс Unix) — не время самого захвата: событие о чужом захвате появляется не раньше границы
/// публичности.
/// </param>
public sealed record InboxItem(Guid Id, InboxKind Kind, string Text, long AtMs, bool Read);

/// <summary>Страница «Входящих», новые сверху.</summary>
/// <param name="NextCursor">Курсор следующей (более старой) страницы; <c>null</c> — дальше ничего нет.</param>
/// <param name="Unread">Непрочитанных всего — для значка на вкладке.</param>
public sealed record InboxResponse(IReadOnlyList<InboxItem> Items, string? NextCursor, int Unread);

/// <summary>Отметить прочитанным.</summary>
public sealed record InboxReadRequest
{
    /// <summary>Все события с <c>atMs</c> не позже этого (мс Unix) — обычно <c>atMs</c> самого нового на экране.</summary>
    [JsonRequired]
    public required long UpToAtMs { get; init; }
}

/// <summary>
/// «Входящие» (PLAN.md, §3.17): события о нападениях, коронах, дуэлях, конце сезона, угасании и серии. Показываются только
/// события с <c>visible_at ≤ now</c>: о чужом захвате — не раньше границы публичности (помощник
/// <c>TerritoryReader.VisibleAtAsync</c>). Бюджет пуш-уведомлений (3 в день, напоминаний ≤ 1, тишина 22–08 по Минску) —
/// чистая функция <c>NotificationBudget</c>, сами пуши — заглушка <c>IPushSender</c>; во «Входящие» попадает всё, бюджет
/// ограничивает только пуши. Задача Егора E10, опора — события после границы (C10).
/// </summary>
public static class InboxEndpoints
{
    /// <summary>Событий на странице.</summary>
    public const int PageSize = 50;

    public static IEndpointRouteBuilder MapInboxEndpoints(this IEndpointRouteBuilder app)
    {
        var inbox = app.MapGroup("/inbox").WithTags("Входящие").RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy);
        inbox.MapGet("", GetInbox)
            .WithName("getInbox")
            .WithSummary("Входящие: свои события, новые сверху, по 50; cursor — из nextCursor прошлой страницы")
            .WithDescription(
                "Только события, которые уже можно показать: о чужом захвате твоей земли — не раньше, чем его покажет карта "
                + "(граница публичности). Без координат — только текст. Битый курсор — 400 inbox_cursor_invalid.")
            .ProducesProblem(StatusCodes.Status400BadRequest);
        inbox.MapPost("/read", MarkInboxRead)
            .WithName("markInboxRead")
            .WithSummary("Отметить прочитанными все события не новее upToAtMs");
        return app;
    }

    private static Task<Results<Ok<InboxResponse>, ProblemHttpResult>> GetInbox(
        string? cursor, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #TBD-E10 (Егор): таблица событий (игрок, вид, текст, visible_at, прочитано). Только свои и только
        // visible_at ≤ now; порядок — visible_at по убыванию, затем номер; курсор — (visible_at, номер) последнего на странице.
        // Номер события — Guid.NewGuid(), не CreateVersion7: в v7 зашито время создания. Тесты — InboxTests.
        _ = (cursor, principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #TBD-E10");
    }

    private static Task<NoContent> MarkInboxRead(
        InboxReadRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #TBD-E10 (Егор): одна команда ExecuteUpdateAsync по своим событиям с visible_at ≤ min(upToAtMs, now) —
        // ещё скрытые не отмечаются. Повтор ничего не меняет, ответ — 204. Тесты — InboxTests.
        _ = (request, principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #TBD-E10");
    }
}
