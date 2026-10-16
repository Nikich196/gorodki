using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Fog;
using Gorodki.Api.Features.Me;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Config;
using Gorodki.Domain.Fog;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Osm;
using Gorodki.Domain.Time;
using Gorodki.OsmPipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Карточка недели — <c>GET /me/weekly</c> (PLAN.md, §3.15), задача #141 (сделано Claude 15.10): км, «+га» тумана и взятое —
/// по своим живым забегам недели и их заявкам; прирост пожизненного «% Бреста» — по «новым достижимым» клеткам забегов, а в
/// неделю смены набора OSM его нет.
/// </summary>
/// <remarks>
/// Набор в действующем конфиге меняет захват для всех тестов, поэтому конфиги с наборами здесь действуют с 2300 года (позже
/// конфигов <c>OsmSetTests</c> и <c>FogPercentTests</c>), и часы теста процента переводятся туда.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class WeeklyCardTests(DatabaseFixture database)
{
    private static int _nextSet = 2_900;

    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task Week_counts_the_new_fog_and_the_land_taken_by_own_runs_of_that_week()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync(); // клиенты — до сдвига часов
        var (boris, _) = await api.CreatePlayerClientAsync();
        var monday = MondayOf(GameClock.GameDayOf(api.Time.GetUtcNow())).AddDays(7); // следующая неделя — своя для теста
        api.Time.Advance(SeasonCalendar.MinskMidnight(monday.AddDays(2)).AddHours(12) - api.Time.GetUtcNow()); // среда, 12:00

        // Анна: петля (взятое видно автору сразу) и забег, открывший туман; Борис — петля в ту же неделю.
        var (claimRun, _, _) = await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100));
        await ProcessAsync(api, claimRun);
        var walk = await WalkAndFinishAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100));
        await using (var scope = api.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<FogProcessor>().StampRunAsync(walk.Id, Cancel) > 0);
        }

        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Square(NewArea(), 0, 0, 100))).RunId);

        var week = monday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var card = (await anna.GetFromJsonAsync<WeeklyCardResponse>($"/me/weekly?week={week}", Json, Cancel))!;

        await using var db = database.CreateContext();
        var taken = await db.Captures.Where(c => c.UserId == annaId && c.Status == CaptureStatus.Applied && c.RunId == claimRun)
            .SumAsync(c => c.AreaSquareMeters, Cancel);
        var stamped = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == walk.Id, Cancel);
        Assert.InRange(taken, 9_000, 11_000); // квадрат 100 × 100 м — только свой, петли Бориса здесь нет
        Assert.Equal(Math.Round(taken, 1), card.CapturedSquareMeters);
        Assert.Equal(Math.Round(stamped.FogNewSquareMeters!.Value, 1), card.ExploredSquareMeters);
        Assert.InRange(card.ExploredSquareMeters, stamped.FogNewCells!.Value * 34.0, stamped.FogNewCells.Value * 35.0); // клетка ≈ 5,87 м
        Assert.Equal((week, (double?)null), (card.WeekStart, card.BrestPercentGained)); // набора OSM нет

        // Без week — прошлая неделя: там у Анны ничего.
        var previous = (await anna.GetFromJsonAsync<WeeklyCardResponse>("/me/weekly", Json, Cancel))!;
        Assert.Equal(new WeeklyCardResponse(monday.AddDays(-7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 0, 0, null, 0), previous);
    }

    [Fact]
    public async Task Percent_gain_is_the_new_reachable_cells_of_the_week_and_none_in_a_week_the_osm_set_changed()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync(newcomer: true); // клиент — до сдвига часов

        // Два набора с «достижимым» города в 200 клеток; первый действует с 2300 года, второй — со среды третьей недели.
        var (first, second) = (Interlocked.Increment(ref _nextSet), Interlocked.Increment(ref _nextSet));
        var activeFrom = new DateTimeOffset(2300, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(first);
        var week1 = MondayOf(GameClock.GameDayOf(activeFrom)).AddDays(7);
        DateTimeOffset Wednesday(int week) => SeasonCalendar.MinskMidnight(week1.AddDays((7 * (week - 1)) + 2)).AddHours(18);
        await ImportAsync(first);
        await ImportAsync(second);
        await UseSetAsync(first, activeFrom);
        await UseSetAsync(second, Wednesday(3));

        // Неделя 1: два забега, посчитанных по первому набору (30 + 20 из 200 — 25 %), и забег, туман которого стёрла очистка
        // истории (ноль клеток, набора нет). Неделя 2: забег открыл туман до карточки недели — посчитать прирост нечем.
        await AddRunAsync(api, annaId, Wednesday(1), RunSource.Live, 1_000, run => (run.FogNewCells, run.FogNewSquareMeters, run.FogNewReachableCells, run.FogOsmSet) = (40, 1_378.1, 30, first));
        await AddRunAsync(api, annaId, Wednesday(1).AddDays(1), RunSource.Live, 1_000, run => (run.FogNewCells, run.FogNewSquareMeters, run.FogNewReachableCells, run.FogOsmSet) = (25, 861.3, 20, first));
        await AddRunAsync(api, annaId, Wednesday(1).AddDays(2), RunSource.Live, 1_000, run => (run.FogNewCells, run.FogNewSquareMeters) = (0, 0));
        await AddRunAsync(api, annaId, Wednesday(2), RunSource.Live, 1_000, run => (run.FogNewCells, run.FogNewSquareMeters) = (10, 344.5));
        api.Time.Advance(SeasonCalendar.MinskMidnight(week1.AddDays(7 * 4)).AddDays(1) - api.Time.GetUtcNow()); // вторник пятой недели

        async Task<WeeklyCardResponse> Card(int week) =>
            (await anna.GetFromJsonAsync<WeeklyCardResponse>($"/me/weekly?week={week1.AddDays(7 * (week - 1)):yyyy-MM-dd}", Json, Cancel))!;

        Assert.Equal((2_239.4, (double?)25), ((await Card(1)).ExploredSquareMeters, (await Card(1)).BrestPercentGained));
        Assert.Null((await Card(2)).BrestPercentGained);
        Assert.Null((await Card(3)).BrestPercentGained); // в среду сменился набор: две мерки — прироста нет
        Assert.Equal((double?)0, (await Card(4)).BrestPercentGained); // весь второй набор, забегов нет
        Assert.Equal((double?)0, (await Card(5)).BrestPercentGained); // идущая неделя — по сегодняшний день
    }

    // MARK: — вспомогательное

    /// <summary>Понедельник (по Минску) недели, которая была <paramref name="weeks"/> недель назад.</summary>
    private static string MondayWeeksAgo(ApiFactory api, int weeks) =>
        MondayOf(GameClock.GameDayOf(api.Time.GetUtcNow())).AddDays(-7 * weeks).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly MondayOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>Набор OSM: «достижимое» города — 200 клеток одного тайла.</summary>
    private async Task ImportAsync(int version)
    {
        var bits = new FogTileBits();
        for (var cell = 0; cell < 200; cell++)
        {
            bits.Set(cell);
        }

        var city = new SortedDictionary<FogTileKey, FogTileBits> { [new FogTileKey(9_270, 5_401)] = bits };
        var outline = GeoOps.Factory.CreateMultiPolygon([new TileKey(684, 5775).ToPolygon()]);
        await FogPercentTests.ImportSetAsync(
            database, version, city, [new DistrictData("city", DistrictKind.City, "Брест", null, false, outline, 2_000_000, city)], Cancel);
    }

    /// <summary>Конфиг с набором <paramref name="setVersion"/>, действующий с <paramref name="activeFrom"/>.</summary>
    private async Task UseSetAsync(int setVersion, DateTimeOffset activeFrom)
    {
        await using var db = database.CreateContext();
        db.GameConfigs.Add(new GameConfigEntity
        {
            Version = 3_000_000 + setVersion, // новее конфигов OsmSetTests (2100) и FogPercentTests (2200)
            Json = (GameConfig.Default with { Osm = new OsmConfig { SetVersion = setVersion } }).ToJson(),
            ActiveFrom = activeFrom,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Cancel);
    }

    /// <summary>Завершённый забег прямо в базе: для километров точки не нужны.</summary>
    private async Task AddRunAsync(
        ApiFactory api, Guid userId, DateTimeOffset startedAt, RunSource source, double meters, Action<RunEntity>? tune = null)
    {
        await using (var scope = api.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<GameConfigStore>().GetCurrentAsync(Cancel); // версия 1 конфига в новой базе
        }

        await using var db = database.CreateContext();
        var run = new RunEntity
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
        };
        tune?.Invoke(run);
        db.Runs.Add(run);
        await db.SaveChangesAsync(Cancel);
    }
}
