using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Clans;
using Gorodki.Api.Features.HallOfFame;
using Gorodki.Api.Features.Leaderboards;
using Gorodki.Api.Features.Me;
using Gorodki.Api.Features.Scoring;
using Gorodki.Api.Features.Seasons;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Scoring;
using Gorodki.Domain.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Зал славы — <c>GET /hall-of-fame</c> и его снимок (PLAN.md, §3.4), задача #137 (сделано Claude 15.10): сезон попадает в Зал
/// один раз — по итогу на момент закрытия (<c>ScoreBook.FinalTotalsAsync</c>, 04:00 первого дня следующего сезона) и после
/// смены на следующий сезон; места 1–3 игроков и первое место клана по лиге; ник — по согласию на момент запроса; удаление
/// аккаунта обезличивает строку (это проверяет и <c>AccountDeletionTests</c>). Время — настоящие сезоны (С0 с 16.11, С1 с
/// 30.11), как в <c>TerritoryLeaderboardTests</c>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class HallOfFameTests(DatabaseFixture database)
{
    private static readonly DateTimeOffset SeasonOne = new(2026, 11, 29, 21, 0, 0, TimeSpan.Zero); // 30.11 00:00 по Минску

    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_season_that_is_still_going_is_not_in_the_hall()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var seasons = (await anna.GetFromJsonAsync<SeasonsResponse>("/seasons", Json, Cancel))!;

        // Очки Анны уже видны всем — но сезон идёт: снимок делается только по итогу закрытого сезона.
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100))).RunId);
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        var hall = (await anna.GetFromJsonAsync<HallOfFameResponse>("/hall-of-fame", Json, Cancel))!;

        Assert.DoesNotContain(hall.Seasons, s => s.Season == seasons.Current);
        Assert.DoesNotContain(hall.Seasons.SelectMany(s => s.Entries), e => e.PlayerId == annaId);
        Assert.Equal(hall.Seasons.Select(s => s.Season).OrderDescending(), hall.Seasons.Select(s => s.Season)); // новые сверху
    }

    [Fact]
    public async Task Closed_season_enters_the_hall_once_with_its_final_top_three_and_top_clan()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync(); // клиенты — до сдвига часов
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (vera, veraId) = await api.CreatePlayerClientAsync();
        var (gleb, glebId) = await api.CreatePlayerClientAsync();
        await ForgetAsync(season: 0); // база общая: Зал С0 мог снять другой прогон этого теста
        var closesAt = SeasonOne + ScoreBook.CloseGrace;

        // В Сезоне 0 Анна создаёт клан, Борис вступает. Анна согласна показывать ник, остальные — нет.
        GoTo(api, SeasonCalendar.MinskMidnight(new DateOnly(2026, 11, 20)).AddHours(12));
        var created = await anna.PostAsJsonAsync("/clans", new CreateClanRequest { Name = $"Зал {Guid.NewGuid().ToString("N")[..8]}" }, Json, Cancel);
        var clan = (await created.Content.ReadFromJsonAsync<ClanResponse>(Json, Cancel))!;
        Assert.Equal(HttpStatusCode.OK, (await boris.PostAsJsonAsync("/clans/join", new JoinClanRequest { Code = clan.InviteCode! }, Json, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anna.PutAsJsonAsync("/me/public-profile", new PublicProfileRequest { Enabled = true }, Json, Cancel)).StatusCode);

        // Очки — заметно больше, чем у игроков других тестов (база общая). Борис и Вера делят второе место, Глеб — четвёртый
        // в «Беге» (вне Зала) и первый в «Вело». Его поздние очки стали видны после закрытия — в итог они не входят.
        var day = new DateOnly(2026, 11, 20);
        await AddPointsAsync(annaId, League.Run, day, 50_000_003, visibleAt: SeasonOne - TimeSpan.FromDays(1));
        await AddPointsAsync(borisId, League.Run, day, 50_000_002, visibleAt: SeasonOne - TimeSpan.FromDays(1));
        await AddPointsAsync(veraId, League.Run, day, 50_000_002, visibleAt: SeasonOne + TimeSpan.FromHours(3)); // до закрытия
        await AddPointsAsync(glebId, League.Run, day, 50_000_001, visibleAt: SeasonOne - TimeSpan.FromDays(1));
        await AddPointsAsync(glebId, League.Run, day.AddDays(9), 100_000_000, visibleAt: closesAt + TimeSpan.FromMinutes(1));
        await AddPointsAsync(glebId, League.Bike, day, 60_000_000, visibleAt: SeasonOne - TimeSpan.FromDays(1));

        // Вера вступает в клан уже в Сезоне 1: очки Сезона 0 клану она не приносит.
        GoTo(api, SeasonOne + TimeSpan.FromHours(1));
        Assert.Equal(HttpStatusCode.OK, (await vera.PostAsJsonAsync("/clans/join", new JoinClanRequest { Code = clan.InviteCode! }, Json, Cancel)).StatusCode);

        // До закрытия — рано, даже если смена сезона прошла; в момент закрытия без смены — тоже рано.
        await SetRolloverAsync(SeasonOne);
        GoTo(api, closesAt - TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, await SnapshotAsync(api));
        GoTo(api, closesAt);
        await SetRolloverAsync(null);
        Assert.Equal(0, await SnapshotAsync(api));
        Assert.DoesNotContain((await HallAsync(boris)).Seasons, s => s.Season == 0);

        // Закрыт и сменён — снимок; повтор ничего не меняет.
        await SetRolloverAsync(SeasonOne);
        Assert.True(await SnapshotAsync(api) > 0);
        Assert.Equal(0, await SnapshotAsync(api));

        var seasonName = (await boris.GetFromJsonAsync<SeasonsResponse>("/seasons", Json, Cancel))!.Seasons.Single(s => s.Number == 0).Name;
        var hall = (await HallAsync(boris)).Seasons.Single(s => s.Season == 0);
        Assert.Equal(seasonName, hall.Name);
        var runPlayers = hall.Entries.Where(e => e.League == League.Run && e.Kind == HallOfFameKind.Player).ToList();
        Assert.Equal(
            [
                new HallOfFameEntry(HallOfFameKind.Player, League.Run, 1, annaId, null, await NameOfAsync(annaId), 50_000_003, Me: false),
                new HallOfFameEntry(HallOfFameKind.Player, League.Run, 2, borisId, null, await NameOfAsync(borisId), 50_000_002, Me: true),
                new HallOfFameEntry(HallOfFameKind.Player, League.Run, 2, veraId, null, LeaderboardEndpoints.Pseudonym(veraId), 50_000_002, Me: false),
            ],
            runPlayers.OrderBy(e => e.Place).ThenBy(e => e.Name == LeaderboardEndpoints.Pseudonym(veraId)));
        var topClan = Assert.Single(hall.Entries, e => e.League == League.Run && e.Kind == HallOfFameKind.Clan);
        Assert.Equal(new HallOfFameEntry(HallOfFameKind.Clan, League.Run, 1, null, clan.Id, clan.Name, 100_000_005, Me: true), topClan);
        var bike = Assert.Single(hall.Entries, e => e.League == League.Bike && e.Kind == HallOfFameKind.Player && e.Place == 1);
        Assert.Equal((glebId, LeaderboardEndpoints.Pseudonym(glebId), 60_000_000), (bike.PlayerId, bike.Name, bike.Value));
        Assert.Equal(hall.Entries.OrderBy(e => e.League).ThenBy(e => e.Kind).ThenBy(e => e.Place).Select(e => (e.League, e.Kind, e.Place)), hall.Entries.Select(e => (e.League, e.Kind, e.Place)));

        // Глеб видит свой ник, а Вера попросила удалить аккаунт — её строка остаётся без имени и номера.
        Assert.Equal(await NameOfAsync(glebId), (await HallAsync(gleb)).Seasons.Single(s => s.Season == 0).Entries.Single(e => e.PlayerId == glebId).Name);
        Assert.Equal(HttpStatusCode.Accepted, (await vera.DeleteAsync("/me", Cancel)).StatusCode);
        var afterDeletion = (await HallAsync(anna)).Seasons.Single(s => s.Season == 0).Entries;
        Assert.DoesNotContain(afterDeletion, e => e.PlayerId == veraId);
        Assert.Contains(afterDeletion, e => e is { League: League.Run, Kind: HallOfFameKind.Player, Place: 2, PlayerId: null, Name: null, Value: 50_000_002 });
        Assert.Contains(afterDeletion, e => e is { Kind: HallOfFameKind.Player, Place: 1, Me: true } && e.PlayerId == annaId);
    }

    // MARK: — вспомогательное

    private static void GoTo(ApiFactory api, DateTimeOffset moment) => api.Time.Advance(moment - api.Time.GetUtcNow());

    private async Task<HallOfFameResponse> HallAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<HallOfFameResponse>("/hall-of-fame", Json, Cancel))!;

    private static async Task<int> SnapshotAsync(ApiFactory api)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HallOfFameSnapshots>().RunIfDueAsync(CancellationToken.None);
    }

    private async Task<string> NameOfAsync(Guid userId)
    {
        await using var db = database.CreateContext();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).SingleAsync(Cancel);
    }

    /// <summary>Очки сезона 0 прямо в книге: строка удержания за сутки (ни захвата, ни забега ей не нужно).</summary>
    private async Task AddPointsAsync(Guid userId, League league, DateOnly day, int points, DateTimeOffset visibleAt)
    {
        await using var db = database.CreateContext();
        db.ScoreEvents.Add(new ScoreEventEntity
        {
            UserId = userId,
            League = league,
            Season = 0,
            GameDay = day,
            Kind = ScoreKind.Hold,
            Points = points,
            Basis = 0,
            EffectiveAt = SeasonCalendar.MinskMidnight(day),
            VisibleAt = visibleAt,
            CreatedAt = visibleAt,
        });
        await db.SaveChangesAsync(Cancel);
    }

    /// <summary>Отметка смены на Сезон 1: тест ставит её сам (смену с землёй проверяет <c>SeasonRolloverTests</c>).</summary>
    private async Task SetRolloverAsync(DateTimeOffset? resetAt)
    {
        await using var db = database.CreateContext();
        await db.Seasons.Where(s => s.Number == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResetAt, resetAt), Cancel);
    }

    private async Task ForgetAsync(int season)
    {
        await using var db = database.CreateContext();
        await db.JobRuns.Where(j => j.Job == HallOfFameSnapshots.Job && j.Key == season).ExecuteDeleteAsync(Cancel);
        await db.HallOfFame.Where(h => h.Season == season).ExecuteDeleteAsync(Cancel);
    }
}
