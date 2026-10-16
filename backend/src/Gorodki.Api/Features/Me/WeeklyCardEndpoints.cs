using System.Globalization;
using System.Security.Claims;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Osm;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Time;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Me;

/// <summary>Карточка недели — только числа (PLAN.md, §3.15).</summary>
/// <param name="WeekStart">Понедельник недели по Минску, <c>yyyy-MM-dd</c>.</param>
/// <param name="DistanceMeters">Засчитанный путь живых забегов недели, м, округлён до 0,1.</param>
/// <param name="ExploredSquareMeters">Открыто тумана за неделю («+N га»), м², оба слоя; округлено до 0,1.</param>
/// <param name="BrestPercentGained">
/// Прирост пожизненного «% Бреста» за неделю, п. п., округлён до 0,01; <c>null</c> — набора OSM нет или в эту неделю он
/// сменился (§3.15: тогда показываются только км и га).
/// </param>
/// <param name="CapturedSquareMeters">
/// Взято захватами за неделю, м² («взятое» заявок — его автор видит сразу), округлено до 0,1.
/// </param>
public sealed record WeeklyCardResponse(
    string WeekStart, double DistanceMeters, double ExploredSquareMeters, double? BrestPercentGained, double CapturedSquareMeters);

/// <summary>
/// Карточка недели (PLAN.md, §3.15): каждый понедельник — «Неделя: 12,4 км · +2,1 га · открыто +1,3% Бреста». Картинку
/// (карта своей земли, закраска районов) рисует телефон из своих данных; сервер отдаёт только числа — без следов, дома и
/// точных дат. Задача E12 (#141, docs/guides/egor-server.md, раздел 7) — сделано Claude 15.10 по решению Никиты.
/// </summary>
/// <remarks>
/// Неделя — понедельник–воскресенье по Минску; в неё входят свои живые забеги, начатые в эти дни: их засчитанный путь, новые
/// клетки тумана (оба слоя) и «взятое» их заявок. Зритель — сам игрок: «взятое» он видит сразу, а очки и разбивка тут не
/// показываются вовсе. Числа забега записываются, когда он обработан (туман, визиты), — карточка недели, запрошенная сразу
/// после воскресного забега, дорастёт сама.
/// </remarks>
public static class WeeklyCardEndpoints
{
    public static IEndpointRouteBuilder MapWeeklyCardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/me/weekly", GetWeeklyCard)
            .WithName("getWeeklyCard")
            .WithTags("Профиль")
            .WithSummary("Карточка недели: км, +га тумана, прирост «% Бреста», взятая площадь; week=yyyy-MM-dd (понедельник)")
            .WithDescription(
                "Без week — последняя завершённая неделя (понедельник–воскресенье по Минску). week — не понедельник или в будущем — "
                + "400 weekly_invalid. Только свои числа.")
            .RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        return app;
    }

    private static async Task<Results<Ok<WeeklyCardResponse>, ProblemHttpResult>> GetWeeklyCard(
        string? week,
        ClaimsPrincipal principal,
        AppDbContext db,
        GameConfigStore configs,
        ReachableStore reachable,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        var now = time.GetUtcNow();
        var today = GameClock.GameDayOf(now);
        var thisMonday = MondayOf(today);
        DateOnly monday;
        if (week is null)
        {
            monday = thisMonday.AddDays(-7); // последняя завершённая неделя
        }
        else if (!DateOnly.TryParseExact(week, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out monday)
            || monday.DayOfWeek != DayOfWeek.Monday || monday > thisMonday)
        {
            return TypedResults.Problem(
                title: "week — понедельник уже начавшейся недели, yyyy-MM-dd.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = "weekly_invalid" });
        }

        var start = SeasonCalendar.MinskMidnight(monday);
        var end = SeasonCalendar.MinskMidnight(monday.AddDays(7));

        // Забеги недели — свои живые (повтор демо не в счёт), по игровым суткам начала на часах сервера: время начала —
        // по часам телефона, поправка на их сдвиг (до 12 ч) — как у сезона тумана и суток очков.
        var runs = (await db.Runs.AsNoTracking()
                .Where(r => r.UserId == userId
                    && r.Source == RunSource.Live
                    && r.StartedAt >= start - RunLimits.MaxClockSkew
                    && r.StartedAt < end + RunLimits.MaxClockSkew)
                .Select(r => new
                {
                    r.Id,
                    r.StartedAt,
                    r.ClockSkewMs,
                    r.AcceptedMeters,
                    r.FogNewCells,
                    r.FogNewSquareMeters,
                    r.FogNewReachableCells,
                    r.FogOsmSet,
                })
                .ToListAsync(cancellationToken))
            .Where(r => MondayOf(GameClock.GameDayOf(r.StartedAt.AddMilliseconds(-r.ClockSkewMs))) == monday)
            .ToList();

        // «Взятое» своих применённых заявок этих забегов: автор видит его сразу (граница публичности — для других). То, что
        // вернул откат нарушителя, не в счёт.
        var ids = runs.Select(r => r.Id).ToList();
        var captured = ids.Count == 0
            ? 0
            : await db.Captures.AsNoTracking()
                .Where(c => ids.Contains(c.RunId) && c.UserId == userId && c.Status == CaptureStatus.Applied)
                .SumAsync(c => c.AreaSquareMeters - (c.RolledBackArea ?? 0), cancellationToken);

        return TypedResults.Ok(new WeeklyCardResponse(
            monday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Math.Round(runs.Sum(r => r.AcceptedMeters ?? 0), 1),
            Math.Round(runs.Sum(r => r.FogNewSquareMeters ?? 0), 1),
            await BrestPercentGainedAsync(),
            Math.Round(Math.Max(0, captured), 1)));

        // Прирост пожизненного «% Бреста» «Всего» (§3.15; osm-pipeline.md, вопрос 6.10): сумма «новых достижимых» клеток забегов
        // недели (FogProcessor) к «достижимому» города. Только если весь набор недели один — в начале и в конце (или сейчас) —
        // и каждый забег, открывший туман, посчитан по нему: в неделю смены набора прирост не показывается (две мерки).
        async Task<double?> BrestPercentGainedAsync()
        {
            var last = end <= now ? end - TimeSpan.FromTicks(1) : now;
            var set = (await configs.GetAtAsync(start, cancellationToken)).Rules.Osm.SetVersion;
            if (set is not { } version
                || (await configs.GetAtAsync(last, cancellationToken)).Rules.Osm.SetVersion != version
                || runs.Any(r => r.FogNewCells > 0 && r.FogOsmSet != version)
                || await reachable.LoadAsync(version, cancellationToken) is not { City.TotalCells: > 0 } reach)
            {
                return null;
            }

            return Math.Round(100.0 * runs.Sum(r => r.FogNewReachableCells ?? 0) / reach.City.TotalCells, 2);
        }
    }

    /// <summary>Понедельник недели игровых суток <paramref name="day"/> (по Минску).</summary>
    private static DateOnly MondayOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
}
