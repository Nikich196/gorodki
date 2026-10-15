using System.Security.Claims;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Leaderboards;
using Gorodki.Api.Features.Seasons;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Segments;
using Gorodki.Domain.Time;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Gorodki.Api.Features.Segments;

/// <summary>Место в таблице отрезка: лучшее время игрока.</summary>
/// <param name="Name">Ник — если игрок согласился его показывать или это ты сам; иначе «Игрок #1234».</param>
/// <param name="TimeMs">Время прохода отрезка, мс (длительность, а не момент).</param>
/// <param name="Me">Это сам спрашивающий.</param>
public sealed record SegmentEntry(int Rank, string Name, long TimeMs, bool Me);

/// <summary>«Местная легенда» — больше всех проходов за 30 дней (§3.13).</summary>
public sealed record SegmentLegend(string Name, int Efforts, bool Me);

/// <summary>Отрезок в списке.</summary>
/// <param name="SeasonCrown">Корона сезона в лиге запроса; <c>null</c> — в этом сезоне отрезок никто не прошёл.</param>
public sealed record SegmentSummary(Guid Id, string Name, double LengthMeters, SegmentEntry? SeasonCrown);

/// <summary>Отрезки в лиге.</summary>
public sealed record SegmentListResponse(League League, IReadOnlyList<SegmentSummary> Segments);

/// <summary>Отрезок: линия на карте, короны, легенда, свой лучший результат.</summary>
/// <param name="Line">Линия отрезка от старта к финишу: широта, долгота, широта, долгота… — общая, не чей-то след.</param>
/// <param name="SeasonCrown">Корона сезона; <c>null</c> — в сезоне проходов нет.</param>
/// <param name="AllTimeCrown">Корона за всё время; <c>null</c> — проходов нет.</param>
/// <param name="Legend">«Местная легенда» за 30 дней (лиги вместе); <c>null</c> — проходов нет.</param>
/// <param name="MyBestTimeMs">Своё лучшее время в лиге за всё время, мс; <c>null</c> — не проходил.</param>
public sealed record SegmentResponse(
    Guid Id,
    string Name,
    double LengthMeters,
    IReadOnlyList<double> Line,
    League League,
    SegmentEntry? SeasonCrown,
    SegmentEntry? AllTimeCrown,
    SegmentLegend? Legend,
    long? MyBestTimeMs);

/// <summary>Таблица отрезка: лучшие времена игроков.</summary>
/// <param name="Season">Номер сезона; <c>null</c> — за всё время.</param>
/// <param name="Entries">Первые места (до 50), по одному лучшему времени на игрока.</param>
/// <param name="Mine">Своё место; <c>null</c> — не проходил.</param>
public sealed record SegmentLeaderboardResponse(
    Guid SegmentId, League League, int? Season, IReadOnlyList<SegmentEntry> Entries, SegmentEntry? Mine);

/// <summary>
/// «Короли участков» (PLAN.md, §3.13): 6–10 отобранных отрезков по 200–800 м; корона — лучшее время, раздельно «Бег» и
/// «Вело», за сезон и за всё время; «Местная легенда» — больше всех проходов за 30 дней. Проход засчитывает сервер
/// (<c>SegmentEffortDetector</c>: ворота 25 м по порядку, коридор 30 м, время интерполяцией, «бег» быстрее 30 км/ч — нет).
/// <b>Граница публичности:</b> проход виден другим (таблица, корона, «тебя свергли») только когда конец его забега публичен —
/// как визиты (<c>segment_efforts.visible_at</c>, <see cref="SegmentEffortProcessor"/>); свои проходы — сразу. Момента
/// прохода в ответах нет, только длительность. Ник — по правилу карточки игрока. Отрезки — начальные данные
/// (<see cref="SegmentSeed"/>), docs/architecture/segments.md. Задача E16 (#146) и C16 — сделаны Claude 15.10.
/// </summary>
public static class SegmentEndpoints
{
    public const int Top = 50;

    public static IEndpointRouteBuilder MapSegmentEndpoints(this IEndpointRouteBuilder app)
    {
        var segments = app.MapGroup("/segments").WithTags("Короли участков").RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy);
        segments.MapGet("", ListSegments)
            .WithName("listSegments")
            .WithSummary("Отрезки «Королей участков» с короной сезона; league=run|bike (без него — run)")
            .WithDescription("Неверная лига — 400 segments_invalid.")
            .ProducesProblem(StatusCodes.Status400BadRequest);
        segments.MapGet("/{id:guid}", GetSegment)
            .WithName("getSegment")
            .WithSummary("Отрезок: линия, короны сезона и всего времени, «Местная легенда», своё лучшее время; league=run|bike")
            .WithDescription("Нет такого отрезка — 404 segment_not_found; неверная лига — 400 segments_invalid.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        segments.MapGet("/{id:guid}/leaderboard", GetSegmentLeaderboard)
            .WithName("getSegmentLeaderboard")
            .WithSummary("Таблица отрезка: лучшие времена; league=run|bike, season=номер (без него — за всё время)")
            .WithDescription("Нет такого отрезка — 404 segment_not_found; неверная лига или сезон — 400 segments_invalid.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static async Task<Results<Ok<SegmentListResponse>, ProblemHttpResult>> ListSegments(
        string? league,
        ClaimsPrincipal principal,
        AppDbContext db,
        SeasonStore seasons,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } me)
        {
            return Problem(StatusCodes.Status401Unauthorized, "unauthorized", "Нужен вход.");
        }

