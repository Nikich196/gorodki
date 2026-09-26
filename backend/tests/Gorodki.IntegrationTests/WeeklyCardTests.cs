using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Me;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Time;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Карточка недели — <c>GET /me/weekly</c> (PLAN.md, §3.15). Задача #TBD-E12 для Егора: тесты со <c>Skip</c> снимаются вместе
/// с реализацией. Прирост «% Бреста» — после E9; «+га» и взятое — по забегам и заявкам недели.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class WeeklyCardTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(Skip = "ЗАДАЧА #TBD-E12")]
    public async Task Week_without_runs_is_zeros_and_only_numbers()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync(newcomer: true);
        var monday = MondayWeeksAgo(api, 2);

        var response = await anna.GetAsync($"/me/weekly?week={monday}", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Cancel);
        var card = JsonSerializer.Deserialize<WeeklyCardResponse>(body, Json)!;
        Assert.Equal(new WeeklyCardResponse(monday, 0, 0, null, 0), card); // набора OSM нет — прироста % нет
        using var json = JsonDocument.Parse(body);
        Assert.Equal( // ровно эти поля: ни следов, ни дома, ни точных дат (§3.15)
            ["brestPercentGained", "capturedSquareMeters", "distanceMeters", "exploredSquareMeters", "weekStart"],
            json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact(Skip = "ЗАДАЧА #TBD-E12")]
    public async Task Week_is_a_past_or_current_monday()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var monday = DateOnly.ParseExact(MondayWeeksAgo(api, 1), "yyyy-MM-dd", CultureInfo.InvariantCulture);

        foreach (var week in new[] { monday.AddDays(1), monday.AddDays(14) }) // вторник; понедельник в будущем
        {
            var response = await anna.GetAsync($"/me/weekly?week={week:yyyy-MM-dd}", Cancel);
            Assert.Equal((400, "weekly_invalid"), await Problems.OfAsync(response, Cancel));
        }

        Assert.Equal((400, "weekly_invalid"), await Problems.OfAsync(await anna.GetAsync("/me/weekly?week=вчера", Cancel), Cancel));
    }

    [Fact(Skip = "ЗАДАЧА #TBD-E12")]
    public async Task Only_own_live_runs_of_that_week_count()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync(newcomer: true);
        var (_, borisId) = await api.CreatePlayerClientAsync(newcomer: true);
        var monday = MondayWeeksAgo(api, 2);
        var wednesday = SeasonCalendar.MinskMidnight(DateOnly.ParseExact(monday, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(2)).AddHours(18);
        await AddRunAsync(api, annaId, wednesday, RunSource.Live, 5_000.04);
        await AddRunAsync(api, annaId, wednesday.AddDays(1), RunSource.Live, 7_400.3);
        await AddRunAsync(api, annaId, wednesday.AddDays(7), RunSource.Live, 9_000); // следующая неделя
        await AddRunAsync(api, annaId, wednesday, RunSource.Replay, 3_000); // повтор демо
        await AddRunAsync(api, borisId, wednesday, RunSource.Live, 1_000); // чужой

        var card = (await anna.GetFromJsonAsync<WeeklyCardResponse>($"/me/weekly?week={monday}", Json, Cancel))!;

        Assert.Equal(12_400.3, card.DistanceMeters);
    }

    // MARK: — вспомогательное

    /// <summary>Понедельник (по Минску) недели, которая была <paramref name="weeks"/> недель назад.</summary>
    private static string MondayWeeksAgo(ApiFactory api, int weeks)
    {
        var today = GameClock.GameDayOf(api.Time.GetUtcNow());
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7) - (7 * weeks));
        return monday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>Завершённый забег прямо в базе: для километров точки не нужны.</summary>
    private async Task AddRunAsync(ApiFactory api, Guid userId, DateTimeOffset startedAt, RunSource source, double meters)
    {
        await using (var scope = api.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<GameConfigStore>().GetCurrentAsync(Cancel); // версия 1 конфига в новой базе
        }

        await using var db = database.CreateContext();
        db.Runs.Add(new RunEntity
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            League = League.Run,
            Source = source,
            ConfigVersion = 1,
            StartedAt = startedAt,
            EndedAt = startedAt.AddHours(1),
            Status = RunStatus.Finished,
            CreatedAt = startedAt,
            DeviceId = Guid.NewGuid(),
            AppVersion = "0.1.0 (1)",
            MotionAuthorized = true,
            AcceptedMeters = meters,
        });
        await db.SaveChangesAsync(Cancel);
    }
}
