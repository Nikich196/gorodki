using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Me;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.IntegrationTests;

/// <summary>Сценарии входа целиком: настоящий сервер в памяти + настоящая база в контейнере, Google — подставной.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class AuthFlowTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task New_player_needs_invite_age_and_consent_then_gets_in()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var client = api.CreateClient();
        var invite = await CreateInviteAsync(maxUses: 3);
        var subject = NewSubject();

        var noInvite = await client.PostAsJsonAsync("/auth/google", new GoogleSignInRequest(subject, null, true, 1), Cancel);
        var noAge = await client.PostAsJsonAsync("/auth/google", new GoogleSignInRequest(subject, invite, false, 1), Cancel);
        var ok = await client.PostAsJsonAsync("/auth/google", new GoogleSignInRequest(subject, invite, true, 1), Cancel);

        Assert.Equal("invite_required", await CodeOf(noInvite));
        Assert.Equal("age_confirmation_required", await CodeOf(noAge));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var session = await ok.Content.ReadFromJsonAsync<SessionResponse>(Cancel);
        Assert.NotNull(session);
        Assert.True(session.IsNewUser);
        Assert.Equal(1, await UsedCountAsync(invite));
    }

    [Fact]
    public async Task Returning_player_signs_in_without_invite_and_sees_profile()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var client = api.CreateClient();
        var subject = NewSubject();
        var invite = await CreateInviteAsync(maxUses: 1);
        await client.PostAsJsonAsync("/auth/google", new GoogleSignInRequest(subject, invite, true, 1), Cancel);

        var again = await client.PostAsJsonAsync("/auth/google", new GoogleSignInRequest(subject, null, false, null), Cancel);
        var session = await again.Content.ReadFromJsonAsync<SessionResponse>(Cancel);
        Assert.NotNull(session);
        Assert.False(session.IsNewUser);

        var anonymous = await client.GetAsync("/me", Cancel);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        var me = await client.GetFromJsonAsync<MeResponse>("/me", Cancel);
        Assert.NotNull(me);
        Assert.StartsWith("Бегун-", me.DisplayName, StringComparison.Ordinal);
        Assert.InRange(me.ColorIndex, 0, 11);
        Assert.Equal("player", me.Role);
    }

    [Fact]
    public async Task Refresh_rotates_and_tolerates_a_lost_response_for_60_seconds()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var client = api.CreateClient();
        var first = await SignUpAsync(client);

        var second = await RefreshAsync(client, first.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, second.Status);

        // Телефон не получил ответ и повторил старый токен через 20 секунд — это не кража.
        api.Time.Advance(TimeSpan.FromSeconds(20));
        var retry = await RefreshAsync(client, first.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, retry.Status);

        var third = await RefreshAsync(client, second.Session!.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, third.Status);
    }

    [Fact]
    public async Task Reusing_an_old_token_after_the_grace_window_revokes_the_whole_family()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var client = api.CreateClient();
        var first = await SignUpAsync(client);
        var second = await RefreshAsync(client, first.RefreshToken);

        api.Time.Advance(TimeSpan.FromSeconds(61));
        var stolen = await RefreshAsync(client, first.RefreshToken);
        var legit = await RefreshAsync(client, second.Session!.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, stolen.Status);
        Assert.Equal("refresh_reused", stolen.Code);
        Assert.Equal(HttpStatusCode.Unauthorized, legit.Status); // вся семья отозвана — нужно войти заново
    }

    [Fact]
    public async Task Logout_revokes_the_session()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var client = api.CreateClient();
        var session = await SignUpAsync(client);

        var logout = await client.PostAsJsonAsync("/auth/logout", new RefreshRequest(session.RefreshToken), Cancel);
        var after = await RefreshAsync(client, session.RefreshToken);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, after.Status);
        Assert.Equal("refresh_invalid", after.Code);
    }

    [Fact]
    public async Task Revoked_family_stays_revoked_even_inside_the_grace_window()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var client = api.CreateClient();
        var a = await SignUpAsync(client);
        var b = await RefreshAsync(client, a.RefreshToken); // A заменён на B

        api.Time.Advance(TimeSpan.FromSeconds(61));
        var c = await RefreshAsync(client, b.Session!.RefreshToken); // B заменён на C только что
        var stolen = await RefreshAsync(client, a.RefreshToken); // A — давно заменён: похоже на кражу, семья отозвана
        var sneaky = await RefreshAsync(client, b.Session.RefreshToken); // B в «окне», но живых токенов у входа нет

        Assert.Equal(HttpStatusCode.OK, c.Status);
        Assert.Equal("refresh_reused", stolen.Code);
        Assert.Equal(HttpStatusCode.Unauthorized, sneaky.Status);
    }

    [Fact]
    public async Task Forged_google_token_is_refused()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var client = api.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/google", new GoogleSignInRequest("not-a-google-token", await CreateInviteAsync(maxUses: 1), true, 1), Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("google_token_invalid", await CodeOf(response));
    }

    [Fact]
    public async Task Invite_cannot_be_used_more_times_than_allowed()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var client = api.CreateClient();
        var invite = await CreateInviteAsync(maxUses: 1);

        var firstPlayer = await client.PostAsJsonAsync("/auth/google", new GoogleSignInRequest(NewSubject(), invite, true, 1), Cancel);
        var secondPlayer = await client.PostAsJsonAsync("/auth/google", new GoogleSignInRequest(NewSubject(), invite, true, 1), Cancel);

        Assert.Equal(HttpStatusCode.OK, firstPlayer.StatusCode);
        Assert.Equal("invite_invalid", await CodeOf(secondPlayer));
    }

    [Fact]
    public async Task Simultaneous_sign_ups_do_not_overuse_an_invite()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var invite = await CreateInviteAsync(maxUses: 2);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            api.CreateClient().PostAsJsonAsync("/auth/google", new GoogleSignInRequest(NewSubject(), invite, true, 1), Cancel)));

        Assert.Equal(2, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.Equal(2, await UsedCountAsync(invite));
    }

    // MARK: — вспомогательное

    private static string NewSubject() => $"google:{Guid.NewGuid():N}";

    private async Task<SessionResponse> SignUpAsync(HttpClient client)
    {
        var invite = await CreateInviteAsync(maxUses: 1);
        var response = await client.PostAsJsonAsync("/auth/google", new GoogleSignInRequest(NewSubject(), invite, true, 1), Cancel);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SessionResponse>(Cancel))!;
    }

    private async Task<(HttpStatusCode Status, SessionResponse? Session, string? Code)> RefreshAsync(HttpClient client, string token)
    {
        var response = await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(token), Cancel);
        return response.IsSuccessStatusCode
            ? (response.StatusCode, await response.Content.ReadFromJsonAsync<SessionResponse>(Cancel), null)
            : (response.StatusCode, null, await CodeOf(response));
    }

    private async Task<string?> CodeOf(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        return problem.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private async Task<string> CreateInviteAsync(int maxUses)
    {
        await using var db = database.CreateContext();
        var code = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        db.Invites.Add(new InviteEntity { Code = code, MaxUses = maxUses, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(Cancel);
        return code;
    }

    private async Task<int> UsedCountAsync(string code)
    {
        await using var db = database.CreateContext();
        return await db.Invites.Where(i => i.Code == code).Select(i => i.UsedCount).SingleAsync(Cancel);
    }
}
