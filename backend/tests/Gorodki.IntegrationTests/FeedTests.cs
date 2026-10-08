using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Clans;
using Gorodki.Api.Features.Players;
using Gorodki.Api.Features.Social;
using Gorodki.Api.Features.Territory;
using Gorodki.Domain.Time;
using Microsoft.EntityFrameworkCore;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Лента — <c>/feed</c>, блокировки — <c>/me/blocks</c> (PLAN.md, §3.8), задача #144 (сделано Claude 07.10). Главное здесь —
/// граница публичности: пост о захвате появляется не раньше самого захвата на карте, без координат и без времени. Лента
/// «друзья и клан» пока — свои и соклановцы: друзей (#143) ещё нет, тест с друзьями ждёт эту задачу.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class FeedTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_clan_mates_capture_appears_only_after_the_public_boundary_without_place_or_time()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        await SameClanAsync(anna, boris);

        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100))).RunId);
        var day = GameClock.GameDayOf(api.Time.GetUtcNow()).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.DoesNotContain((await FeedAsync(boris, "friends")).Posts, p => p.AuthorId == annaId); // захват ещё скрыт
        Assert.DoesNotContain((await FeedAsync(anna, "friends")).Posts, p => p.AuthorId == annaId); // и для самой Анны

        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep - TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.DoesNotContain((await FeedAsync(boris, "all")).Posts, p => p.AuthorId == annaId); // 20 минут без секунды — ещё рано

        api.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var response = await boris.GetAsync("/feed?scope=friends", Cancel);

        var post = Assert.Single((await Problems.BodyAsync<FeedResponse>(response, Cancel)).Posts, p => p.AuthorId == annaId);
        Assert.Equal((FeedPostKind.Capture, day, false), (post.Kind, post.Day, post.Mine));
        Assert.InRange(post.CapturedSquareMeters ?? 0, 9_500, 10_500);
        Assert.Equal(4, post.Id.Version); // случайный номер: в UUIDv7 зашито время захвата
        Problems.HasNoCoordinates(await response.Content.ReadAsStringAsync(Cancel));
    }

    [Fact]
    public async Task Respect_counts_once_and_reports_need_a_visible_post()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        await SameClanAsync(anna, boris);
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100))).RunId);
        var hidden = await LatestPostIdAsync(annaId);
        Assert.Equal((404, "post_not_found"), await Problems.OfAsync(await boris.PostAsync($"/feed/{hidden}/respect", null, Cancel), Cancel));
        Assert.Equal((404, "post_not_found"), await Problems.OfAsync(
            await boris.PostAsJsonAsync($"/feed/{hidden}/report", new ReportPostRequest("до границы"), Json, Cancel), Cancel));
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        var post = (await FeedAsync(boris, "friends")).Posts.Single(p => p.AuthorId == annaId);

        Assert.Equal(HttpStatusCode.NoContent, (await boris.PostAsync($"/feed/{post.Id}/respect", null, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await boris.PostAsync($"/feed/{post.Id}/respect", null, Cancel)).StatusCode);
        var again = (await FeedAsync(boris, "friends")).Posts.Single(p => p.Id == post.Id);
        Assert.Equal((1, true), (again.Respects, again.RespectedByMe));

        var tooLong = await boris.PostAsJsonAsync($"/feed/{post.Id}/report", new ReportPostRequest(new string('ж', 201)), Json, Cancel);
        var unknown = await boris.PostAsJsonAsync($"/feed/{Guid.NewGuid()}/report", new ReportPostRequest(null), Json, Cancel);
        Assert.Equal((400, "report_invalid"), await Problems.OfAsync(tooLong, Cancel));
        Assert.Equal((404, "post_not_found"), await Problems.OfAsync(unknown, Cancel));
        Assert.Equal(HttpStatusCode.NoContent, (await boris.PostAsJsonAsync(
            $"/feed/{post.Id}/report", new ReportPostRequest("спам"), Json, Cancel)).StatusCode);
        await using var db = database.CreateContext();
        var report = await db.FeedReports.AsNoTracking().SingleAsync(r => r.PostId == post.Id, Cancel);
        Assert.Equal((borisId, "спам"), (report.ReporterId, report.Reason)); // строка для админа: кто, на что, причина
    }

    [Fact]
    public async Task Strangers_are_only_in_the_all_scope_and_blocking_hides_both_ways()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (carl, carlId) = await api.CreatePlayerClientAsync(); // не друг
        var area = NewArea();
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, carl, Square(area, 400, 0, 100))).RunId);
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);

        Assert.DoesNotContain((await FeedAsync(anna, "friends")).Posts, p => p.AuthorId == carlId);
        Assert.Contains(await AllPagesAsync(anna), p => p.AuthorId == carlId);
        Assert.Equal((400, "feed_invalid"), await Problems.OfAsync(await anna.GetAsync("/feed?scope=everyone", Cancel), Cancel));

        Assert.Equal(HttpStatusCode.NoContent, (await anna.PutAsync($"/me/blocks/{carlId}", null, Cancel)).StatusCode);

        Assert.DoesNotContain(await AllPagesAsync(anna), p => p.AuthorId == carlId);
        Assert.DoesNotContain(await AllPagesAsync(carl), p => p.AuthorId == annaId);
        Assert.Equal(carlId, Assert.Single((await anna.GetFromJsonAsync<List<PlayerResponse>>("/me/blocks", Json, Cancel))!).Id);
        Assert.Equal((400, "block_self"), await Problems.OfAsync(await anna.PutAsync($"/me/blocks/{annaId}", null, Cancel), Cancel));

        Assert.Equal(HttpStatusCode.NoContent, (await anna.DeleteAsync($"/me/blocks/{carlId}", Cancel)).StatusCode);
        Assert.Contains(await AllPagesAsync(anna), p => p.AuthorId == carlId);
    }

    [Fact]
    public async Task A_run_post_shows_only_the_distance_once_the_runs_end_is_public_and_pages_follow_the_cursor()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var run = await WalkAndFinishAsync(Cancel, api, anna, Rectangle(NewArea(), 0, 0, 500, 100)); // 1,2 км
        Assert.Null(await VisitAsync(api, run.Id)); // конец забега ещё не публичен — визитов и поста нет
        Assert.DoesNotContain((await FeedAsync(anna, "friends")).Posts, p => p.AuthorId == annaId);

        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        Assert.NotNull(await VisitAsync(api, run.Id));
        var response = await anna.GetAsync("/feed", Cancel); // без scope — «друзья и клан»
        var post = Assert.Single((await Problems.BodyAsync<FeedResponse>(response, Cancel)).Posts, p => p.AuthorId == annaId);

        Assert.Equal((FeedPostKind.Run, true, (double?)null), (post.Kind, post.Mine, post.CapturedSquareMeters));
        Assert.InRange(post.DistanceMeters ?? 0, 1_100, 1_300);
        Problems.HasNoCoordinates(await response.Content.ReadAsStringAsync(Cancel));

        // Курсор — номер поста: следующая страница начинается после него, чужой или выдуманный курсор — 400.
        var all = await AllPagesAsync(anna);
        Assert.Equal(all.Count, all.Select(p => p.Id).Distinct().Count());
        Assert.Equal((400, "feed_invalid"), await Problems.OfAsync(await anna.GetAsync($"/feed?scope=all&cursor={Guid.NewGuid()}", Cancel), Cancel));
        Assert.Equal((400, "feed_invalid"), await Problems.OfAsync(await anna.GetAsync("/feed?scope=all&cursor=12", Cancel), Cancel));
    }

    [Fact(Skip = "ЗАДАЧА #143")]
    public async Task A_friends_posts_are_in_the_friends_scope()
    {
        // Друзья (E14a, Егор): когда появятся, посты друга — в ленте «друзья и клан» (FeedEndpoints.Circle), а блокировка
        // снимает дружбу (FeedEndpoints.BlockPlayer).
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        await BefriendAsync(anna, boris);
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100))).RunId);
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);

        Assert.Contains((await FeedAsync(boris, "friends")).Posts, p => p.AuthorId == annaId);
    }

    // MARK: — вспомогательное

    /// <summary>Вся лента «все» по страницам: база общая, чужих постов в ней много.</summary>
    private async Task<List<FeedPost>> AllPagesAsync(HttpClient client)
    {
        var posts = new List<FeedPost>();
        string? cursor = null;
        do
        {
            var page = (await client.GetFromJsonAsync<FeedResponse>(
                cursor is null ? "/feed?scope=all" : $"/feed?scope=all&cursor={cursor}", Json, Cancel))!;
            posts.AddRange(page.Posts);
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return posts;
    }

    private async Task<Guid> LatestPostIdAsync(Guid authorId)
    {
        await using var db = database.CreateContext();
        return await db.FeedPosts.Where(p => p.AuthorId == authorId).Select(p => p.Id).SingleAsync(Cancel);
    }

    /// <summary>Один клан: «друзья и клан» (§3.8).</summary>
    private async Task SameClanAsync(HttpClient leader, HttpClient member)
    {
        var created = await leader.PostAsJsonAsync("/clans", new CreateClanRequest { Name = $"Лента {Guid.NewGuid().ToString("N")[..8]}" }, Json, Cancel);
        var clan = await Problems.BodyAsync<ClanResponse>(created, Cancel);
        Assert.Equal(HttpStatusCode.OK, (await member.PostAsJsonAsync("/clans/join", new JoinClanRequest { Code = clan.InviteCode! }, Json, Cancel)).StatusCode);
    }

    private async Task<FeedResponse> FeedAsync(HttpClient client, string scope) =>
        (await client.GetFromJsonAsync<FeedResponse>($"/feed?scope={scope}", Json, Cancel))!;

    /// <summary>Друзья по коду (E14a).</summary>
    private async Task BefriendAsync(HttpClient first, HttpClient second)
    {
        var firstCode = (await first.GetFromJsonAsync<FriendsResponse>("/friends", Json, Cancel))!.MyCode;
        var secondCode = (await second.GetFromJsonAsync<FriendsResponse>("/friends", Json, Cancel))!.MyCode;
        await second.PostAsJsonAsync("/friends", new AddFriendRequest { Code = firstCode }, Json, Cancel);
        var back = await first.PostAsJsonAsync("/friends", new AddFriendRequest { Code = secondCode }, Json, Cancel);
        Assert.Equal(FriendStatus.Friend, (await Problems.BodyAsync<FriendResponse>(back, Cancel)).Status);
    }
}
