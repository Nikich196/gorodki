using Gorodki.Api.Features.Captures;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Time;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Seasons;

/// <summary>Сезон для приложения: время — миллисекунды Unix, как везде в API.</summary>
/// <param name="EndsAtMs">Конец — начало следующего сезона; <c>null</c> — последний сезон идёт до показа.</param>
public sealed record SeasonView(int Number, string Name, long StartsAtMs, long? EndsAtMs);

/// <param name="Current">Номер идущего сезона; <c>null</c> — сезоны ещё не начались (предсезонье).</param>
public sealed record SeasonsResponse(IReadOnlyList<SeasonView> Seasons, int? Current);

/// <summary>Календарь сезонов из базы (таблица <c>seasons</c>, PLAN.md §3.4).</summary>
public sealed class SeasonStore(AppDbContext db)
{
    public async Task<SeasonCalendar> CalendarAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Seasons.AsNoTracking().OrderBy(s => s.Number).ToListAsync(cancellationToken);
        return new SeasonCalendar(rows.Select(s => new Season(s.Number, s.Name, s.StartsAt)));
    }
}

public static class SeasonEndpoints
{
    public static IEndpointRouteBuilder MapSeasonEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/seasons", GetSeasons)
            .WithName("getSeasons")
            .WithTags("Сезоны")
            .RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy)
            .WithSummary("Сезоны: номера, названия, начало и конец; какой идёт сейчас");
        return app;
    }

    private static async Task<SeasonsResponse> GetSeasons(SeasonStore store, TimeProvider time, CancellationToken cancellationToken)
    {
        var calendar = await store.CalendarAsync(cancellationToken);
        return ToResponse(calendar, time.GetUtcNow());
    }

    public static SeasonsResponse ToResponse(SeasonCalendar calendar, DateTimeOffset now) => new(
        calendar.Seasons
            .Select(s => new SeasonView(s.Number, s.Name, s.StartsAt.ToUnixTimeMilliseconds(), calendar.EndOf(s)?.ToUnixTimeMilliseconds()))
            .ToList(),
        calendar.At(now)?.Number);
}
