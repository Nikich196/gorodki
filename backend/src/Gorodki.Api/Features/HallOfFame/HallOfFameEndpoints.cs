using System.Security.Claims;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Leaderboards;
using Gorodki.Api.Features.Seasons;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Leagues;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.HallOfFame;

/// <summary>Что отмечено в Зале славы (PLAN.md, §3.4: «топ-3, топ-клан»).</summary>
public enum HallOfFameKind
{
    /// <summary>Игрок: места 1–3 по очкам сезона в лиге.</summary>
    Player,

    /// <summary>Клан: место 1.</summary>
    Clan,
}

/// <summary>Строка Зала славы.</summary>
/// <param name="Place">Место: 1–3 у игроков, 1 у клана.</param>
/// <param name="PlayerId">Игрок; <c>null</c> — у клана или игрок удалил аккаунт (строка обезличена).</param>
/// <param name="ClanId">Клан; <c>null</c> — у игрока или клана больше нет.</param>
/// <param name="Name">
/// Игрок — ник с его согласия (или свой), иначе «Игрок #1234»; клан — название. <c>null</c> — игрок удалил аккаунт.
/// </param>
/// <param name="Value">Очки сезона — итог на момент закрытия сезона.</param>
/// <param name="Me">Это сам спрашивающий (или его клан).</param>
public sealed record HallOfFameEntry(
    HallOfFameKind Kind, League League, int Place, Guid? PlayerId, Guid? ClanId, string? Name, int Value, bool Me);

/// <summary>Зал славы одного сезона.</summary>
/// <param name="Name">Название сезона, как в <c>GET /seasons</c>.</param>
public sealed record HallOfFameSeason(int Season, string Name, IReadOnlyList<HallOfFameEntry> Entries);

/// <summary>Зал славы: закрытые сезоны, новые сверху.</summary>
public sealed record HallOfFameResponse(IReadOnlyList<HallOfFameSeason> Seasons);

/// <summary>
/// Зал славы (PLAN.md, §3.4: «снимок карты, топ-3, топ-клан; при удалении аккаунта игрок обезличивается»). Снимок —
/// один раз на сезон по его итогу (<see cref="HallOfFameSnapshots"/>: <c>ScoreBook.FinalTotalsAsync</c>, начисления, видимые
/// на момент закрытия — 04:00 первого дня следующего сезона). Задача E8 (#137, docs/guides/egor-server.md, раздел 7) —
/// сделано Claude 15.10 по решению Никиты. «Снимок карты» в контракте не предусмотрен — только места.
/// </summary>
public static class HallOfFameEndpoints
{
    public static IEndpointRouteBuilder MapHallOfFameEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/hall-of-fame", GetHallOfFame)
            .WithName("getHallOfFame")
            .WithTags("Рейтинги")
            .WithSummary("Зал славы: закрытые сезоны — топ-3 игроков по лигам и топ-клан")
            .WithDescription(
                "Только закрытые сезоны: снимок делается один раз по итогу сезона (очки, видимые на момент закрытия — 04:00 первого "
                + "дня следующего). Ник — с согласия игрока, иначе «Игрок #1234»; удалил аккаунт — строка остаётся без имени "
                + "(playerId и name — null).")
            .RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy);
        return app;
    }

    private static async Task<Ok<HallOfFameResponse>> GetHallOfFame(
        ClaimsPrincipal principal, AppDbContext db, SeasonStore seasons, CancellationToken cancellationToken)
    {
        var viewer = principal.UserId();
        var calendar = await seasons.CalendarAsync(cancellationToken);

        // Сезоны в Зале — те, чей снимок сделан (HallOfFameSnapshots): только закрытые; новые сверху.
        var snapped = await db.JobRuns.AsNoTracking()
            .Where(j => j.Job == HallOfFameSnapshots.Job)
            .OrderByDescending(j => j.Key)
            .Select(j => j.Key)
            .ToListAsync(cancellationToken);
        var myClan = await db.ClanMembers.AsNoTracking()
            .Where(m => m.UserId == viewer)
            .Select(m => (Guid?)m.ClanId)
            .SingleOrDefaultAsync(cancellationToken);

        // Ник — по правилу карточки игрока (#115), на момент запроса: согласие меняется. Аккаунт удаляется — строка уже
        // без игрока, как после удаления (тогда база сама ставит user_id = null).
        var rows = await db.HallOfFame.AsNoTracking()
            .Where(h => snapped.Contains(h.Season))
            .Select(h => new
            {
                h.Id,
                h.Season,
                h.League,
                h.Kind,
                h.Place,
                h.Value,
                h.ClanId,
                h.ClanName,
                Player = db.Users
                    .Where(u => u.Id == h.UserId && u.DeletionRequestedAt == null)
                    .Select(u => new { u.Id, u.DisplayName, u.PublicProfile })
                    .FirstOrDefault(),
                ClanNow = db.Clans.Where(c => c.Id == h.ClanId).Select(c => c.Name).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        // У клана «это ты» — спрашивающий сейчас в нём.
        HallOfFameEntry Entry(
            HallOfFameKind kind, League league, int place, int value, Guid? playerId, string? playerName, Guid? clanId, string? clanName) =>
            kind == HallOfFameKind.Player
                ? new(kind, league, place, playerId, null, playerName, value, playerId is not null && playerId == viewer)
                : new(kind, league, place, null, clanId, clanName, value, clanId is not null && clanId == myClan);

        return TypedResults.Ok(new HallOfFameResponse([.. snapped.Select(season => new HallOfFameSeason(
            season,
            calendar.Seasons.FirstOrDefault(s => s.Number == season)?.Name ?? $"Сезон {season}",
            [.. rows
                .Where(r => r.Season == season)
                .OrderBy(r => r.League)
                .ThenBy(r => r.Kind)
                .ThenBy(r => r.Place)
                .ThenBy(r => r.Id)
                .Select(r => Entry(
                    r.Kind,
                    r.League,
                    r.Place,
                    r.Value,
                    r.Player?.Id,
                    r.Player is null
                        ? null
                        : r.Player.Id == viewer || r.Player.PublicProfile ? r.Player.DisplayName : LeaderboardEndpoints.Pseudonym(r.Player.Id),
                    r.ClanId,
                    r.ClanNow ?? r.ClanName))]))]));
    }
}