        if (LeagueOf(league) is not { } kind)
        {
            return Invalid();
        }

        var now = time.GetUtcNow();
        var season = (await seasons.CalendarAsync(cancellationToken)).At(now)?.Number;
        var segments = (await InLeague(db, kind).Select(s => new { s.Id, s.Name, s.LengthMeters }).ToListAsync(cancellationToken))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        // Корона сезона — лучший видимый проход идущего сезона; между сезонами (до Сезона 0) её нет.
        var crowns = new Dictionary<Guid, EffortRow>();
        if (season is { } number)
        {
            var rows = await Visible(db, me, now)
                .Where(e => e.League == kind && e.Season == number)
                .Select(e => new EffortRow(e.SegmentId, e.UserId, e.ElapsedMs, e.Id))
                .ToListAsync(cancellationToken);
            crowns = rows.GroupBy(r => r.SegmentId).ToDictionary(g => g.Key, g => Best(g)[0]);
        }

        var names = await NamesAsync(db, me, crowns.Values.Select(c => c.UserId), cancellationToken);
        return TypedResults.Ok(new SegmentListResponse(
            kind,
            [.. segments.Select(s => new SegmentSummary(
                s.Id, s.Name, s.LengthMeters, crowns.TryGetValue(s.Id, out var crown) ? Entry(1, crown, names, me) : null))]));
    }

    private static async Task<Results<Ok<SegmentResponse>, ProblemHttpResult>> GetSegment(
        Guid id,
        string? league,
        ClaimsPrincipal principal,
        AppDbContext db,
        SeasonStore seasons,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } me)
        {
            return Problem(StatusCodes.Status401Unauthorized, "unauthorized", "Нужен вход.");
        }

        if (LeagueOf(league) is not { } kind)
        {
            return Invalid();
        }

        if (await InLeague(db, kind).Where(s => s.Id == id).SingleOrDefaultAsync(cancellationToken) is not { } segment)
        {
            return NotFound();
        }

        var now = time.GetUtcNow();
        var season = (await seasons.CalendarAsync(cancellationToken)).At(now)?.Number;
        var rows = await Visible(db, me, now)
            .Where(e => e.SegmentId == id)
            .Select(e => new { Row = new EffortRow(e.SegmentId, e.UserId, e.ElapsedMs, e.Id), e.League, e.Season, e.StartedAt })
            .ToListAsync(cancellationToken);
        var inLeague = rows.Where(r => r.League == kind).Select(r => r.Row).ToList();
        var seasonal = season is { } number ? rows.Where(r => r.League == kind && r.Season == number).Select(r => r.Row).ToList() : [];

        // «Местная легенда» — больше всех проходов за 30 игровых суток, лиги вместе (§3.13). Окно сдвигается только в полночь по
        // Минску: по уходу прохода из окна не узнать его время точнее даты. При равенстве — кто набрал это число раньше.
        var since = SeasonCalendar.MinskMidnight(GameClock.GameDayOf(now).AddDays(-(SegmentRules.Default.LegendDays - 1)));
        var legend = rows.Where(r => r.StartedAt >= since)
            .GroupBy(r => r.Row.UserId)
            .Select(g => (UserId: g.Key, Efforts: g.Count(), Last: g.Max(r => r.Row.Id)))
            .OrderByDescending(l => l.Efforts)
            .ThenBy(l => l.Last)
            .FirstOrDefault();

        var allTimeCrown = inLeague.Count > 0 ? Best(inLeague)[0] : null;
        var seasonCrown = seasonal.Count > 0 ? Best(seasonal)[0] : null;
        var names = await NamesAsync(
            db, me, new[] { allTimeCrown?.UserId, seasonCrown?.UserId, legend.Efforts > 0 ? legend.UserId : null }.OfType<Guid>(), cancellationToken);
        var myBest = inLeague.Where(r => r.UserId == me).Select(r => (long?)r.ElapsedMs).Min();
        return TypedResults.Ok(new SegmentResponse(
            segment.Id,
            segment.Name,
            segment.LengthMeters,
            LatLon(segment.Line),
            kind,
            seasonCrown is null ? null : Entry(1, seasonCrown, names, me),
            allTimeCrown is null ? null : Entry(1, allTimeCrown, names, me),
            legend.Efforts > 0 ? new SegmentLegend(names[legend.UserId], legend.Efforts, legend.UserId == me) : null,
            myBest));
    }

    private static async Task<Results<Ok<SegmentLeaderboardResponse>, ProblemHttpResult>> GetSegmentLeaderboard(
        Guid id, string? league, int? season, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } me)
        {
            return Problem(StatusCodes.Status401Unauthorized, "unauthorized", "Нужен вход.");
        }

        if (LeagueOf(league) is not { } kind || season < 0)
        {
            return Invalid();
        }

        if (!await InLeague(db, kind).AnyAsync(s => s.Id == id, cancellationToken))
        {
            return NotFound();
        }

        var rows = await Visible(db, me, time.GetUtcNow())
            .Where(e => e.SegmentId == id && e.League == kind && (season == null || e.Season == season))
            .Select(e => new EffortRow(e.SegmentId, e.UserId, e.ElapsedMs, e.Id))
            .ToListAsync(cancellationToken);
        var best = Best(rows);
        var top = best.Take(Top).ToList();
        var mine = best.FirstOrDefault(r => r.UserId == me);
        var names = await NamesAsync(db, me, top.Select(r => r.UserId).Append(me), cancellationToken);

        // Одинаковое время — одинаковое место: 1, 2, 2, 4 (как в рейтингах).
        int Rank(EffortRow row) => 1 + best.Count(r => r.ElapsedMs < row.ElapsedMs);
        return TypedResults.Ok(new SegmentLeaderboardResponse(
            id, kind, season, [.. top.Select(r => Entry(Rank(r), r, names, me))], mine is null ? null : Entry(Rank(mine), mine, names, me)));
    }

    /// <summary>Проход для таблиц: отрезок, игрок, время и номер (при равном времени выше тот, кто прошёл раньше).</summary>
    private sealed record EffortRow(Guid SegmentId, Guid UserId, long ElapsedMs, long Id);

    /// <summary>
    /// Проходы, которые видит игрок <paramref name="me"/>: чужие — с границы публичности конца забега (<c>visible_at</c>),
    /// свои — сразу. Игроки, удаляющие аккаунт, в таблицах уже не показываются (как в ленте).
    /// </summary>
    private static IQueryable<SegmentEffortEntity> Visible(AppDbContext db, Guid me, DateTimeOffset now) =>
        db.SegmentEfforts.AsNoTracking()
            .Where(e => (e.VisibleAt <= now || e.UserId == me) && db.Users.Any(u => u.Id == e.UserId && u.DeletionRequestedAt == null));

    /// <summary>Отрезки лиги: общие и свои для неё.</summary>
    private static IQueryable<SegmentEntity> InLeague(AppDbContext db, League league) =>
        db.Segments.AsNoTracking().Where(s => s.League == null || s.League == league);

    /// <summary>Лучший проход каждого игрока — по возрастанию времени.</summary>
    private static List<EffortRow> Best(IEnumerable<EffortRow> rows) =>
        [.. rows.GroupBy(r => r.UserId)
            .Select(g => g.OrderBy(r => r.ElapsedMs).ThenBy(r => r.Id).First())
            .OrderBy(r => r.ElapsedMs)
            .ThenBy(r => r.Id)];

    /// <summary>Ники по правилу карточки игрока (#115): свой — всегда, чужой — с согласия, иначе «Игрок #1234».</summary>
    private static async Task<Dictionary<Guid, string>> NamesAsync(
        AppDbContext db, Guid me, IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToList();
        var users = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.PublicProfile })
            .ToListAsync(cancellationToken);
        return users.ToDictionary(u => u.Id, u => u.Id == me || u.PublicProfile ? u.DisplayName : LeaderboardEndpoints.Pseudonym(u.Id));
    }

    private static SegmentEntry Entry(int rank, EffortRow row, Dictionary<Guid, string> names, Guid me) =>
        new(rank, names[row.UserId], row.ElapsedMs, row.UserId == me);

    /// <summary>Линия для карты: «широта, долгота, …», 7 знаков (~1 см) — как контуры участков.</summary>
    private static List<double> LatLon(LineString line)
    {
        var result = new List<double>(line.NumPoints * 2);
        foreach (var point in line.Coordinates)
        {
            var (latitude, longitude) = Utm34.Inverse(point.X, point.Y);
            result.Add(Math.Round(latitude, 7));
            result.Add(Math.Round(longitude, 7));
        }

        return result;
    }

    /// <summary>Лига из строки запроса: <c>run</c> (по умолчанию) или <c>bike</c>; иначе <c>null</c>.</summary>
    private static League? LeagueOf(string? league) =>
        Enum.TryParse<League>(league ?? "run", ignoreCase: true, out var kind) && Enum.IsDefined(kind) && !int.TryParse(league, out _)
            ? kind
            : null;

    private static ProblemHttpResult Invalid() =>
        Problem(StatusCodes.Status400BadRequest, "segments_invalid", "Лига — run или bike; сезон — номер от 0.");

    private static ProblemHttpResult NotFound() =>
        Problem(StatusCodes.Status404NotFound, "segment_not_found", "Такого отрезка нет.");

    private static ProblemHttpResult Problem(int status, string code, string title) =>
        TypedResults.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
