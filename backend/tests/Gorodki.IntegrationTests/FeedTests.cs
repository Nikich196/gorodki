using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Players;
using Gorodki.Api.Features.Social;
using Gorodki.Api.Features.Territory;
using Gorodki.Domain.Time;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Лента — <c>/feed</c>, блокировки — <c>/me/blocks</c> (PLAN.md, §3.8). Задача #144 для Егора: тесты со <c>Skip</c>
/// снимаются вместе с реализацией (друзья — из E14a). Главное здесь — граница публичности: пост о захвате появляется не
/// раньше самого захвата на карте, без координат и без времени.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class FeedTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(Skip = "ЗАДАЧА #144")]
    public async Task A_friends_capture_appears_only_after_the_public_boundary_without_place_or_time()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        await BefriendAsync(anna, boris);

        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100))).RunId);
        var day = GameClock.GameDayOf(api.Time.GetUtcNow()).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.DoesNotContain((await FeedAsync(boris, "friends")).Posts, p => p.AuthorId == annaId); // захват ещё скрыт
        Assert.DoesNotContain((await FeedAsync(anna, "friends")).Posts, p => p.AuthorId == annaId); // и для самой Анны

        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        var response = await boris.GetAsync("/feed?scope=friends", Cancel);

        var post = Assert.Single((await Problems.BodyAsync<FeedResponse>(response, Cancel)).Posts, p => p.AuthorId == annaId);
        Assert.Equal((FeedPostKind.Capture, day, false), (post.Kind, post.Day, post.Mine));
        Assert.InRange(post.CapturedSquareMeters ?? 0, 9_500, 10_500);
        Assert.Equal(4, post.Id.Version); // случайный номер: в UUIDv7 зашито время захвата
        Problems.HasNoCoordinates(await response.Content.ReadAsStringAsync(Cancel));
    }

    [Fact(Skip = "ЗАДАЧА #144")]
    public async Task Respect_counts_once_and_reports_need_a_visible_post()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        await BefriendAsync(anna, boris);
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100))).RunId);
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
    }

    [Fact(Skip = "ЗАДАЧА #144")]
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
        Assert.Contains((await FeedAsync(anna, "all")).Posts, p => p.AuthorId == carlId);
        Assert.Equal((400, "feed_invalid"), await Problems.OfAsync(await anna.GetAsync("/feed?scope=everyone", Cancel), Cancel));

        Assert.Equal(HttpStatusCode.NoContent, (await anna.PutAsync($"/me/blocks/{carlId}", null, Cancel)).StatusCode);

        Assert.DoesNotContain((await FeedAsync(anna, "all")).Posts, p => p.AuthorId == carlId);
        Assert.DoesNotContain((await FeedAsync(carl, "all")).Posts, p => p.AuthorId == annaId);
        Assert.Equal(carlId, Assert.Single((await anna.GetFromJsonAsync<List<PlayerResponse>>("/me/blocks", Json, Cancel))!).Id);
        Assert.Equal((400, "block_self"), await Problems.OfAsync(await anna.PutAsync($"/me/blocks/{annaId}", null, Cancel), Cancel));

        Assert.Equal(HttpStatusCode.NoContent, (await anna.DeleteAsync($"/me/blocks/{carlId}", Cancel)).StatusCode);
        Assert.Contains((await FeedAsync(anna, "all")).Posts, p => p.AuthorId == carlId);
    }

    // MARK: — вспомогательное

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
