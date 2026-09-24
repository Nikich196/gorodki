using Gorodki.Api.Features.Leaderboards;
using Gorodki.Api.Features.Realtime;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Time;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Fog;

/// <summary>Что стёрла очистка истории исследований.</summary>
/// <param name="Tiles">Тайлов тумана — обоих слоёв, за всё время и по сезонам.</param>
/// <param name="Runs">Забегов, которые ещё не открывали туман и теперь уже не откроют.</param>
/// <param name="Rankings">Своих строк в срезах рейтинга «Кто открыл больше».</param>
public sealed record FogClearing(int Tiles, int Runs, int Rankings);

/// <summary>
/// «Очистить историю исследований» (PLAN.md, §3.10, «Приватность»; §5, экран 26): туман игрока стирается целиком — оба
/// слоя, за всё время и по сезонам. Только свой: чужой туман стереть нельзя, адреса с номером игрока нет.
/// </summary>
/// <remarks>
/// Тайлы удаляются, а не обнуляются: в базе тайл хранит хотя бы одну клетку (<c>ck_fog_tiles_cells</c>). Чтобы телефон
/// не показывал стёртое из кэша, <c>GET /fog</c> на тайл, которого больше нет, отвечает пустым тайлом с версией новее
/// спрошенной. Кэш хранит такой тайл с версией 0, как любой пустой, — и открытый заново примет с любой версией. Пока
/// телефон ещё помнит стёртый тайл со старой версией, открытый заново тайл обгоняет её версией-временем
/// (<see cref="FogProcessor.NextVersion"/>), если часы сервера не пошли назад.
/// </remarks>
public sealed class FogHistory(
    AppDbContext db, LeaderboardSnapshots leaderboards, RealtimeHints hints, TimeProvider time, ILogger<FogHistory> logger)
{
    public async Task<FogClearing> ClearAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var today = GameClock.GameDayOf(now);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '15s'", cancellationToken);
        // Та же блокировка игрока, что у открытия тумана (FogProcessor): забег, который открывает туман прямо сейчас, либо
        // успевает раньше — и его клетки стираются здесь, — либо после очистки находит свою отметку ниже и откатывается.
        var userKey = userId.ToString();
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(2, hashtext({userKey}))", cancellationToken);
        // И блокировки среза рейтингов (LeaderboardSnapshots) за вчера, сегодня и завтра по Минску: срез, который как раз
        // читает туман (в том числе на границе суток), либо заканчивается раньше — и строки игрока стираются ниже, — либо
        // читает туман уже без стёртого.
        for (var day = today.AddDays(-1); day <= today.AddDays(1); day = day.AddDays(1))
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(4, {day.DayNumber})", cancellationToken);
        }

        var tiles = await db.FogTiles.Where(f => f.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        // Забеги, которые туман ещё не открывали (идёт, ждёт последних кусков или суток после конца), помечаются открывшими
        // ничего: иначе путь до очистки вернул бы стёртое. «+N га» у них — ноль.
        var runs = await db.Runs
            .Where(r => r.UserId == userId && r.FogStampedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.FogStampedAt, now).SetProperty(r => r.FogNewCells, 0), cancellationToken);
        var rankings = await leaderboards.RemovePlayerAsync(userId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (tiles > 0)
        {
            // После фиксации, как у открытия тумана: телефон перезапросит свои тайлы и получит их пустыми.
            hints.FogChanged(userId);
        }

        logger.LogInformation(
            "История исследований {UserId} очищена: {Tiles} тайлов, {Runs} забегов без тумана, {Rankings} мест в рейтинге",
            userId, tiles, runs, rankings);
        return new FogClearing(tiles, runs, rankings);
    }
}
