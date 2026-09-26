using System.Security.Claims;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Gorodki.Api.Features.Collection;

/// <summary>Редкость значка (PLAN.md, §3.7, §3.12).</summary>
public enum BadgeRarity
{
    Common,
    Rare,
    Epic,
    Legendary,
}

/// <summary>Значок тайника в «Коллекции» — без координат.</summary>
/// <param name="Name">Название значка; <c>null</c> — ещё не найден (на экране силуэт).</param>
/// <param name="Hint">Подсказка-загадка о месте; проясняется, когда игрок открывает туман рядом (текст уже с учётом этого).</param>
/// <param name="FoundAtMs">Когда найден (мс Unix); <c>null</c> — не найден.</param>
/// <param name="FinderNumber">Каким по счёту игрок его нашёл («№ N нашедших»); <c>null</c> — не найден.</param>
/// <param name="GoldFrame">Игрок нашёл первым — золотая рамка.</param>
public sealed record CacheBadgeResponse(
    Guid Id, BadgeRarity Rarity, string? Name, string Hint, long? FoundAtMs, int? FinderNumber, bool GoldFrame);

/// <summary>«Коллекция»: все тайники текущего набора — найденные и силуэты.</summary>
public sealed record CollectionResponse(IReadOnlyList<CacheBadgeResponse> Caches);

/// <summary>
/// «Коллекция» (PLAN.md, §3.12): ~40 тайников — обычные 20, редкие 12, эпические 6, легендарные 2. <b>Координат тайников на
/// телефон нет</b>, пока тайник не найден, — и после находки тоже (здесь только числа и тексты): клиент получает подсказки.
/// Находку засчитывает сервер (пройти в 30 м в валидном забеге) — это C12 (тайники в конвейере). Задача Егора E15
/// (docs/guides/egor-server.md, раздел 7).
/// </summary>
public static class CollectionEndpoints
{
    public static IEndpointRouteBuilder MapCollectionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/collection", GetCollection)
            .WithName("getCollection")
            .WithTags("Коллекция")
            .WithSummary("Коллекция тайников: найденные значки и силуэты с подсказками — без координат")
            .WithDescription(
                "Все тайники текущего сезона: найденный — название, дата, «№ N нашедших», золотая рамка первому; ненайденный — "
                + "только редкость и подсказка-загадка, которая проясняется, когда открываешь туман рядом.")
            .RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy);
        return app;
    }

    private static Task<Results<Ok<CollectionResponse>, NotFound>> GetCollection(
        ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #145 (Егор): тайники текущего сезона (таблицу и находки даёт C12) и находки игрока. Ни широты, ни долготы
        // в ответе — тест обязан это проверять (egor-server.md, раздел 4, п. 2). Ясность подсказки — по своему туману рядом
        // с тайником (правило уточнит заготовка C12). Игрока нет — 404. Тесты — CollectionTests.
        _ = (principal, db, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #145");
    }
}
