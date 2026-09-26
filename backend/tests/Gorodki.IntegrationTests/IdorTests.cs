using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Admin;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Me;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Infrastructure.Persistence;
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
        // код-приглашение Борису не виден. Остальное — «нет такого»: Анна не в клане Бориса, фишки, заявки, посты (ещё до
        // границы публичности) и дуэли Анны — не его; отрезок — несуществующий номер. Блокировка — право Бориса: 204, данные
        // Анны не меняются. Бан — только администратору.
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
        ["GET /players/{id:guid}"] = "ЗАДАЧА #115",
        ["GET /clans/{id:guid}"] = "ЗАДАЧА #135",
        ["DELETE /clans/mine/members/{userId:guid}"] = "ЗАДАЧА #135",
        ["PUT /clans/mine/members/{userId:guid}/role"] = "ЗАДАЧА #135",
        ["POST /me/inventory/{id:guid}/activate"] = "ЗАДАЧА #140",
        ["POST /friends/{id:guid}/accept"] = "ЗАДАЧА #143",
        ["DELETE /friends/{id:guid}"] = "ЗАДАЧА #143",
        ["POST /feed/{id:guid}/respect"] = "ЗАДАЧА #144",
        ["POST /feed/{id:guid}/report"] = "ЗАДАЧА #144",
        ["PUT /me/blocks/{id:guid}"] = "ЗАДАЧА #144",
        ["DELETE /me/blocks/{id:guid}"] = "ЗАДАЧА #144",
        ["GET /segments/{id:guid}"] = "ЗАДАЧА #146",
        ["GET /segments/{id:guid}/leaderboard"] = "ЗАДАЧА #146",
        ["GET /duels/{id:guid}"] = "ЗАДАЧА #147",
        ["POST /duels/{id:guid}/accept"] = "ЗАДАЧА #147",
        ["POST /duels/{id:guid}/decline"] = "ЗАДАЧА #147",
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

        // Ресурсы Анны: забег с кусками и заявкой (без завершения), приватная зона, задание отката (заводит админ).
        var claim = await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100));
        var zone = await anna.PostAsJsonAsync("/me/privacy-zones", new PrivacyZoneRequest(52.09, 23.7), Json, Cancel);
        Assert.Equal(HttpStatusCode.Created, zone.StatusCode);
        var zoneId = (await zone.Content.ReadFromJsonAsync<PrivacyZoneResponse>(Json, Cancel))!.Id;
        var rollback = await admin.PostAsJsonAsync($"/admin/users/{annaId}/rollback", new RollbackRequest("проверка IDOR"), Json, Cancel);
        Assert.True(rollback.IsSuccessStatusCode, $"Откат не заведён: {rollback.StatusCode}");
        var rollbackId = (await rollback.Content.ReadFromJsonAsync<RollbackResponse>(Json, Cancel))!.Id;
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

    private async Task<string> DisplayNameAsync(Guid userId)
    {
        await using var db = database.CreateContext();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).SingleAsync(Cancel);
    }

    /// <summary>Всё, что Борис мог бы испортить у Анны: забег, куски, заявки, задания отката.</summary>
    private async Task<string> SnapshotAsync(Guid runId, Guid annaId)
    {
        await using var db = database.CreateContext();
        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == runId, Cancel);
        var chunks = await db.RunChunks.CountAsync(c => c.RunId == runId, Cancel);
        var captures = await db.Captures.Where(c => c.RunId == runId).Select(c => c.ClaimNo).OrderBy(n => n).ToListAsync(Cancel);
        var rollbacks = await db.CaptureRollbacks.CountAsync(r => r.UserId == annaId, Cancel);
        return $"{run.UserId} {run.Status} {run.EndedAt} {chunks} [{string.Join(",", captures)}] {rollbacks}";
    }
}
