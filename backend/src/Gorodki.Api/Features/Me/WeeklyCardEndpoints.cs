using System.Security.Claims;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;

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
/// точных дат. Задача Егора E12 (docs/guides/egor-server.md, раздел 7), после E9.
/// </summary>
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

    private static Task<Results<Ok<WeeklyCardResponse>, ProblemHttpResult>> GetWeeklyCard(
        string? week, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #TBD-E12 (Егор): неделя по Минску (GameClock.GameDayOf). Км — AcceptedMeters своих живых забегов (Source = Live,
        // не Active), начатых в эту неделю; +га — сумма FogNewCells этих забегов × площадь клетки (как в GetSummary); взятое —
        // сумма AreaSquareMeters своих применённых заявок. Прирост % — разница «% Бреста» (E9) с началом недели по срезу
        // (osm-pipeline.md, «Как считается % Бреста»; какой процент и что в неделю смены набора — вопрос 6.10 там же).
        // Тесты — WeeklyCardTests.
        _ = (week, principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #TBD-E12");
    }
}
