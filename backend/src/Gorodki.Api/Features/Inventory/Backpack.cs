using Gorodki.Api.Features.Streaks;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Inventory;
using Gorodki.Domain.Time;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Inventory;

/// <summary>
/// Рюкзак (PLAN.md, §3.11) на сервере: что лежит в ячейках, выдача «Припасов» и расход «Радара». Числа — <see cref="SupplyRules"/>.
/// </summary>
/// <remarks>
/// <para>
/// В Сезоне 0 выдаются только «Радар» и «Заморозка серии» (решение 16.10: «Магнит», «Компас», «Сундук» — после показа),
/// по очереди. Фишка лежит в Рюкзаке и занимает ячейку, пока она не истекла (7 дней до активации) и не израсходована:
/// активированный «Радар» — пока остались метры, активированная «Заморозка серии» — пока её не потратила серия (она
/// ждёт пропуска сколько угодно: 7 дней идут только до активации — решено 15.10).
/// </para>
/// <para>
/// «Ночью фишек нет» (§3.7, 23:00–06:00) — не сделано: «Припасы» выдаются за дистанцию после забега, а не на карте.
/// </para>
/// </remarks>
public static class Backpack
{
    /// <summary>Фишки в Рюкзаке игрока сейчас — новые сверху; истёкшие и израсходованные не входят.</summary>
    public static async Task<List<InventoryItemEntity>> VisibleAsync(
        AppDbContext db, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var items = await db.InventoryItems.AsNoTracking()
            .Where(i => i.UserId == userId
                && ((i.ActivatedAt == null && i.ExpiresAt > now)
                    || (i.ActivatedAt != null && (i.Kind != ItemKind.Radar || i.RemainingMeters > 0))))
            .OrderByDescending(i => i.ReceivedAt)
            .ThenBy(i => i.Id)
            .ToListAsync(cancellationToken);
        if (!items.Any(i => i.Kind == ItemKind.StreakFreeze && i.ActivatedAt != null))
        {
            return items;
        }

        // Потраченные заморозки — первые по порядку активации (так их тратит StreakRules).
        var spent = (await StreakEndpoints.StateAsync(db, userId, now, cancellationToken))?.FrozenDays.Count ?? 0;
        var spentIds = await db.InventoryItems.AsNoTracking()
            .Where(i => i.UserId == userId && i.Kind == ItemKind.StreakFreeze && i.ActivatedAt != null)
            .OrderBy(i => i.ActivatedAt)
            .ThenBy(i => i.Id)
            .Take(spent)
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);
        return [.. items.Where(i => !spentIds.Contains(i.Id))];
    }

    /// <summary>
    /// «Припасы» за забег — в транзакции визитов, под блокировкой игрока (<c>VisitProcessor</c>): засчитанный путь
    /// <paramref name="acceptedMeters"/> посчитан ровно один раз, поэтому повтор фишек не даст. Фишки добавляются в
    /// контекст (сохраняет вызывающий). Повтор записанного забега (демо) «Припасов» не даёт. Возвращает, сколько выдано.
    /// </summary>
    public static async Task<int> GrantAsync(
        AppDbContext db,
        RunEntity run,
        double acceptedMeters,
        DateTimeOffset startedAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (run.Source != RunSource.Live || acceptedMeters < SupplyRules.MetersPerItem)
        {
            return 0;
        }

        var day = GameClock.GameDayOf(startedAt); // сутки — по началу забега, как у очков за дистанцию
        var grantedToday = await db.InventoryItems.CountAsync(i => i.UserId == run.UserId && i.GameDay == day, cancellationToken);
        var occupied = (await VisibleAsync(db, run.UserId, now, cancellationToken)).Count;
        var count = SupplyRules.Count(acceptedMeters, grantedToday, occupied);
        var issued = await db.InventoryItems.CountAsync(i => i.UserId == run.UserId, cancellationToken);
        for (var k = 0; k < count; k++)
        {
            var radar = SupplyRules.IsRadar(issued + k);
            db.InventoryItems.Add(new InventoryItemEntity
            {
                Id = Guid.NewGuid(),
                UserId = run.UserId,
                Kind = radar ? ItemKind.Radar : ItemKind.StreakFreeze,
                ReceivedAt = now,
                ExpiresAt = now.AddDays(SupplyRules.LifetimeDays),
                RemainingMeters = radar ? SupplyRules.RadarMeters : null,
                SourceRunId = run.Id,
                GameDay = day,
            });
        }

        return count;
    }

    /// <summary>
    /// Активированные «Радары», которые действуют в забеге, начатом в <paramref name="runStartedAt"/> (по часам сервера):
    /// активированы до его начала и ещё не израсходованы — по порядку активации.
    /// </summary>
    public static IQueryable<InventoryItemEntity> ActiveRadars(AppDbContext db, Guid userId, DateTimeOffset runStartedAt) =>
        db.InventoryItems
            .Where(i => i.UserId == userId && i.Kind == ItemKind.Radar && i.ActivatedAt != null && i.ActivatedAt <= runStartedAt
                && i.RemainingMeters > 0)
            .OrderBy(i => i.ActivatedAt)
            .ThenBy(i => i.Id);

    /// <summary>Списывает <paramref name="usedMeters"/> с «Радаров» по порядку активации (фишки отслеживает контекст).</summary>
    public static void ConsumeRadar(IEnumerable<InventoryItemEntity> radars, double usedMeters)
    {
        foreach (var radar in radars)
        {
            if (usedMeters <= 0)
            {
                break;
            }

            var take = Math.Min(radar.RemainingMeters ?? 0, usedMeters);
            radar.RemainingMeters = Math.Round((radar.RemainingMeters ?? 0) - take, 1);
            usedMeters -= take;
        }
    }
}
