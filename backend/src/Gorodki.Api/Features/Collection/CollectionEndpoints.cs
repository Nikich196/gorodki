using System.Security.Claims;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

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
/// <param name="Hint">
/// Подсказка-загадка о месте. По уменьшенному плану (16.10) она одна; «проясняется, когда открываешь туман рядом» (так
/// в описании адреса) — после показа: вторая, ясная подсказка по своему туману в ~150 м (docs/architecture/caches.md).
/// </param>
/// <param name="FoundAtMs">Когда найден (мс Unix); <c>null</c> — не найден.</param>
/// <param name="FinderNumber">
/// Каким по счёту игрок его нашёл («№ N нашедших») среди находок, уже видимых другим; <c>null</c> — не найден.
/// </param>
/// <param name="GoldFrame">Игрок нашёл первым — золотая рамка.</param>
public sealed record CacheBadgeResponse(
    Guid Id, BadgeRarity Rarity, string? Name, string Hint, long? FoundAtMs, int? FinderNumber, bool GoldFrame);

/// <summary>«Коллекция»: все тайники текущего набора — найденные и силуэты.</summary>
public sealed record CollectionResponse(IReadOnlyList<CacheBadgeResponse> Caches);

/// <summary>
/// «Коллекция» (PLAN.md, §3.12): тайники набора — по уменьшенному плану (16.10) 20: обычных 10, редких 6, эпических 3,
/// легендарный 1 (<see cref="CacheSeed"/>, docs/architecture/caches.md). <b>Координат тайников на телефон нет</b>, пока
/// тайник не найден, — и после находки тоже (здесь только числа и тексты): клиент получает подсказки. Находку засчитывает
/// сервер (пройти в 30 м в валидном забеге, <see cref="CacheProcessor"/>). Задача #145 (E15), сделано Claude 16.10.
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

    private static async Task<Results<Ok<CollectionResponse>, NotFound>> GetCollection(
        ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } userId || !await db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var now = time.GetUtcNow();
        var caches = await db.Caches.AsNoTracking()
            .OrderByDescending(c => c.Rarity)
            .ThenBy(c => c.Number)
            .Select(c => new { c.Id, c.Rarity, c.Name, c.Hint })
            .ToListAsync(cancellationToken);
        var mine = await db.CacheFinds.AsNoTracking()
            .Where(f => f.UserId == userId)
            .Select(f => new FindKey(f.CacheId, f.Id, f.FoundAt, f.VisibleAt))
            .ToDictionaryAsync(f => f.CacheId, cancellationToken);

        // Чужие находки тех же тайников — только уже видимые другим (граница публичности) и только порядок: ни времени, ни
        // места, ни того, кто нашёл, в ответе нет.
        var foundIds = mine.Keys.ToList();
        var others = await db.CacheFinds.AsNoTracking()
            .Where(f => foundIds.Contains(f.CacheId) && f.UserId != userId && f.VisibleAt <= now)
            .Select(f => new FindKey(f.CacheId, f.Id, f.FoundAt, f.VisibleAt))
            .ToListAsync(cancellationToken);

        var badges = caches.Select(c =>
        {
            if (!mine.TryGetValue(c.Id, out var find))
            {
                return new CacheBadgeResponse(c.Id, c.Rarity, null, c.Hint, null, null, false);
            }

            var number = FinderNumber(find, others.Where(o => o.CacheId == c.Id));
            return new CacheBadgeResponse(c.Id, c.Rarity, c.Name, c.Hint, find.FoundAt.ToUnixTimeMilliseconds(), number, number == 1);
        });
        return TypedResults.Ok(new CollectionResponse([.. badges]));
    }

    /// <summary>Находка — для номера нашедших: порядок, а не время, наружу не выходит.</summary>
    private sealed record FindKey(Guid CacheId, Guid Id, DateTimeOffset FoundAt, DateTimeOffset VisibleAt);

    /// <summary>
    /// «№ N нашедших» (§3.12) — каким по счёту игрок нашёл тайник среди находок, <b>уже видимых другим</b>
    /// (<see cref="CacheFindEntity.VisibleAt"/> не позже «сейчас»). Порядок — по моменту видимости, при равенстве (граница
    /// идёт шагами по 5 минут) — по времени находки, потом по номеру. Поэтому номер растёт, только пока своя находка ещё
    /// не стала видимой другим (до ~25 минут после записи: чужая находка, записанная раньше, ещё скрыта), а дальше
    /// не меняется — удаление аккаунта нашедшего раньше сдвигает его вниз. Номер 1 — золотая рамка.
    /// </summary>
    private static int FinderNumber(FindKey mine, IEnumerable<FindKey> visibleOthers) =>
        1 + visibleOthers.Count(o => o.VisibleAt < mine.VisibleAt
            || (o.VisibleAt == mine.VisibleAt && (o.FoundAt < mine.FoundAt || (o.FoundAt == mine.FoundAt && o.Id.CompareTo(mine.Id) < 0))));
}
