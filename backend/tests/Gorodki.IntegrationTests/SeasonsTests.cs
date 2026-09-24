using System.Net.Http.Json;
using Gorodki.Api.Features.Seasons;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Time;
using Microsoft.EntityFrameworkCore;
using static Gorodki.IntegrationTests.RunRequests;

namespace Gorodki.IntegrationTests;

/// <summary>Календарь сезонов в базе (PLAN.md, §3.4) и адрес <c>/seasons</c>.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class SeasonsTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Seeded_seasons_are_the_plan_dates_at_minsk_midnight()
    {
        database.RequireDatabase();
        await using var db = database.CreateContext();

        var rows = await db.Seasons.AsNoTracking().OrderBy(s => s.Number).ToListAsync(Cancel);
        var calendar = new SeasonCalendar(rows.Select(s => new Season(s.Number, s.Name, s.StartsAt))); // проверяет полночь по Минску

        Assert.Equal(
            [new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 30), new DateOnly(2026, 12, 14)],
            calendar.Seasons.Select(s => GameClock.GameDayOf(s.StartsAt)));
        Assert.All(calendar.Seasons, s => Assert.Equal(SeasonCalendar.MinskMidnight(GameClock.GameDayOf(s.StartsAt)), s.StartsAt));
    }

    [Fact]
    public async Task Season_not_starting_at_minsk_midnight_is_refused_by_the_database_itself()
    {
        // Сезоны вставляются вручную. Полночь UTC — это 03:00 в Минске: календарь такой сезон не принимает, и падали бы
        // туман всех забегов и GET /seasons. База не даёт записать такую строку.
        database.RequireDatabase();
        await using var db = database.CreateContext();
        db.Seasons.Add(new SeasonEntity { Number = 3, Name = "Сезон 3", StartsAt = new DateTimeOffset(2026, 12, 28, 0, 0, 0, TimeSpan.Zero) });

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
        Assert.Contains("ck_seasons_minsk_midnight", error.InnerException?.Message ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Seasons_show_which_one_is_on_now()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync(); // до сдвига часов: токен «из будущего» не пройдёт проверку
        api.Time.SetUtcNow(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

        var before = await client.GetFromJsonAsync<SeasonsResponse>("/seasons", Json, Cancel);
        api.Time.SetUtcNow(SeasonCalendar.MinskMidnight(new DateOnly(2026, 11, 30)));
        var first = await client.GetFromJsonAsync<SeasonsResponse>("/seasons", Json, Cancel);

        Assert.Null(before!.Current); // предсезонье — полевые тесты
        Assert.Equal(3, before.Seasons.Count);
        Assert.Equal(before.Seasons[1].StartsAtMs, before.Seasons[0].EndsAtMs);
        Assert.Null(before.Seasons[2].EndsAtMs);
        Assert.Equal(1, first!.Current); // ровно в полночь 30.11 по Минску — уже Сезон 1
    }
}
