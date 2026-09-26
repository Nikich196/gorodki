using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Seasons;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Time;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Leaderboards;

/// <summary>Место в рейтинге.</summary>
/// <param name="Name">Ник — только если игрок согласился показывать профиль (закон 99-З), иначе «Игрок #1234».</param>
/// <param name="Hectares">Открытая площадь, га (2 знака).</param>
/// <param name="Me">Это сам спрашивающий.</param>
public sealed record LeaderboardEntry(int Rank, string Name, double Hectares, bool Me);

/// <summary>Рейтинг «Кто открыл больше» по срезу за сутки.</summary>
/// <param name="Day">Игровые сутки среза (по Минску), <c>yyyy-MM-dd</c>; <c>null</c> — срезов ещё не было.</param>
/// <param name="Layer"><c>foot</c>, <c>bike</c> или <c>total</c> (сумма).</param>
/// <param name="Season">Номер сезона; <c>null</c> — за всё время.</param>
/// <param name="Entries">Первые места (до 50).</param>
/// <param name="Mine">Своё место, даже если оно ниже первых; <c>null</c> — в срезе игрока нет (ещё ничего не открыл).</param>
public sealed record ExplorationLeaderboardResponse(
    string? Day, string Layer, int? Season, IReadOnlyList<LeaderboardEntry> Entries, LeaderboardEntry? Mine);

/// <summary>Место в рейтинге территории.</summary>
/// <param name="Name">Ник — только если игрок согласился показывать профиль, иначе «Игрок #1234».</param>
/// <param name="Points">Очки сезона (SP): захваты, удержание, дистанция — только уже видимые всем начисления.</param>
/// <param name="Me">Это сам спрашивающий.</param>
public sealed record TerritoryLeaderboardEntry(int Rank, string Name, int Points, bool Me);

/// <summary>Рейтинг территории — очки сезона по лиге, срез за сутки.</summary>
/// <param name="Day">Игровые сутки среза (по Минску), <c>yyyy-MM-dd</c>; <c>null</c> — срезов ещё не было.</param>
/// <param name="Season">Номер сезона: спрошенный или текущий; <c>null</c> — сезона нет (межсезонье) и номер не спрошен.</param>
/// <param name="Final">
/// Итог закрытого сезона (после 04:00 первого дня следующего). <c>false</c> — предварительно: сезон идёт, или очки его
/// последних минут ещё не видны.
/// </param>
/// <param name="Entries">Первые места (до 50).</param>
/// <param name="Mine">Своё место, даже если оно ниже первых; <c>null</c> — в срезе игрока нет.</param>
public sealed record TerritoryLeaderboardResponse(
    string? Day, League League, int? Season, bool Final, IReadOnlyList<TerritoryLeaderboardEntry> Entries, TerritoryLeaderboardEntry? Mine);

/// <summary>
/// Рейтинги (PLAN.md, §3.10: «кто открыл больше» — сезон и всё время, «Пешком / Вело / Всего», только числа; §3.5: по
/// ежедневному снимку). Чужие карты исследования не показываются никогда — только площадь.
/// </summary>
public static class LeaderboardEndpoints
{
    public const int Top = 50;

