using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Features.Territory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Визиты на настоящей базе (PLAN.md, §3.3): прогулка по своей земле освежает её и раз в 20 ч добавляет уровень;
/// первые и последние 200 м забега и меньше 50 м внутри куска — не визит.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class VisitsTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Walk_through_own_land_a_day_later_levels_it_up_once()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync(); // до сдвига часов: токен «из будущего» не прошёл бы проверку
        var area = NewArea();
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromHours(21));

        // 700 м по прямой через квадрат: первые и последние 200 м не в счёт, внутри остаётся 100 м.
        var run = await WalkAndFinishAsync(Cancel, api, anna, [(area.X - 300, area.Y + 50), (area.X + 400, area.Y + 50)]);

        // Визит меняет карту — публично, поэтому не раньше чем через 20 минут после конца забега (§3.16).
        Assert.DoesNotContain(run.Id, await ReadyAsync(api));
        Assert.Null(await VisitAsync(api, run.Id));
        api.Time.Advance(TerritoryReader.PublicDelay);
        Assert.Contains(run.Id, await ReadyAsync(api));
        Assert.Equal(1, await VisitAsync(api, run.Id));
        Assert.Null(await VisitAsync(api, run.Id)); // забег считается один раз
        Assert.DoesNotContain(run.Id, await ReadyAsync(api));
        var piece = Assert.Single(await LandAsync(annaId));
        Assert.Equal(2, piece.Level);
        Assert.Equal(piece.LastVisitAt, piece.LastLevelUpAt);

        // Визит — по часам сервера: последняя точка внутри квадрата — на 400-м метре прогулки, начатой 20 минут назад.
        var expected = DateTimeOffset.FromUnixTimeMilliseconds(run.StartedAtMs) + TimeSpan.FromSeconds(400 / 1.4);
        Assert.InRange(piece.LastVisitAt, expected - TimeSpan.FromSeconds(5), expected + TimeSpan.FromSeconds(5));

        await using var db = database.CreateContext();
        var stored = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == run.Id, Cancel);
        Assert.Equal(1, stored.VisitedParcels);
        Assert.InRange(stored.AcceptedMeters!.Value, 690, 710); // пробег — весь засчитанный путь, без обрезки 200 м
    }

    [Fact]
    public async Task Land_at_the_start_of_a_run_is_not_visited()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        var captured = Assert.Single(await LandAsync(annaId));
        api.Time.Advance(TimeSpan.FromHours(21));

        // Старт посреди своего квадрата — «у дома»: 50 м внутри приходятся на первые 200 м.
        var run = await WalkAndFinishAsync(Cancel, api, anna, [(area.X + 50, area.Y + 50), (area.X + 750, area.Y + 50)]);
        api.Time.Advance(TerritoryReader.PublicDelay);

        Assert.Equal(0, await VisitAsync(api, run.Id));
        var piece = Assert.Single(await LandAsync(annaId));
        Assert.Equal((1, captured.LastVisitAt), (piece.Level, piece.LastVisitAt));
    }

    [Fact]
    public async Task Cutting_a_corner_is_not_a_visit()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        var captured = Assert.Single(await LandAsync(annaId));
        api.Time.Advance(TimeSpan.FromHours(21));

        // Диагональ x + y = 25 срезает угол квадрата: внутри ~35 м, меньше 50.
        var run = await WalkAndFinishAsync(Cancel, api, anna, [(area.X - 325, area.Y + 350), (area.X + 375, area.Y - 350)]);
        api.Time.Advance(TerritoryReader.PublicDelay);

        Assert.Equal(0, await VisitAsync(api, run.Id));
        var piece = Assert.Single(await LandAsync(annaId));
        Assert.Equal((1, captured.LastVisitAt), (piece.Level, piece.LastVisitAt));
    }

    [Fact]
    public async Task Visit_waits_20_minutes_by_the_server_clock_when_the_phone_clock_is_behind()
    {
        // Часы телефона отстают на 30 минут: по ним забег закончился «полчаса назад» уже в момент завершения. По часам
        // телефона визит засчитался бы сразу — и вся лига увидела бы, где игрок только что пробежал (§3.16).
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromHours(21));

        var run = await WalkAndFinishWithSkewAsync(api, anna, [(area.X - 300, area.Y + 50), (area.X + 400, area.Y + 50)], TimeSpan.FromMinutes(-30));

        Assert.DoesNotContain(run.Id, await ReadyAsync(api));
        Assert.Null(await VisitAsync(api, run.Id));
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        Assert.Contains(run.Id, await ReadyAsync(api));
        Assert.Equal(1, await VisitAsync(api, run.Id));
    }

    [Fact]
    public async Task Visit_waits_for_the_same_5_minute_boundary_as_the_public_map()
    {
        // Всё, что сообщает о чужих изменениях, раскрывается на одной границе — «сейчас − 20 минут» вниз до 5 минут
        // (TerritoryReader.PublicHorizon). Ровно через 20 минут версия тайла выдала бы минуту конца забега.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromHours(21));
        var run = await WalkAndFinishAsync(Cancel, api, anna, [(area.X - 300, area.Y + 50), (area.X + 400, area.Y + 50)]);
        DateTimeOffset endedAt;
        await using (var db = database.CreateContext())
        {
            endedAt = (await db.Runs.AsNoTracking().SingleAsync(r => r.Id == run.Id, Cancel)).EndedAt!.Value;
        }

        // Первая граница 5 минут не раньше конца забега: до неё + 20 минут конец ещё не публичен.
        var step = TerritoryReader.RevealStep.Ticks;
        var boundary = new DateTimeOffset((endedAt.UtcTicks + step - 1) / step * step, TimeSpan.Zero);
        api.Time.Advance(boundary + TerritoryReader.PublicDelay - TimeSpan.FromMilliseconds(1) - api.Time.GetUtcNow());

        Assert.DoesNotContain(run.Id, await ReadyAsync(api));
        Assert.Null(await VisitAsync(api, run.Id));
        api.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Contains(run.Id, await ReadyAsync(api));
        Assert.Equal(1, await VisitAsync(api, run.Id));
    }

    [Fact]
    public async Task Visits_wait_exactly_until_a_hidden_capture_of_own_land_is_public_and_this_is_not_a_failure()
    {
        // Борис взял правую часть квадрата Анны (L1 — переход), пока её визиты ещё не засчитаны. Засчитай их сразу — остаток
        // квадрата получил бы визит, а взятая часть в публичной проекции вернулась бы Анне без него: шов по линии скрытой
        // петли (territory-map.md). Визиты ждут раскрытия захвата: в его журнале (до захвата) земля Анны. Это не ошибка:
        // счётчик ошибок не растёт, а забег возвращается в очередь ровно тогда, когда захват становится публичным.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        var captured = Assert.Single(await LandAsync(annaId));
        api.Time.Advance(TimeSpan.FromHours(21));

        // 700 м по прямой: засчитан путь x −100…200, по остатку квадрата (x 0…60) — 60 м, визит.
        var run = await WalkAndFinishAsync(Cancel, api, anna, [(area.X - 300, area.Y + 50), (area.X + 400, area.Y + 50)]);
        var claim = await WalkAndClaimAsync(Cancel, api, boris, Rectangle(area, 60, -10, 100, 120));
        Assert.Equal(1, await Walks.ProcessAsync(api, claim.RunId));
        api.Time.Advance(TerritoryReader.PublicDelay); // конец забега Анны уже публичен, захват Бориса — ещё нет

        Assert.Null(await VisitAsync(api, run.Id));
        DateTimeOffset publicAt;
        await using (var db = database.CreateContext())
        {
            var appliedAt = await db.CaptureJournal.AsNoTracking().Where(j => j.CaptureId == claim.CaptureId).MaxAsync(j => j.AppliedAt, Cancel);
            Assert.True(appliedAt > TerritoryReader.PublicHorizon(api.Time.GetUtcNow(), TerritoryReader.PublicDelay));
            publicAt = TerritoryReader.PublicAt(appliedAt, TerritoryReader.PublicDelay);
            var stored = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == run.Id, Cancel);
            Assert.Equal(0, stored.VisitsFailures);
            Assert.Equal(publicAt, stored.VisitsRetryAt);
            Assert.Null(stored.VisitsProcessedAt);
        }

        Assert.DoesNotContain(run.Id, await ReadyAsync(api));
        Assert.Equal(captured.LastVisitAt, Assert.Single(await LandAsync(annaId)).LastVisitAt); // остаток пока без визита
        api.Time.Advance(publicAt - TimeSpan.FromMilliseconds(1) - api.Time.GetUtcNow());
        Assert.DoesNotContain(run.Id, await ReadyAsync(api));
        Assert.Null(await VisitAsync(api, run.Id)); // за миллисекунду до раскрытия захват ещё скрыт
        api.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Contains(run.Id, await ReadyAsync(api));
        Assert.Equal(1, await VisitAsync(api, run.Id));
        var rest = Assert.Single(await LandAsync(annaId));
        Assert.True(rest.LastVisitAt > captured.LastVisitAt);
        Assert.Equal(2, rest.Level); // визит лёг на публичный остаток: с повышения больше 20 ч
    }

    [Fact]
    public async Task Hidden_capture_that_does_not_touch_own_land_does_not_delay_visits()
    {
        // Скрытый захват в тех же тайлах и на пути Анны, но не по её земле — в его журнале её земли нет, шва он не даст.
        // Визиты засчитываются, как только публичен конец забега, без лишней задержки.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromHours(21));

        var run = await WalkAndFinishAsync(Cancel, api, anna, [(area.X - 300, area.Y + 50), (area.X + 400, area.Y + 50)]);
        var claim = await WalkAndClaimAsync(Cancel, api, boris, Square(area, 150, 0, 100)); // ничья земля в 50 м от квадрата
        Assert.Equal(1, await Walks.ProcessAsync(api, claim.RunId));
        api.Time.Advance(TerritoryReader.PublicDelay);
        await using (var db = database.CreateContext())
        {
            var journal = await db.CaptureJournal.AsNoTracking().Where(j => j.CaptureId == claim.CaptureId).ToListAsync(Cancel);
            Assert.NotEmpty(journal);
            var horizon = TerritoryReader.PublicHorizon(api.Time.GetUtcNow(), TerritoryReader.PublicDelay);
            Assert.All(journal, j => Assert.True(j.AppliedAt > horizon)); // захват ещё скрыт
        }

        Assert.Contains(run.Id, await ReadyAsync(api));
        Assert.Equal(1, await VisitAsync(api, run.Id));
        Assert.Equal(2, Assert.Single(await LandAsync(annaId)).Level);
    }

    /// <summary>Прогулка с завершением, когда часы телефона сбиты на <paramref name="skew"/>: всё время в запросах — по ним.</summary>
    private async Task<StartRunRequest> WalkAndFinishWithSkewAsync(
        ApiFactory api, HttpClient client, IReadOnlyList<(double X, double Y)> vertices, TimeSpan skew)
    {
        var skewMs = (long)skew.TotalMilliseconds;
        var start = NewStart(api, startedAgo: TimeSpan.FromMinutes(20), clockSkewMs: skewMs);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/runs", start, Json, Cancel)).StatusCode);
        var points = WalkPoints(start, vertices);
        foreach (var chunk in WalkChunks(api, points))
        {
            var put = await client.PutAsJsonAsync(
                $"/runs/{start.Id}/chunks/{chunk.Points![0].Seq}", chunk with { SentAtMs = chunk.SentAtMs + skewMs }, Json, Cancel);
            Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        }

        var finish = await client.PostAsJsonAsync(
            $"/runs/{start.Id}/finish",
            new FinishRunRequest(points[^1].T, points.Count - 1, api.Time.GetUtcNow().ToUnixTimeMilliseconds() + skewMs),
            Json,
            Cancel);
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
        return start;
    }

    private static async Task<List<Guid>> ReadyAsync(ApiFactory api)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<VisitProcessor>().RunsReadyAsync(1_000, CancellationToken.None);
    }

    private static async Task<int?> VisitAsync(ApiFactory api, Guid runId)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<VisitProcessor>().ProcessRunAsync(runId, CancellationToken.None);
    }

    private async Task<List<Gorodki.Api.Infrastructure.Persistence.ParcelEntity>> LandAsync(Guid userId)
    {
        await using var db = database.CreateContext();
        return await db.Parcels.AsNoTracking().Where(p => p.OwnerId == userId).ToListAsync(Cancel);
    }
}
