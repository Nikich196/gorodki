using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Admin;
using Gorodki.Api.Infrastructure.Persistence;
using static Gorodki.IntegrationTests.RunRequests;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Оценка доверия и бан — <c>GET /admin/runs/suspicious</c>, <c>POST /admin/users/{id}/ban</c> (PLAN.md, §3.9). Задача
/// #TBD-E13 для Егора: тесты со <c>Skip</c> снимаются вместе с реализацией. Признаки забега — <c>TrustSignalsTests</c> без
/// Docker (одинаковая точность, идеальные интервалы, нет шагов, &gt;60 км в день пешком).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AdminTrustTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(Skip = "ЗАДАЧА #TBD-E13")]
    public async Task Only_an_admin_sees_suspicious_runs_and_bans()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (admin, _) = await api.CreatePlayerClientAsync(UserRole.Admin);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (_, borisId) = await api.CreatePlayerClientAsync();

        Assert.Equal((403, "admin_only"), await Problems.OfAsync(await anna.GetAsync("/admin/runs/suspicious", Cancel), Cancel));
        Assert.Equal((403, "admin_only"), await Problems.OfAsync(
            await anna.PostAsJsonAsync($"/admin/users/{borisId}/ban", new BanRequest("чужими руками"), Json, Cancel), Cancel));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/admin/runs/suspicious", Cancel)).StatusCode);
    }

    [Fact(Skip = "ЗАДАЧА #TBD-E13")]
    public async Task Ban_needs_a_reason_and_closes_the_game_for_the_player()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (admin, _) = await api.CreatePlayerClientAsync(UserRole.Admin);
        var (boris, borisId) = await api.CreatePlayerClientAsync();

        Assert.Equal((400, "reason_required"), await Problems.OfAsync(
            await admin.PostAsJsonAsync($"/admin/users/{borisId}/ban", new BanRequest("  "), Json, Cancel), Cancel));
        Assert.Equal((404, "user_not_found"), await Problems.OfAsync(
            await admin.PostAsJsonAsync($"/admin/users/{Guid.NewGuid()}/ban", new BanRequest("проверка"), Json, Cancel), Cancel));

        var ban = await admin.PostAsJsonAsync($"/admin/users/{borisId}/ban", new BanRequest("машина вместо бега"), Json, Cancel);

        Assert.Equal(HttpStatusCode.OK, ban.StatusCode);
        Assert.Equal(borisId, (await Problems.BodyAsync<BanResponse>(ban, Cancel)).UserId);
        var start = await boris.PostAsJsonAsync("/runs", NewStart(api), Json, Cancel);
        Assert.Equal((403, "account_banned"), await Problems.OfAsync(start, Cancel)); // забеги больше не принимаются
    }
}