    public static IEndpointRouteBuilder MapLeaderboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/leaderboards/exploration", GetExploration)
            .WithName("getExplorationLeaderboard")
            .WithTags("Исследование")
            .WithSummary("Кто открыл больше: layer=foot|bike|total, season=номер (без него — за всё время); срез раз в сутки")
            .RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        app.MapGet("/leaderboards/territory", GetTerritory)
            .WithName("getTerritoryLeaderboard")
            .WithTags("Рейтинги")
            .WithSummary("Рейтинг территории: очки сезона, league=run|bike, season=номер (без него — текущий); срез раз в сутки")
            .WithDescription(
                "Очки сезона (§3.5): захваты, удержание (срез в 00:00 по Минску) и дистанция — раздельно по лигам. Только уже "
                + "видимые всем начисления: очки за захват появляются с той же границы публичности, что сам захват на карте. "
                + "Ник — с согласия, иначе «Игрок #1234». Рейтинг прошлого сезона до 04:00 первого дня нового — предварительный "
                + "(final = false), потом — итог. Неверная лига или сезон — 400 leaderboard_invalid.")
            .RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        return app;
    }

    private static Task<Results<Ok<TerritoryLeaderboardResponse>, ProblemHttpResult, UnauthorizedHttpResult>> GetTerritory(
        string? league,
        int? season,
        ClaimsPrincipal principal,
        AppDbContext db,
        SeasonStore seasons,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        // ЗАДАЧА #TBD-E7 (Егор): копия GetExploration по доске LeaderboardBoard.Territory (новое значение перечисления):
        // лига — run или bike (без неё — run), сезон — номер от 0 (без него — текущий: seasons.CalendarAsync(…).At(now)),
        // иначе 400 leaderboard_invalid. Value среза — очки сезона (ScoreBook.SeasonTotalsAsync после записи удержания).
        // Final — сезон закрыт: now ≥ ScoreBook.ClosesAt(календарь, сезон) и итоговый проход (ScoreBook.FinalTotalsAsync) уже
        // сделан. Ник — как Entry в GetExploration. Сам срез — задача Hangfire в 00:00 (образец — LeaderboardSnapshots,
        // egor-server.md, карточка E7). Тесты — TerritoryLeaderboardTests.
        _ = (league, season, principal, db, seasons, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #TBD-E7");
    }

    private static async Task<Results<Ok<ExplorationLeaderboardResponse>, ProblemHttpResult, UnauthorizedHttpResult>> GetExploration(
        string? layer, int? season, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        if (!Enum.TryParse<LeaderboardLayer>(layer ?? "total", ignoreCase: true, out var kind) || !Enum.IsDefined(kind)
            || (layer is not null && int.TryParse(layer, out _)) || season < 0)
        {
            return TypedResults.Problem(
                title: "Слой — foot, bike или total; сезон — номер от 0.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = "leaderboard_invalid" });
        }

        var seasonNumber = season ?? SeasonCalendar.AllTime;
        var day = await db.LeaderboardSnapshots.AsNoTracking()
            .Where(s => s.Board == LeaderboardBoard.Exploration)
            .MaxAsync(s => (DateOnly?)s.Day, cancellationToken);
        var layerName = kind.ToString().ToLowerInvariant();
        if (day is not { } snapshotDay)
        {
            return TypedResults.Ok(new ExplorationLeaderboardResponse(null, layerName, season, [], null));
        }

        var board = db.LeaderboardSnapshots.AsNoTracking()
            .Where(s => s.Day == snapshotDay && s.Board == LeaderboardBoard.Exploration && s.Layer == kind && s.Season == seasonNumber)
            .Join(db.Users, s => s.UserId, u => u.Id, (s, u) => new { s.UserId, s.Rank, s.Value, u.DisplayName, u.PublicProfile });
        var top = await board.OrderBy(r => r.Rank).ThenBy(r => r.UserId).Take(Top).ToListAsync(cancellationToken);
        var mine = await board.Where(r => r.UserId == userId).SingleOrDefaultAsync(cancellationToken);

        LeaderboardEntry Entry(Guid id, int rank, double value, string displayName, bool publicProfile) =>
            new(rank, id == userId || publicProfile ? displayName : Pseudonym(id), Math.Round(value / 10_000, 2), id == userId);

        return TypedResults.Ok(new ExplorationLeaderboardResponse(
            snapshotDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            layerName,
            season,
            [.. top.Select(r => Entry(r.UserId, r.Rank, r.Value, r.DisplayName, r.PublicProfile))],
            mine is null ? null : Entry(mine.UserId, mine.Rank, mine.Value, mine.DisplayName, mine.PublicProfile)));
    }

    /// <summary>
    /// «Игрок #1234» — для тех, кто не согласился показывать ник (закон 99-З, §3.16). Номер постоянный (от id игрока):
    /// по нему не узнать ник, а с кусками на карте (там тоже только id и цвет, без ника) он связывается лишь анонимно.
    /// </summary>
    public static string Pseudonym(Guid userId)
    {
        var hash = SHA256.HashData(userId.ToByteArray());
        return $"Игрок #{1_000 + (BitConverter.ToUInt32(hash, 0) % 9_000)}";
    }
}
