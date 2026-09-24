using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Auth;

/// <summary>
/// Истёкшие refresh-токены стираются раз в час (фоновый обработчик). Каждый вход и каждое обновление (раз в 15 минут на
/// телефон) добавляют строку, и без чистки таблица росла бы без предела, а «Мои данные» выгружали бы всю историю входов.
/// </summary>
/// <remarks>
/// Истёкший токен ни на что не влияет: обновление по нему — сразу <c>refresh_invalid</c>, а «живой ли вход» и повтор
/// заменённого токена смотрят только на неистёкшие. Отозванные, но не истёкшие (заменённые) остаются до конца срока:
/// по ним сервер узнаёт повтор украденного токена (<c>refresh_reused</c>).
/// </remarks>
public sealed class RefreshTokenRetention(AppDbContext db, TimeProvider time)
{
    /// <summary>Стирает токены, срок которых вышел. Возвращает, сколько стёрто.</summary>
    public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        return db.RefreshTokens.Where(t => t.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
    }
}
