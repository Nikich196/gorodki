using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gorodki.Api.Features.Admin;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static Gorodki.IntegrationTests.RunRequests;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Админка инвайтов — <c>POST /admin/invites</c>, <c>GET /admin/invites</c>, <c>DELETE /admin/invites/{code}</c> (PLAN.md, D10).
/// Задача #114 для Егора: тесты со <c>Skip</c> снимаются вместе с реализацией (CONTRIBUTING.md, «Задачи для друга»).
/// Формат кода проверяет <c>Gorodki.Api.Tests/Admin/InviteCodesTests</c> — без базы.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AdminInvitesTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(Skip = "ЗАДАЧА #114")]
    public async Task Admin_issues_codes_that_let_new_players_register()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (admin, _) = await api.CreatePlayerClientAsync(UserRole.Admin);

        var response = await IssueAsync(admin, count: 5, maxUses: 2, note: "группа ПО-4");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var issued = (await response.Content.ReadFromJsonAsync<List<InviteResponse>>(Json, Cancel))!;
        Assert.Equal(5, issued.Count);
        Assert.Equal(5, issued.Select(i => i.Code).Distinct().Count());
        var now = api.Time.GetUtcNow().ToUnixTimeMilliseconds();
        Assert.All(issued, invite =>
            Assert.Equal((2, 0, "группа ПО-4", (long?)null, now), (invite.MaxUses, invite.UsedCount, invite.Note, invite.ExpiresAtMs, invite.CreatedAtMs)));

        // По новому коду проходит регистрация — как в AuthFlowTests, — а список показывает, что код использован один раз.
        Assert.Equal(HttpStatusCode.OK, (await SignUpAsync(api, issued[0].Code)).StatusCode);
        var listed = await ListAsync(admin);
        Assert.Equal(1, listed.Single(i => i.Code == issued[0].Code).UsedCount);
        Assert.Equal(0, listed.Single(i => i.Code == issued[1].Code).UsedCount);
    }

    [Fact(Skip = "ЗАДАЧА #114")]
    public async Task List_shows_newest_codes_first()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (admin, _) = await api.CreatePlayerClientAsync(UserRole.Admin);
        var older = (await IssuedAsync(admin, count: 1, maxUses: 1))[0].Code;
        api.Time.Advance(TimeSpan.FromMinutes(1));
        var newer = (await IssuedAsync(admin, count: 1, maxUses: 1))[0].Code;

        var codes = (await ListAsync(admin)).Select(i => i.Code).ToList();

        Assert.True(codes.IndexOf(newer) < codes.IndexOf(older), "Новые коды — сверху");
    }

    [Fact(Skip = "ЗАДАЧА #114")]
    public async Task Only_an_admin_manages_invites()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (admin, _) = await api.CreatePlayerClientAsync(UserRole.Admin);
        var (player, _) = await api.CreatePlayerClientAsync();
        var code = (await IssuedAsync(admin, count: 1, maxUses: 1))[0].Code;

        Assert.Equal((403, "admin_only"), await ProblemOf(await IssueAsync(player, count: 1, maxUses: 1)));
        Assert.Equal((403, "admin_only"), await ProblemOf(await player.GetAsync("/admin/invites", Cancel)));
        Assert.Equal((403, "admin_only"), await ProblemOf(await player.DeleteAsync($"/admin/invites/{code}", Cancel)));

        // И ничего не изменилось: код игрок не погасил — по нему по-прежнему регистрируются.
        Assert.Equal(HttpStatusCode.OK, (await SignUpAsync(api, code)).StatusCode);
    }

    [Fact(Skip = "ЗАДАЧА #114")]
    public async Task Requests_out_of_range_are_refused_and_create_nothing()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (admin, _) = await api.CreatePlayerClientAsync(UserRole.Admin);
        var now = api.Time.GetUtcNow().ToUnixTimeMilliseconds();
        var before = await InviteCountAsync();

        CreateInvitesRequest[] wrong =
        [
            new() { Count = 0, MaxUses = 1 },
            new() { Count = InviteEndpoints.MaxCount + 1, MaxUses = 1 },
            new() { Count = 1, MaxUses = 0 },
            new() { Count = 1, MaxUses = InviteEndpoints.MaxUsesLimit + 1 },
            new() { Count = 1, MaxUses = 1, ExpiresAtMs = now }, // срок — только в будущем
            new() { Count = 1, MaxUses = 1, Note = new string('я', InviteEndpoints.MaxNoteLength + 1) },
        ];
        foreach (var request in wrong)
        {
            var response = await admin.PostAsJsonAsync("/admin/invites", request, Json, Cancel);
            Assert.Equal((400, "invites_invalid"), await ProblemOf(response));
        }

        Assert.Equal(before, await InviteCountAsync());
    }

    [Fact(Skip = "ЗАДАЧА #114")]
    public async Task Revoked_code_no_longer_lets_anyone_register()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (admin, _) = await api.CreatePlayerClientAsync(UserRole.Admin);
        var code = (await IssuedAsync(admin, count: 1, maxUses: 5))[0].Code;

        var revoke = await admin.DeleteAsync($"/admin/invites/{code}", Cancel);
        var again = await admin.DeleteAsync($"/admin/invites/{code}", Cancel);
        var unknown = await admin.DeleteAsync("/admin/invites/NOPE-NOPE", Cancel);

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode); // повтор ничего не ломает
        Assert.Equal((404, "invite_not_found"), await ProblemOf(unknown));
        Assert.Equal((403, "invite_invalid"), await ProblemOf(await SignUpAsync(api, code)));
        var revoked = (await ListAsync(admin)).Single(i => i.Code == code); // строка осталась — погашена, а не удалена
        Assert.True(revoked.ExpiresAtMs <= api.Time.GetUtcNow().ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Without_sign_in_or_required_fields_the_request_is_refused()
    {
        // Это проверяет уже контракт (вход и обязательные поля), а не реализацию, — поэтому без Skip.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (admin, _) = await api.CreatePlayerClientAsync(UserRole.Admin);
        using var anonymous = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await IssueAsync(anonymous, count: 1, maxUses: 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/admin/invites", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync("/admin/invites/ABCD-EFGH", Cancel)).StatusCode);
        var withoutMaxUses = await admin.PostAsJsonAsync("/admin/invites", new { count = 1 }, Json, Cancel);
        Assert.Equal(HttpStatusCode.BadRequest, withoutMaxUses.StatusCode);
    }

    // MARK: — вспомогательное

    private Task<HttpResponseMessage> IssueAsync(HttpClient client, int count, int maxUses, string? note = null) =>
        client.PostAsJsonAsync("/admin/invites", new CreateInvitesRequest { Count = count, MaxUses = maxUses, Note = note }, Json, Cancel);

    private async Task<List<InviteResponse>> IssuedAsync(HttpClient admin, int count, int maxUses)
    {
        var response = await IssueAsync(admin, count, maxUses);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<InviteResponse>>(Json, Cancel))!;
    }

    private async Task<List<InviteResponse>> ListAsync(HttpClient admin) =>
        (await admin.GetFromJsonAsync<List<InviteResponse>>("/admin/invites", Json, Cancel))!;

    /// <summary>Регистрация нового игрока по коду — как в <see cref="AuthFlowTests"/>: 16+ и согласие версии 1.</summary>
    private Task<HttpResponseMessage> SignUpAsync(ApiFactory api, string code) =>
        api.CreateClient().PostAsJsonAsync("/auth/google", new GoogleSignInRequest($"google:{Guid.NewGuid():N}", code, true, 1), Cancel);

    private async Task<int> InviteCountAsync()
    {
        await using var db = database.CreateContext();
        return await db.Invites.CountAsync(Cancel);
    }

    private async Task<(int Status, string? Code)> ProblemOf(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        return ((int)response.StatusCode, problem.TryGetProperty("code", out var code) ? code.GetString() : null);
    }
}
