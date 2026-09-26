using System.Security.Claims;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Leagues;
using Microsoft.AspNetCore.Http.HttpResults;

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
/// как визиты (<c>runs.visits_processed_at</c>); момента прохода в ответах нет, только длительность. Задача Егора E16
/// (docs/guides/egor-server.md, раздел 7), после C16 (отрезки).
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

    private static Task<Results<Ok<SegmentListResponse>, ProblemHttpResult>> ListSegments(
        string? league, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #146 (Егор): отрезки (таблицы даёт C16) и корона сезона в лиге — лучший видимый проход сезона (видимый —
        // конец его забега публичен). Имя — правило карточки игрока (#115). Тесты — SegmentsTests.
        _ = (league, principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #146");
    }

    private static Task<Results<Ok<SegmentResponse>, ProblemHttpResult>> GetSegment(
        Guid id, string? league, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #146 (Егор): отрезок, короны сезона и всего времени, легенда за 30 дней (по видимым проходам) и своё лучшее
        // время. Свои проходы можно показывать сразу — как «взятое» у заявки. Тесты — SegmentsTests; строка в
        // IdorTests.AwaitingTasks.
        _ = (id, league, principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #146");
    }

    private static Task<Results<Ok<SegmentLeaderboardResponse>, ProblemHttpResult>> GetSegmentLeaderboard(
        Guid id, string? league, int? season, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #146 (Егор): лучшее видимое время каждого игрока в лиге (и сезоне), по возрастанию, до 50, плюс своё место
        // (как Mine в GetExploration). Тесты — SegmentsTests; строка в IdorTests.AwaitingTasks.
        _ = (id, league, season, principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #146");
    }
}
