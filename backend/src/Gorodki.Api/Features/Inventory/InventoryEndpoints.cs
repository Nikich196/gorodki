using System.Security.Claims;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Gorodki.Api.Features.Inventory;

/// <summary>Фишка Сезона 0 — полезная или косметическая (PLAN.md, §3.11).</summary>
public enum ItemKind
{
    /// <summary>«Магнит»: радиус замыкания петли +20 м на 2 км.</summary>
    Magnet,

    /// <summary>«Радар»: радиус открытия тумана ×2 на 3 км.</summary>
    Radar,

    /// <summary>«Компас»: направление к ближайшему тайнику или сундуку.</summary>
    Compass,

    /// <summary>«Заморозка серии»: серия не сгорает за пропущенный день.</summary>
    StreakFreeze,

    /// <summary>«Сундук»: стиль следа, рамка аватара, эмблема, XP.</summary>
    Chest,
}

/// <summary>Фишка в Рюкзаке.</summary>
/// <param name="ReceivedAtMs">Когда получена (мс Unix).</param>
/// <param name="ExpiresAtMs">Когда пропадёт, если не использовать: через 7 дней после получения (мс Unix).</param>
/// <param name="Active">Активирована: действует сейчас или ждёт своего забега.</param>
/// <param name="RemainingMeters">
/// Сколько ещё действует, м — у фишек, чей эффект меряется дистанцией («Магнит», «Радар»); иначе <c>null</c>.
/// </param>
public sealed record InventoryItemResponse(
    Guid Id, ItemKind Kind, long ReceivedAtMs, long ExpiresAtMs, bool Active, double? RemainingMeters);

/// <summary>Рюкзак.</summary>
/// <param name="Slots">Ячеек в Рюкзаке (12): полный Рюкзак новых «Припасов» не принимает.</param>
/// <param name="Items">Фишки, новые сверху; истёкшие не показываются.</param>
public sealed record InventoryResponse(int Slots, IReadOnlyList<InventoryItemResponse> Items);

/// <summary>
/// «Припасы» и Рюкзак (PLAN.md, §3.11): 1 фишка за каждые 2 км валидного движения, не больше 4 в сутки, 12 ячеек, живут 7
/// дней; тип — детерминированно от номера забега, повтор забега «Припасов» не даёт. Геоданных нет: «Припасы» — за дистанцию,
/// а не за место. Задача Егора E11 (docs/guides/egor-server.md, раздел 7).
/// </summary>
public static class InventoryEndpoints
{
    /// <summary>Ячеек в Рюкзаке (§3.11).</summary>
    public const int Slots = 12;

    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        var inventory = app.MapGroup("/me/inventory").WithTags("Рюкзак").RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy);
        inventory.MapGet("", GetInventory)
            .WithName("getInventory")
            .WithSummary("Рюкзак: фишки, сроки, что активировано");
        inventory.MapPost("/{id:guid}/activate", ActivateItem)
            .WithName("activateItem")
            .WithSummary("Активировать фишку — только вне забега (стоя или перед «Стартом»)")
            .WithDescription(
                "Идёт забег — 409 run_active; фишка истекла — 409 item_expired; уже активирована — 409 item_active. Нет такой "
                + "фишки у игрока — 404 item_not_found. Ответ — фишка после активации.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }

    private static Task<Results<Ok<InventoryResponse>, NotFound>> GetInventory(
        ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #140 (Егор): таблица inventory_items (игрок, вид, получена, истекает, активирована, осталось метров,
        // забег-источник); миграция, удаление и выгрузка (egor-server.md, 2.4). Выдача «Припасов» — после подсчёта метров
        // забега (рядом с VisitProcessor): 1 за 2 км, до 4 в игровые сутки по Минску, не больше 12 ячеек, повтор (Replay) не
        // даёт, тип — детерминированно от номера забега. Здесь — свои неистёкшие фишки. Тесты — InventoryTests.
        _ = (principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #140");
    }

    private static Task<Results<Ok<InventoryItemResponse>, ProblemHttpResult>> ActivateItem(
        Guid id, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #140 (Егор): фишка ищется среди своих одним запросом (чужая — 404 item_not_found, egor-server.md,
        // раздел 4, п. 5). Активный забег игрока (RunStatus.Active) — 409 run_active. Тесты — InventoryTests; строка в
        // IdorTests.AwaitingTasks.
        _ = (id, principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #140");
    }
}
