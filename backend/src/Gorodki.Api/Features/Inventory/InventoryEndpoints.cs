using System.Security.Claims;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Inventory;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

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
/// «Припасы» и Рюкзак (PLAN.md, §3.11): 1 фишка за каждые 2 км засчитанного пути забега, не больше 4 в игровые сутки, 12
/// ячеек, живут 7 дней до активации; в Сезоне 0 — только «Радар» и «Заморозка серии», по очереди; повтор забега «Припасов»
/// не даёт. Геоданных нет: «Припасы» — за дистанцию, а не за место. Выдача — <see cref="Backpack.GrantAsync"/> (вместе с
/// визитами), «Радар» — в открытии тумана (<c>FogProcessor</c>), «Заморозка серии» — в серии (<c>StreakEndpoints</c>).
/// Задача #140 (E11), сделано Claude 16.10.
/// </summary>
public static class InventoryEndpoints
{
    /// <summary>Ячеек в Рюкзаке (§3.11).</summary>
    public const int Slots = SupplyRules.Slots;

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

    private static async Task<Results<Ok<InventoryResponse>, NotFound>> GetInventory(
        ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } userId || !await db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var items = await Backpack.VisibleAsync(db, userId, time.GetUtcNow(), cancellationToken);
        return TypedResults.Ok(new InventoryResponse(Slots, [.. items.Select(ToResponse)]));
    }

    private static async Task<Results<Ok<InventoryItemResponse>, ProblemHttpResult>> ActivateItem(
        Guid id, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // Своя фишка — условием в том же запросе: чужая — «нет такой» (404), а не 403 (egor-server.md, раздел 4, п. 5).
        var userId = principal.UserId();
        var item = await db.InventoryItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id && i.UserId == userId, cancellationToken);
        if (item is null)
        {
            return Problem(StatusCodes.Status404NotFound, "item_not_found", "Такой фишки нет.");
        }

        // «Активация — только стоя или перед „Стартом“» (§3.11): сервер видит только незавершённый забег. Забег, брошенный
        // телефоном, сервер закрывает сам (RunRetention) — до этого активация ждёт.
        if (await db.Runs.AnyAsync(r => r.UserId == item.UserId && r.Status == RunStatus.Active, cancellationToken))
        {
            return Problem(StatusCodes.Status409Conflict, "run_active", "Идёт забег — фишку можно активировать только вне забега.");
        }

        var now = time.GetUtcNow();
        if (item.ActivatedAt is not null)
        {
            return ItemActive();
        }

        if (item.ExpiresAt <= now)
        {
            return Problem(StatusCodes.Status409Conflict, "item_expired", "Срок фишки истёк.");
        }

        // Одной командой с условием: две активации одновременно не пройдут обе.
        var activated = await db.InventoryItems
            .Where(i => i.Id == item.Id && i.ActivatedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(i => i.ActivatedAt, now), cancellationToken);
        if (activated == 0)
        {
            return ItemActive();
        }

        item.ActivatedAt = now;
        return TypedResults.Ok(ToResponse(item));
    }

    private static InventoryItemResponse ToResponse(InventoryItemEntity item) =>
        new(
            item.Id,
            item.Kind,
            item.ReceivedAt.ToUnixTimeMilliseconds(),
            item.ExpiresAt.ToUnixTimeMilliseconds(),
            item.ActivatedAt is not null,
            item.RemainingMeters);

    private static ProblemHttpResult ItemActive() =>
        Problem(StatusCodes.Status409Conflict, "item_active", "Фишка уже активирована.");

    private static ProblemHttpResult Problem(int status, string code, string title) =>
        TypedResults.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
