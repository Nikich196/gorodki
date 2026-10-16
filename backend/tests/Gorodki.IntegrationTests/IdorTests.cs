using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Admin;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Clans;
using Gorodki.Api.Features.Duels;
using Gorodki.Api.Features.Me;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Features.Social;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Clans;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// IDOR — чужой ресурс по угаданному идентификатору (PLAN.md, §7.3 «Безопасность», §13). Анна создаёт забег с заявкой,
/// приватную зону и задание отката; Борис, обычный игрок, пробует каждый адрес с идентификатором в пути на её ресурсах.
/// Ответ — «нет такого» (404) или «только для администратора» (403), а данные Анны не меняются.
/// </summary>
/// <remarks>
/// Список адресов с параметрами берётся у самого сервера (<see cref="EndpointDataSource"/>): новый адрес с идентификатором
/// без случая здесь роняет <see cref="Every_route_with_an_identifier_is_checked"/>. Адрес задачи Егора, у которого пока
/// заглушка, ждёт в <see cref="AwaitingTasks"/>.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class IdorTests(DatabaseFixture database)
{
    /// <summary>Адреса с параметрами в пути и что Борис получает на ресурсах Анны.</summary>
    private static readonly Dictionary<string, HttpStatusCode> Expected = new()
    {
        ["GET /runs/{runId:guid}"] = HttpStatusCode.NotFound,
        ["PUT /runs/{runId:guid}/chunks/{firstSeq:int}"] = HttpStatusCode.NotFound,
        ["POST /runs/{runId:guid}/finish"] = HttpStatusCode.NotFound,
        ["POST /runs/{runId:guid}/loops"] = HttpStatusCode.NotFound,
        ["GET /runs/{runId:guid}/captures"] = HttpStatusCode.NotFound,
        ["DELETE /me/privacy-zones/{id:guid}"] = HttpStatusCode.NotFound,
        ["POST /admin/users/{userId:guid}/rollback"] = HttpStatusCode.Forbidden,
        ["GET /admin/rollbacks/{rollbackId:guid}"] = HttpStatusCode.Forbidden,
        ["DELETE /admin/invites/{code}"] = HttpStatusCode.Forbidden,
        // Карточка игрока публична по замыслу: номер владельца и так есть у каждого куска на карте (GET /territory).
        ["GET /players/{id:guid}"] = HttpStatusCode.OK,

        // Заготовки задач Егора (C15). Карточка клана публична, как карточка игрока (участники — по номерам, ники — с согласия);
        // код-приглашение Борису не виден. Остальное — «нет такого»: Анна не в клане Бориса, фишки, заявки в друзья (заявку
        // Карла Анне не принять, дружбу Анны не разорвать), посты (ещё до границы публичности) и дуэли Анны (вызов Дине Борису
        // не принять и не отклонить) — не его; отрезок —
        // несуществующий номер. Блокировка — право Бориса: 204, данные Анны не меняются. Бан — только администратору.
        ["GET /clans/{id:guid}"] = HttpStatusCode.OK,
        ["DELETE /clans/mine/members/{userId:guid}"] = HttpStatusCode.NotFound,
        ["PUT /clans/mine/members/{userId:guid}/role"] = HttpStatusCode.NotFound,
        ["POST /me/inventory/{id:guid}/activate"] = HttpStatusCode.NotFound,
        ["POST /friends/{id:guid}/accept"] = HttpStatusCode.NotFound,
        ["DELETE /friends/{id:guid}"] = HttpStatusCode.NotFound,
        ["POST /feed/{id:guid}/respect"] = HttpStatusCode.NotFound,
        ["POST /feed/{id:guid}/report"] = HttpStatusCode.NotFound,
        ["PUT /me/blocks/{id:guid}"] = HttpStatusCode.NoContent,
        ["DELETE /me/blocks/{id:guid}"] = HttpStatusCode.NoContent,
        ["GET /segments/{id:guid}"] = HttpStatusCode.NotFound,
        ["GET /segments/{id:guid}/leaderboard"] = HttpStatusCode.NotFound,
        ["GET /duels/{id:guid}"] = HttpStatusCode.NotFound,
        ["POST /duels/{id:guid}/accept"] = HttpStatusCode.NotFound,
        ["POST /duels/{id:guid}/decline"] = HttpStatusCode.NotFound,
        ["POST /admin/users/{userId:guid}/ban"] = HttpStatusCode.Forbidden,
    };

    /// <summary>
    /// Адреса задач Егора, у которых пока заглушка: запроса Бориса к ним ещё нет. Сделал задачу — убери её строку отсюда,
    /// и <see cref="Someone_elses_resources_cannot_be_read_or_changed"/> попросит добавить запрос Бориса к адресу.
    /// </summary>
    private static readonly Dictionary<string, string> AwaitingTasks = new()
    {
        ["DELETE /admin/invites/{code}"] = "ЗАДАЧА #114",
        ["POST /me/inventory/{id:guid}/activate"] = "ЗАДАЧА #140",
        ["POST /admin/users/{userId:guid}/ban"] = "ЗАДАЧА #142",
    };

    /// <summary>Ответ называет номер Анны по замыслу: карточки игрока и клана. Ника без её согласия в ответе всё равно нет.</summary>
    private static readonly string[] ShowsOwnerId = ["GET /players/{id:guid}", "GET /clans/{id:guid}"];

    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_route_with_an_identifier_is_checked()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        _ = api.CreateClient(); // сервер собирается при первом клиенте

        var routes = api.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.Parameters.Count > 0)
            // Описание API и Scalar — только в разработке, данных игроков в них нет.
            .Where(e => e.RoutePattern.RawText is { } raw && !raw.StartsWith("/openapi", StringComparison.Ordinal)
                && !raw.StartsWith("/scalar", StringComparison.Ordinal))
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => $"{method} {e.RoutePattern.RawText}"))
            .Distinct()
            .Order()
            .ToList();

        Assert.Equal(Expected.Keys.Order(), routes);
    }

    [Fact]
    public async Task Someone_elses_resources_cannot_be_read_or_changed()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        var (admin, _) = await api.CreatePlayerClientAsync(UserRole.Admin);
        var (carl, carlId) = await api.CreatePlayerClientAsync();
        var (dina, _) = await api.CreatePlayerClientAsync();

        // Ресурсы Анны: забег с кусками и заявкой (без завершения), приватная зона, задание отката (заводит админ).
        var claim = await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100));
        var zone = await anna.PostAsJsonAsync("/me/privacy-zones", new PrivacyZoneRequest(52.09, 23.7), Json, Cancel);
        Assert.Equal(HttpStatusCode.Created, zone.StatusCode);
        var zoneId = (await zone.Content.ReadFromJsonAsync<PrivacyZoneResponse>(Json, Cancel))!.Id;
        var rollback = await admin.PostAsJsonAsync($"/admin/users/{annaId}/rollback", new RollbackRequest("проверка IDOR"), Json, Cancel);
        Assert.True(rollback.IsSuccessStatusCode, $"Откат не заведён: {rollback.StatusCode}");
        var rollbackId = (await rollback.Content.ReadFromJsonAsync<RollbackResponse>(Json, Cancel))!.Id;
        // Клан Анны (Борис — в своём): карточку видно всем, а участников из чужого клана не исключить и не повысить.
        var annaClan = await anna.PostAsJsonAsync("/clans", new CreateClanRequest { Name = $"IDOR {Guid.NewGuid().ToString("N")[..8]}" }, Json, Cancel);
        Assert.Equal(HttpStatusCode.Created, annaClan.StatusCode);
        var annaClanId = (await annaClan.Content.ReadFromJsonAsync<ClanResponse>(Json, Cancel))!.Id;
        var borisClan = await boris.PostAsJsonAsync("/clans", new CreateClanRequest { Name = $"IDOR {Guid.NewGuid().ToString("N")[..8]}" }, Json, Cancel);
        Assert.Equal(HttpStatusCode.Created, borisClan.StatusCode);
        // Пост Анны ещё до границы публичности (его захват скрыт): Борису его нет — ни для респекта, ни для жалобы.
        var postId = Guid.NewGuid();
        await using (var db = database.CreateContext())
        {
            db.FeedPosts.Add(new FeedPostEntity
            {
                Id = postId,
                AuthorId = annaId,
                Kind = FeedPostKind.Capture,
                League = Gorodki.Domain.Leagues.League.Run,
                GameDay = Gorodki.Domain.Time.GameClock.GameDayOf(api.Time.GetUtcNow()),
                CapturedSquareMeters = 10_000,
                VisibleAt = api.Time.GetUtcNow().AddMinutes(25),
            });
            await db.SaveChangesAsync(Cancel);
        }

        // Друзья Анны: Карл прислал ей заявку, с Диной они друзья. Борис не примет заявку Карла и не разорвёт дружбу Анны.
        var annaCode = (await anna.GetFromJsonAsync<FriendsResponse>("/friends", Json, Cancel))!.MyCode;
        var dinaCode = (await dina.GetFromJsonAsync<FriendsResponse>("/friends", Json, Cancel))!.MyCode;
        foreach (var (client, code) in new[] { (carl, annaCode), (dina, annaCode), (anna, dinaCode) })
        {
            var added = await client.PostAsJsonAsync("/friends", new AddFriendRequest { Code = code }, Json, Cancel);
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        }

        // Дуэль Анны: она вызвала Дину. Борис её не видит, не примет и не отклонит.
        var duel = await anna.PostAsJsonAsync(
            "/duels",
            new CreateDuelRequest { OpponentId = await IdOfAsync(dina), League = Gorodki.Domain.Leagues.League.Run, Metric = DuelMetric.Distance, Days = 3 },
            Json,
            Cancel);
        Assert.Equal(HttpStatusCode.Created, duel.StatusCode);
        var duelId = (await duel.Content.ReadFromJsonAsync<DuelResponse>(Json, Cancel))!.Id;

        var before = await SnapshotAsync(claim.RunId, annaId);

        var now = api.Time.GetUtcNow().ToUnixTimeMilliseconds();
        var start = NewStart(api) with { Id = claim.RunId };
        var answers = new Dictionary<string, HttpResponseMessage>
        {
            ["GET /runs/{runId:guid}"] = await boris.GetAsync($"/runs/{claim.RunId}", Cancel),
            ["PUT /runs/{runId:guid}/chunks/{firstSeq:int}"] = await boris.PutAsJsonAsync(
                $"/runs/{claim.RunId}/chunks/100000", ChunkRequest(api, start, firstSeq: 100_000, count: 5), Json, Cancel),
            ["POST /runs/{runId:guid}/finish"] = await boris.PostAsJsonAsync(
                $"/runs/{claim.RunId}/finish", new FinishRunRequest(now, 5, now), Json, Cancel),
            ["POST /runs/{runId:guid}/loops"] = await boris.PostAsJsonAsync(
                $"/runs/{claim.RunId}/loops", new LoopClaimRequest(1, 0, 10, LoopClosure.Proximity, 10_000, now), Json, Cancel),
            ["GET /runs/{runId:guid}/captures"] = await boris.GetAsync($"/runs/{claim.RunId}/captures", Cancel),
            ["DELETE /me/privacy-zones/{id:guid}"] = await boris.DeleteAsync($"/me/privacy-zones/{zoneId}", Cancel),
            ["POST /admin/users/{userId:guid}/rollback"] = await boris.PostAsJsonAsync(
                $"/admin/users/{annaId}/rollback", new RollbackRequest("чужими руками"), Json, Cancel),
            ["GET /admin/rollbacks/{rollbackId:guid}"] = await boris.GetAsync($"/admin/rollbacks/{rollbackId}", Cancel),
            ["GET /players/{id:guid}"] = await boris.GetAsync($"/players/{annaId}", Cancel),
            ["GET /clans/{id:guid}"] = await boris.GetAsync($"/clans/{annaClanId}", Cancel),
            ["DELETE /clans/mine/members/{userId:guid}"] = await boris.DeleteAsync($"/clans/mine/members/{annaId}", Cancel),
            ["PUT /clans/mine/members/{userId:guid}/role"] = await boris.PutAsJsonAsync(
                $"/clans/mine/members/{annaId}/role", new ClanRoleRequest { Role = ClanRole.Member }, Json, Cancel),
            ["POST /friends/{id:guid}/accept"] = await boris.PostAsync($"/friends/{carlId}/accept", null, Cancel),
            ["DELETE /friends/{id:guid}"] = await boris.DeleteAsync($"/friends/{annaId}", Cancel),
            ["POST /feed/{id:guid}/respect"] = await boris.PostAsync($"/feed/{postId}/respect", null, Cancel),
            ["POST /feed/{id:guid}/report"] = await boris.PostAsJsonAsync($"/feed/{postId}/report", new ReportPostRequest("чужое"), Json, Cancel),
            ["PUT /me/blocks/{id:guid}"] = await boris.PutAsync($"/me/blocks/{annaId}", null, Cancel),
            ["DELETE /me/blocks/{id:guid}"] = await boris.DeleteAsync($"/me/blocks/{annaId}", Cancel),
            ["GET /duels/{id:guid}"] = await boris.GetAsync($"/duels/{duelId}", Cancel),
            ["POST /duels/{id:guid}/accept"] = await boris.PostAsJsonAsync($"/duels/{duelId}/accept", new AcceptDuelRequest(), Json, Cancel),
            ["POST /duels/{id:guid}/decline"] = await boris.PostAsync($"/duels/{duelId}/decline", null, Cancel),
            // Отрезки общие, своих у Анны нет: номер её забега вместо номера отрезка — «нет такого».
            ["GET /segments/{id:guid}"] = await boris.GetAsync($"/segments/{claim.RunId}", Cancel),
            ["GET /segments/{id:guid}/leaderboard"] = await boris.GetAsync($"/segments/{claim.RunId}/leaderboard", Cancel),
        };

        // Здесь нужен запрос Бориса к каждому адресу, кроме ждущих задачи Егора (AwaitingTasks).
        Assert.Equal(Expected.Keys.Except(AwaitingTasks.Keys).Order(), answers.Keys.Order());
        var annaName = await DisplayNameAsync(annaId);
        foreach (var (route, response) in answers)
        {
            Assert.True(Expected[route] == response.StatusCode, $"{route}: {response.StatusCode}, а нужно {Expected[route]}");
            var body = await response.Content.ReadAsStringAsync(Cancel);
            if (!ShowsOwnerId.Contains(route))
            {
                Assert.DoesNotContain(annaId.ToString(), body, StringComparison.OrdinalIgnoreCase); // ответ ничего не выдаёт
            }

            Assert.DoesNotContain(annaName, body, StringComparison.OrdinalIgnoreCase); // и ника Анны (согласия нет) — тоже
        }

        // Тот же идентификатор забега в новом старте — конфликт, а не чужой забег.
        var hijack = await boris.PostAsJsonAsync("/runs", start, Json, Cancel);
        Assert.Equal(HttpStatusCode.Conflict, hijack.StatusCode);

        Assert.Equal(before, await SnapshotAsync(claim.RunId, annaId));
        var zones = await anna.GetFromJsonAsync<List<PrivacyZoneResponse>>("/me/privacy-zones", Json, Cancel);
        Assert.Contains(zones!, z => z.Id == zoneId);
    }

    private async Task<Guid> IdOfAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<MeResponse>("/me", Json, Cancel))!.Id;

    private async Task<string> DisplayNameAsync(Guid userId)
    {
        await using var db = database.CreateContext();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).SingleAsync(Cancel);
    }

    /// <summary>
    /// Всё, что Борис мог бы испортить у Анны: забег, куски, заявки, задания отката, клан и роль в нём, лента, друзья, дуэли.
    /// </summary>
    private async Task<string> SnapshotAsync(Guid runId, Guid annaId)
    {
        await using var db = database.CreateContext();
        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == runId, Cancel);
        var chunks = await db.RunChunks.CountAsync(c => c.RunId == runId, Cancel);
        var captures = await db.Captures.Where(c => c.RunId == runId).Select(c => c.ClaimNo).OrderBy(n => n).ToListAsync(Cancel);
        var rollbacks = await db.CaptureRollbacks.CountAsync(r => r.UserId == annaId, Cancel);
        var clan = await db.ClanMembers.Where(m => m.UserId == annaId).Select(m => new { m.ClanId, m.Role }).SingleOrDefaultAsync(Cancel);
        var feed = await db.FeedPosts.Where(p => p.AuthorId == annaId)
            .Select(p => new { p.Id, Respects = db.FeedRespects.Count(r => r.PostId == p.Id), Reports = db.FeedReports.Count(r => r.PostId == p.Id) })
            .ToListAsync(Cancel);
        var friends = await db.FriendLinks.Where(f => f.UserId == annaId || f.FriendId == annaId)
            .OrderBy(f => f.UserId).ThenBy(f => f.FriendId).Select(f => $"{f.UserId}>{f.FriendId}").ToListAsync(Cancel);
        Assert.Equal(3, friends.Count); // заявка Карла и дружба с Диной (две строки) на месте
        var duels = await db.Duels.Where(d => d.ChallengerId == annaId || d.OpponentId == annaId)
            .Select(d => new { d.Id, d.Status, d.OpponentId, d.StartsAt, d.OpponentStakeItemId })
            .ToListAsync(Cancel);
        Assert.Single(duels); // вызов Дине ждёт ответа
        return $"{run.UserId} {run.Status} {run.EndedAt} {chunks} [{string.Join(",", captures)}] {rollbacks} {clan} "
            + $"{string.Join(",", feed)} {string.Join(",", friends)} {string.Join(",", duels)}";
    }
}
