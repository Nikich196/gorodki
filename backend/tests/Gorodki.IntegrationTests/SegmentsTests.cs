using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Features.Seasons;
using Gorodki.Api.Features.Segments;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// «Короли участков» — <c>/segments</c> и засчитывание проходов на сервере (PLAN.md, §3.13; #146, C16). У каждого теста — свой
/// отрезок в своём месте (300 м на восток), база общая. Правила самого прохода (ворота, коридор, интерполяция, скорость лиги,
/// разрывы) — <c>SegmentEffortDetectorTests</c> без Docker; здесь — забег целиком: судья, запись, повтор, граница
/// публичности, короны, «Местная легенда», таблица и «тебя свергли с трона».
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class SegmentsTests(DatabaseFixture database)
{
    private const double SegmentLength = 300;

    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task List_is_per_league_and_a_wrong_league_is_rejected()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();

        var run = (await anna.GetFromJsonAsync<SegmentListResponse>("/segments", Json, Cancel))!;
        var bike = (await anna.GetFromJsonAsync<SegmentListResponse>("/segments?league=bike", Json, Cancel))!;

        Assert.Equal((League.Run, League.Bike), (run.League, bike.League));
        Assert.Equal((400, "segments_invalid"), await Problems.OfAsync(await anna.GetAsync("/segments?league=walk", Cancel), Cancel));
        Assert.Equal((400, "segments_invalid"), await Problems.OfAsync(await anna.GetAsync("/segments?league=1", Cancel), Cancel));

        // Начальные отрезки (предложение на утверждение): набережная с bicycle=no — только «Бег», велодорожка — только «Вело»,
        // аллеи парков — в обеих лигах.
        Assert.Contains(run.Segments, s => s.Name == "Набережная Мухавца (восток)");
        Assert.DoesNotContain(run.Segments, s => s.Name == "Велодорожка вдоль Мухавца");
        Assert.Contains(bike.Segments, s => s.Name == "Велодорожка вдоль Мухавца");
        Assert.DoesNotContain(bike.Segments, s => s.Name == "Набережная Мухавца (восток)");
        Assert.All(
            SegmentSeed.Entries.Where(e => e.League is null),
            e => Assert.True(run.Segments.Any(s => s.Id == e.Id) && bike.Segments.Any(s => s.Id == e.Id), e.Name));
        Assert.All(run.Segments.Concat(bike.Segments), s => Assert.InRange(s.LengthMeters, 200, 800));
        var cycleway = SegmentSeed.Entries.Single(e => e.League == League.Bike).Id;
        Assert.Equal((404, "segment_not_found"), await Problems.OfAsync(await anna.GetAsync($"/segments/{cycleway}?league=run", Cancel), Cancel));
    }

    [Fact]
    public async Task Unknown_segment_is_not_found()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var id = Guid.NewGuid();

        Assert.Equal((404, "segment_not_found"), await Problems.OfAsync(await anna.GetAsync($"/segments/{id}", Cancel), Cancel));
        Assert.Equal((404, "segment_not_found"), await Problems.OfAsync(await anna.GetAsync($"/segments/{id}/leaderboard", Cancel), Cancel));
        Assert.NotEqual(HttpStatusCode.OK, (await anna.GetAsync($"/segments/{id}/leaderboard?season=-1", Cancel)).StatusCode);
    }

    [Fact]
    public async Task Segment_detail_gives_its_line_on_the_map_and_nothing_yet_without_passes()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (segmentId, area) = await NewSegmentAsync();

        var segment = (await anna.GetFromJsonAsync<SegmentResponse>($"/segments/{segmentId}", Json, Cancel))!;

        Assert.Equal((League.Run, SegmentLength), (segment.League, segment.LengthMeters));
        var (latitude, longitude) = Utm34.Inverse(WalkOrigin.X + area.X, WalkOrigin.Y + area.Y);
        Assert.Equal(4, segment.Line.Count); // старт и финиш: широта, долгота (7 знаков, ~1 см)
        Assert.Equal((Math.Round(latitude, 7), Math.Round(longitude, 7)), (segment.Line[0], segment.Line[1]));
        Assert.Null(segment.AllTimeCrown);
        Assert.Null(segment.Legend);
        Assert.Null(segment.MyBestTimeMs);
    }

    [Fact]
    public async Task Pass_is_counted_once_seen_by_me_at_once_and_by_others_only_after_the_public_boundary()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        var (segmentId, area) = await NewSegmentAsync();

        var runId = await RunAlongAsync(api, anna, area, speed: 1.4);
        Assert.Equal(1, await ProcessAsync(api, runId));
        Assert.Null(await ProcessAsync(api, runId)); // повтор обработки ничего не пишет
        var effort = await SingleEffortAsync(segmentId);
        Assert.Equal((annaId, runId, League.Run), (effort.UserId, effort.RunId, effort.League));
        Assert.InRange(effort.ElapsedMs, (SegmentLength / 1.4 * 1000) - 1_500, (SegmentLength / 1.4 * 1000) + 1_500);
        Assert.True(effort.VisibleAt > api.Time.GetUtcNow()); // конец забега ещё не публичен

        // Себе — сразу: своё лучшее время, корона и место в таблице.
        var mine = (await anna.GetFromJsonAsync<SegmentResponse>($"/segments/{segmentId}", Json, Cancel))!;
        Assert.Equal(effort.ElapsedMs, mine.MyBestTimeMs);
        Assert.Equal(new SegmentEntry(1, await NameAsync(annaId), effort.ElapsedMs, true), mine.AllTimeCrown);
        Assert.Equal(new SegmentLegend(await NameAsync(annaId), 1, true), mine.Legend);

        // Другим — пока ничего: ни короны, ни места в таблице, ни легенды.
        var early = (await boris.GetFromJsonAsync<SegmentResponse>($"/segments/{segmentId}", Json, Cancel))!;
        var earlyBoard = (await boris.GetFromJsonAsync<SegmentLeaderboardResponse>($"/segments/{segmentId}/leaderboard", Json, Cancel))!;
        Assert.Equal((null, null, null), (early.AllTimeCrown, early.Legend, early.MyBestTimeMs));
        Assert.Empty(earlyBoard.Entries);

        // После границы публичности — видно, но только длительность и «Игрок #1234» (согласия на ник у Анны нет).
        api.Time.Advance(TimeSpan.FromMinutes(25));
        var detail = await boris.GetAsync($"/segments/{segmentId}", Cancel);
        var board = await boris.GetAsync($"/segments/{segmentId}/leaderboard", Cancel);
        var seen = (await detail.Content.ReadFromJsonAsync<SegmentResponse>(Json, Cancel))!;
        var pseudonym = Gorodki.Api.Features.Leaderboards.LeaderboardEndpoints.Pseudonym(annaId);
        Assert.Equal(new SegmentEntry(1, pseudonym, effort.ElapsedMs, false), seen.AllTimeCrown);
        Assert.Equal(new SegmentLegend(pseudonym, 1, false), seen.Legend);
        Assert.Null(seen.MyBestTimeMs);
        var boardBody = await board.Content.ReadAsStringAsync(Cancel);
        Assert.Equal([new SegmentEntry(1, pseudonym, effort.ElapsedMs, false)], (await board.Content.ReadFromJsonAsync<SegmentLeaderboardResponse>(Json, Cancel))!.Entries);
        foreach (var body in new[] { boardBody, await detail.Content.ReadAsStringAsync(Cancel) })
        {
            Assert.DoesNotContain(annaId.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(await NameAsync(annaId), body, StringComparison.Ordinal);
            Assert.DoesNotContain(runId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        }

        Problems.HasNoCoordinates(boardBody);
    }

    [Fact]
    public async Task Walking_the_segment_backwards_or_off_the_corridor_is_not_a_pass()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (segmentId, area) = await NewSegmentAsync();

        var backwards = await RunAsync(api, anna, [(area.X + SegmentLength + 40, area.Y), (area.X - 40, area.Y)], 1.4);
        var detour = await RunAsync(
            api, anna, [(area.X - 40, area.Y), (area.X + 150, area.Y), (area.X + 150, area.Y + 60), (area.X + 170, area.Y), (area.X + SegmentLength + 40, area.Y)], 1.4);

        Assert.Equal(0, await ProcessAsync(api, backwards));
        Assert.Equal(0, await ProcessAsync(api, detour));
        await using var db = database.CreateContext();
        Assert.False(await db.SegmentEfforts.AnyAsync(e => e.SegmentId == segmentId, Cancel));
        Assert.True(await db.Runs.Where(r => r.Id == backwards || r.Id == detour).AllAsync(r => r.SegmentsProcessedAt != null, Cancel));
    }

    [Fact]
    public async Task Faster_pass_dethrones_the_king_and_the_event_waits_for_the_public_boundary()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (segmentId, area) = await NewSegmentAsync();

        await ProcessAsync(api, await RunAlongAsync(api, anna, area, speed: 1.4));
        api.Time.Advance(TimeSpan.FromMinutes(30)); // корона Анны публична
        await ProcessAsync(api, await RunAlongAsync(api, boris, area, speed: 2.5));

        await using (var db = database.CreateContext())
        {
            var borisEffort = await db.SegmentEfforts.SingleAsync(e => e.SegmentId == segmentId && e.UserId == borisId, Cancel);
            var dethroned = await db.SegmentDethronements.SingleAsync(d => d.SegmentId == segmentId, Cancel);
            Assert.Equal((annaId, League.Run, borisEffort.Id, true, borisEffort.Season), (dethroned.UserId, dethroned.League, dethroned.EffortId, dethroned.AllTime, dethroned.Season));
            Assert.True(dethroned.VisibleAt >= borisEffort.VisibleAt && dethroned.VisibleAt > api.Time.GetUtcNow()); // не раньше самого прохода
        }

        // Пока проход Бориса скрыт, Анна по-прежнему видит себя королём.
        Assert.True((await anna.GetFromJsonAsync<SegmentResponse>($"/segments/{segmentId}", Json, Cancel))!.AllTimeCrown!.Me);

        // Борис улучшает свой же рекорд — себя он не свергает, а Анну дважды не свергают.
        await ProcessAsync(api, await RunAlongAsync(api, boris, area, speed: 3.0));
        api.Time.Advance(TimeSpan.FromMinutes(30));
        var seen = (await anna.GetFromJsonAsync<SegmentResponse>($"/segments/{segmentId}", Json, Cancel))!;
        var board = (await anna.GetFromJsonAsync<SegmentLeaderboardResponse>($"/segments/{segmentId}/leaderboard", Json, Cancel))!;

        await using (var db = database.CreateContext())
        {
            Assert.Equal(1, await db.SegmentDethronements.CountAsync(d => d.SegmentId == segmentId, Cancel));
            Assert.Equal(3, await db.SegmentEfforts.CountAsync(e => e.SegmentId == segmentId, Cancel));
        }

        Assert.False(seen.AllTimeCrown!.Me);
        Assert.Equal(2, seen.Legend!.Efforts); // у Бориса два прохода
        Assert.Equal([1, 2], board.Entries.Select(e => e.Rank)); // по одному лучшему времени на игрока
        Assert.Equal(new SegmentEntry(2, await NameAsync(annaId), seen.MyBestTimeMs!.Value, true), board.Mine);
    }

    [Fact]
    public async Task Leaderboard_ranks_each_players_best_time_and_hides_passes_not_yet_public()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (_, veraId) = await api.CreatePlayerClientAsync();
        var (gleb, glebId) = await api.CreatePlayerClientAsync();
        var (segmentId, _) = await NewSegmentAsync();
        var now = api.Time.GetUtcNow();
        await using (var db = database.CreateContext())
        {
            await db.Users.Where(u => u.Id == veraId).ExecuteUpdateAsync(set => set.SetProperty(u => u.PublicProfile, true), Cancel);
        }

        await AddEffortAsync(api, annaId, segmentId, 120_000, now.AddHours(-2), season: null);
        await AddEffortAsync(api, annaId, segmentId, 100_000, now.AddHours(-2), season: null);
        await AddEffortAsync(api, borisId, segmentId, 90_000, now.AddHours(-2), season: 7);
        await AddEffortAsync(api, veraId, segmentId, 100_000, now.AddHours(-1), season: null); // то же время, что у Анны, но позже
        await AddEffortAsync(api, glebId, segmentId, 80_000, now.AddMinutes(10), season: null); // ещё скрыт от всех, кроме Глеба

        var board = (await anna.GetFromJsonAsync<SegmentLeaderboardResponse>($"/segments/{segmentId}/leaderboard", Json, Cancel))!;
        var seasonal = (await anna.GetFromJsonAsync<SegmentLeaderboardResponse>($"/segments/{segmentId}/leaderboard?season=7", Json, Cancel))!;
        var glebsOwn = (await gleb.GetFromJsonAsync<SegmentLeaderboardResponse>($"/segments/{segmentId}/leaderboard", Json, Cancel))!;
        var bike = (await boris.GetFromJsonAsync<SegmentLeaderboardResponse>($"/segments/{segmentId}/leaderboard?league=bike", Json, Cancel))!;

        var borisName = Gorodki.Api.Features.Leaderboards.LeaderboardEndpoints.Pseudonym(borisId);
        Assert.Equal((League.Run, (int?)null), (board.League, board.Season));
        Assert.Equal(
            [new SegmentEntry(1, borisName, 90_000, false), new SegmentEntry(2, await NameAsync(annaId), 100_000, true), new SegmentEntry(2, await NameAsync(veraId), 100_000, false)],
            board.Entries);
        Assert.Equal(new SegmentEntry(2, await NameAsync(annaId), 100_000, true), board.Mine);
        Assert.Equal(7, seasonal.Season);
        Assert.Equal([new SegmentEntry(1, borisName, 90_000, false)], seasonal.Entries);
        Assert.Null(seasonal.Mine); // в сезоне 7 у Анны проходов нет
        Assert.Equal(new SegmentEntry(1, await NameAsync(glebId), 80_000, true), glebsOwn.Entries[0]); // свой проход — сразу
        Assert.Empty(bike.Entries); // короны и таблицы — раздельно по лигам
        Assert.Equal((400, "segments_invalid"), await Problems.OfAsync(await anna.GetAsync($"/segments/{segmentId}/leaderboard?league=car", Cancel), Cancel));
    }

    [Fact]
    public async Task Season_crown_is_the_best_pass_of_the_current_season_and_legend_counts_30_days()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync(); // до сдвига часов: токен «из будущего» не прошёл бы проверку
        var (_, borisId) = await api.CreatePlayerClientAsync();
        var (segmentId, _) = await NewSegmentAsync();
        var inSeasonZero = SeasonCalendar.MinskMidnight(new DateOnly(2026, 11, 20)).AddHours(9);
        if (api.Time.GetUtcNow() < inSeasonZero)
        {
            api.Time.SetUtcNow(inSeasonZero);
        }

        var now = api.Time.GetUtcNow();
        int season;
        await using (var scope = api.Services.CreateAsyncScope())
        {
            season = (await scope.ServiceProvider.GetRequiredService<SeasonStore>().CalendarAsync(Cancel)).At(now)!.Number;
        }

        await AddEffortAsync(api, borisId, segmentId, 60_000, now.AddDays(-40), season: null); // рекорд за всё время, старый
        await AddEffortAsync(api, annaId, segmentId, 70_000, now.AddHours(-3), season: season);
        await AddEffortAsync(api, annaId, segmentId, 75_000, now.AddHours(-2), season: season);

        var list = (await anna.GetFromJsonAsync<SegmentListResponse>("/segments", Json, Cancel))!;
        var detail = (await anna.GetFromJsonAsync<SegmentResponse>($"/segments/{segmentId}", Json, Cancel))!;

        var annaName = await NameAsync(annaId);
        Assert.Equal(new SegmentEntry(1, annaName, 70_000, true), list.Segments.Single(s => s.Id == segmentId).SeasonCrown);
        Assert.Equal(new SegmentEntry(1, annaName, 70_000, true), detail.SeasonCrown);
        Assert.Equal(60_000, detail.AllTimeCrown!.TimeMs); // Борис — король за всё время
        Assert.Equal(new SegmentLegend(annaName, 2, true), detail.Legend); // проход Бориса 40 дней назад — вне окна
        Assert.Equal(70_000, detail.MyBestTimeMs);
    }

    /// <summary>Свой отрезок теста: прямая 300 м на восток от начала нового места (метры от <see cref="WalkOrigin"/>).</summary>
    private async Task<(Guid Id, (double X, double Y) Area)> NewSegmentAsync()
    {
        var area = NewArea();
        var id = Guid.NewGuid();
        var x = WalkOrigin.X + area.X;
        var y = WalkOrigin.Y + area.Y;
        await using var db = database.CreateContext();
        db.Segments.Add(new SegmentEntity
        {
            Id = id,
            Name = $"Тестовый отрезок {id.ToString("N")[..8]}",
            Line = GeoOps.Factory.CreateLineString([new Coordinate(x, y), new Coordinate(x + SegmentLength, y)]),
            LengthMeters = SegmentLength,
        });
        await db.SaveChangesAsync(Cancel);
        return (id, area);
    }

    /// <summary>Забег вдоль всего отрезка теста — от 40 м до старта до 40 м после финиша.</summary>
    private Task<Guid> RunAlongAsync(ApiFactory api, HttpClient client, (double X, double Y) area, double speed) =>
        RunAsync(api, client, [(area.X - 40, area.Y), (area.X + SegmentLength + 40, area.Y)], speed);

    /// <summary>Забег по вершинам со скоростью <paramref name="speed"/>: старт, куски с датчиками, завершение.</summary>
    private async Task<Guid> RunAsync(ApiFactory api, HttpClient client, (double X, double Y)[] vertices, double speed)
    {
        var start = await StartWalkAsync(Cancel, api, client);
        var points = WalkPoints(start, vertices, speed);
        foreach (var chunk in WalkChunks(api, points, speed))
        {
            var put = await client.PutAsJsonAsync($"/runs/{start.Id}/chunks/{chunk.Points![0].Seq}", chunk, Json, Cancel);
            Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        }

        var finish = await client.PostAsJsonAsync(
            $"/runs/{start.Id}/finish", new FinishRunRequest(points[^1].T, points.Count - 1, api.Time.GetUtcNow().ToUnixTimeMilliseconds()), Json, Cancel);
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
        return start.Id;
    }

    private static async Task<int?> ProcessAsync(ApiFactory api, Guid runId)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SegmentEffortProcessor>().ProcessRunAsync(runId, CancellationToken.None);
    }

    /// <summary>Проход прямо в базе (с забегом, к которому он относится) — для таблиц, где сам забег не важен.</summary>
    private async Task AddEffortAsync(ApiFactory api, Guid userId, Guid segmentId, long elapsedMs, DateTimeOffset visibleAt, int? season)
    {
        await using var db = database.CreateContext();
        var startedAt = visibleAt.AddMinutes(-30);
        var run = new RunEntity
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            League = League.Run,
            Source = RunSource.Live,
            ConfigVersion = 1,
            StartedAt = startedAt,
            EndedAt = startedAt.AddMinutes(10),
            Status = RunStatus.Finished,
            CreatedAt = api.Time.GetUtcNow(),
            DeviceId = Guid.NewGuid(),
            AppVersion = "0.1.0 (1)",
            MotionAuthorized = true,
            LastSeq = -1,
            SegmentsProcessedAt = startedAt.AddMinutes(10),
        };
        db.Runs.Add(run);
        db.SegmentEfforts.Add(new SegmentEffortEntity
        {
            SegmentId = segmentId,
            UserId = userId,
            RunId = run.Id,
            League = League.Run,
            Season = season,
            ElapsedMs = elapsedMs,
            StartedAt = startedAt,
            VisibleAt = visibleAt,
        });
        await db.SaveChangesAsync(Cancel);
    }

    private async Task<SegmentEffortEntity> SingleEffortAsync(Guid segmentId)
    {
        await using var db = database.CreateContext();
        return await db.SegmentEfforts.AsNoTracking().SingleAsync(e => e.SegmentId == segmentId, Cancel);
    }

    private async Task<string> NameAsync(Guid userId)
    {
        await using var db = database.CreateContext();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).SingleAsync(Cancel);
    }
}
