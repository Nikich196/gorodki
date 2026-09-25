using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class TerritoryTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Captured_block_appears_on_the_map_and_known_tiles_are_not_sent_again()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, userId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var claim = await WalkAndClaimAsync(Cancel, api, client, Square(area, 0, 0, 100));
        await ProcessAsync(api, claim.RunId);
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);

        var first = await client.GetFromJsonAsync<TerritoryResponse>($"/territory?league=run&tiles={tile.X}:{tile.Y}", Json, Cancel);
        var version = first!.Tiles.Single().Version;
        var again = await client.GetFromJsonAsync<TerritoryResponse>(
            $"/territory?league=run&tiles={tile.X}:{tile.Y}@{version}", Json, Cancel);

        var parcel = Assert.Single(first.Tiles.Single().Parcels);
        Assert.Equal((userId, (short)1), (parcel.OwnerId, parcel.Level));
        Assert.True(version >= 1);
        Assert.InRange(AreaOf(parcel.Exterior), 9_500, 10_500);
        Assert.Empty(again!.Tiles);
        Assert.Equal(new TileRef(tile.X, tile.Y), Assert.Single(again.Unchanged));
    }

    [Fact]
    public async Task Land_decays_to_a_ghost_and_then_disappears_from_the_map()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, userId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var claim = await WalkAndClaimAsync(Cancel, api, client, Square(area, 0, 0, 100));
        await ProcessAsync(api, claim.RunId);
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);

        // Чтение — напрямую через сервис: после сдвига часов на неделю токен для HTTP мог бы истечь.
        api.Time.Advance(TimeSpan.FromDays(7));
        var ghost = (await ReadAsync(api, tile)).Parcels.Single();
        api.Time.Advance(TimeSpan.FromDays(3));
        var gone = (await ReadAsync(api, tile)).Parcels;

        Assert.Equal((userId, (short)0, true), (ghost.OwnerId, ghost.Level, ghost.Ghost));
        Assert.Empty(gone);
    }

    [Fact]
    public async Task Others_see_a_capture_only_after_the_public_delay_and_nothing_gives_it_away_before()
    {
        // PLAN.md, §3.16: чужие видят изменения через 20 минут — иначе карта показывала бы, где человек прямо сейчас.
        // Скрытый захват не выдаёт себя и версией тайла: с прежней версией тайл «без изменений».
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        var (demo, _) = await api.CreatePlayerClientAsync(Gorodki.Api.Infrastructure.Persistence.UserRole.Demo);
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        var before = await TileAsync(boris, tile);
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);

        var own = await TileAsync(anna, tile);
        var hidden = await TileAsync(boris, tile);
        var sameVersion = await IsUnchangedAsync(boris, tile, before.Version);
        var demoViewer = await TileAsync(demo, tile);

        Assert.Equal(annaId, Assert.Single(own.Parcels).OwnerId); // свой захват — сразу
        Assert.True(own.Version > before.Version);
        Assert.Equal(before.Version, hidden.Version); // видимая версия не сдвинулась
        Assert.Empty(hidden.Parcels);
        Assert.True(sameVersion); // с прежней версией — «без изменений»
        Assert.Empty(demoViewer.Parcels); // демо-зритель видит карту как все

        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        var revealed = await TileAsync(boris, tile, known: before.Version);

        Assert.True(revealed.Version > before.Version);
        var parcel = Assert.Single(revealed.Parcels);
        Assert.Equal(annaId, parcel.OwnerId);
        Assert.Equal(0, parcel.LastVisitAtMs % 3_600_000); // время чужого визита — с точностью до часа
    }

    [Fact]
    public async Task Demo_account_captures_are_public_at_once()
    {
        // PLAN.md, §11, показ: «захват точно по контуру, /live обновился» — изменения демо-аккаунта без задержки.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (demo, demoId) = await api.CreatePlayerClientAsync(Gorodki.Api.Infrastructure.Persistence.UserRole.Demo);
        var (boris, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, demo, Square(area, 0, 0, 100))).RunId);

        var seen = await TileAsync(boris, TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50));

        Assert.Equal(demoId, Assert.Single(seen.Parcels).OwnerId);
    }

    [Fact]
    public async Task While_hidden_the_land_looks_as_it_was_before_the_capture()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (vera, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep); // захват Анны уже публичен
        var beforeBoris = await TileAsync(vera, tile);
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100))).RunId);

        var seenByVera = await TileAsync(vera, tile);
        var veraUnchanged = await IsUnchangedAsync(vera, tile, beforeBoris.Version);
        var seenByBoris = await TileAsync(boris, tile);
        var seenByAnna = await TileAsync(anna, tile);

        // Вера и сама Анна видят квадрат Анны целым — захват Бориса ещё скрыт и версию не сдвигает.
        var annaLand = Assert.Single(seenByVera.Parcels);
        Assert.Equal(annaId, annaLand.OwnerId);
        Assert.InRange(AreaOf(annaLand.Exterior), 9_500, 10_500);
        Assert.Equal(beforeBoris.Version, seenByVera.Version);
        Assert.True(veraUnchanged);
        Assert.True(annaLand.Id > 0); // номер собранного проекцией куска — такой же, как у настоящего
        Assert.InRange(AreaOf(Assert.Single(seenByAnna.Parcels).Exterior), 9_500, 10_500);
        // Борис свой захват видит сразу: половина Анны — его, щит — до миллисекунды.
        var borisOwn = seenByBoris.Parcels.Where(p => p.OwnerId == borisId).ToList();
        Assert.InRange(borisOwn.Sum(p => AreaOf(p.Exterior)), 9_500, 10_500);

        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        var later = await TileAsync(vera, tile, known: beforeBoris.Version);

        Assert.True(later.Version > beforeBoris.Version);
        var borisLand = later.Parcels.Where(p => p.OwnerId == borisId).ToList();
        Assert.InRange(borisLand.Sum(p => AreaOf(p.Exterior)), 9_500, 10_500);
        Assert.InRange(later.Parcels.Where(p => p.OwnerId == annaId).Sum(p => AreaOf(p.Exterior)), 4_700, 5_300);
        // Чужой щит (12 ч после захвата) — с точностью до 10 минут: минута захвата не видна и после раскрытия.
        Assert.Contains(borisLand, p => p.ShieldUntilMs is not null);
        Assert.All(borisLand.Where(p => p.ShieldUntilMs is not null), p => Assert.Equal(0, p.ShieldUntilMs!.Value % 600_000));
    }

    [Fact]
    public async Task Capture_that_changes_no_land_leaves_the_map_as_it_was_for_everyone()
    {
        // Аудит BE-01: новичок обвёл землю Анны и Веры поперёк их границ и ничего не взял (§3.3). Раньше куски всё равно
        // переписывались — с вершинами там, где прошла петля, — а версия тайла росла без записи в журнале: все видели
        // перемену сразу, а не через 20 минут.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (vera, _) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync(newcomer: true);
        var (gleb, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        await NeighboursAsync(api, anna, vera, area);
        var before = await TileAsync(gleb, tile);

        var claim = await WalkAndClaimAsync(Cancel, api, boris, Rectangle(area, 25, 20, 100, 60));
        await ProcessAsync(api, claim.RunId);

        await using (var db = database.CreateContext())
        {
            var capture = await db.Captures.AsNoTracking().SingleAsync(c => c.Id == claim.CaptureId, Cancel);
            Assert.Equal(CaptureStatus.Applied, capture.Status);
            Assert.Contains("newAccountLimited", capture.AreaByOutcome!); // петля правда прошла по чужой земле
        }

        var after = await TileAsync(gleb, tile);
        Assert.Equal(before.Version, (await ReadAsync(api, tile)).Version); // настоящая версия не выросла
        Assert.Equal(before.Version, after.Version);
        Assert.True(await IsUnchangedAsync(gleb, tile, before.Version));
        Assert.Equal(Ids(before), Ids(after));
    }

    [Fact]
    public async Task While_hidden_a_capture_leaves_no_trace_on_the_neighbours_land()
    {
        // Аудит BE-01: новичок обвёл землю Анны и Веры и ничью рядом — взял только ничью. Пока захват скрыт, Глеб получает
        // тайл, откаченный по журналу. Куски Анны и Веры в нём те же до вершины и с теми же номерами: раньше на них
        // оставались вершины ровно там, где скрытая петля пересекла их границы.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (vera, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync(newcomer: true);
        var (gleb, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        await NeighboursAsync(api, anna, vera, area);
        var before = await TileAsync(gleb, tile);

        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Rectangle(area, 25, 20, 200, 60))).RunId);
        var hidden = await TileAsync(gleb, tile);

        Assert.True((await ReadAsync(api, tile)).Version > before.Version); // захват применён и тайл изменился
        Assert.Equal(before.Version, hidden.Version);
        Assert.Equal(Ids(before), Ids(hidden));

        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        var revealed = await TileAsync(gleb, tile, known: before.Version);

        // После раскрытия у Анны и Веры всё те же куски, у Бориса — взятая ничья земля.
        Assert.True(revealed.Version > before.Version);
        Assert.Equal(Ids(before), Ids(revealed).Where(id => revealed.Parcels.Single(p => p.Id == id).OwnerId != borisId).ToList());
        Assert.Contains(revealed.Parcels, p => p.OwnerId == borisId);
    }

    [Fact]
    public async Task Authors_visits_wait_until_his_hidden_capture_is_public()
    {
        // Аудит BE-01, «проблема 2»: забег из офлайна — петля и ещё путь по взятой земле, отправлен через час. Конец забега
        // давно публичен, а захват применён только что и скрыт ещё 20 минут. Визиты автора легли бы на записанные захватом
        // куски, и проекции пришлось бы их переносить. Теперь они ждут раскрытия захвата: в его журнале земля автора.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (vera, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromHours(2)); // квадрат Анны уже публичен, а забег Бориса (час назад) — позже него
        var beforeBoris = await TileAsync(vera, tile);

        // Петля по правой половине квадрата Анны и ничьей земле рядом, потом ещё 160 м по взятому и прочь.
        var run = await WalkLoopAndOnAsync(
            Cancel,
            api,
            boris,
            Square(area, 50, 0, 100),
            [(area.X + 100, area.Y + 50), (area.X + 140, area.Y + 50), (area.X + 140, area.Y + 400)],
            startedAgo: TimeSpan.FromHours(1));
        Assert.Equal(1, await ProcessAsync(api, run.RunId));
        Assert.Null(await VisitAsync(api, run.RunId)); // захват скрыт — визиты ждут
        CaptureEntity capture;
        await using (var db = database.CreateContext())
        {
            capture = await db.Captures.AsNoTracking().SingleAsync(c => c.Id == run.CaptureId, Cancel);
            Assert.Equal(CaptureStatus.Applied, capture.Status);
            var land = await db.Parcels.AsNoTracking().Where(p => p.OwnerId == borisId).ToListAsync(Cancel);
            Assert.Equal(2, land.Count); // взятое у Анны и ничьё
            Assert.All(land, p => Assert.True(p.LastVisitAt <= capture.EffectiveAt)); // визитов по ним ещё нет
        }

        var hidden = await TileAsync(vera, tile);

        // Вера видит землю такой, какой она была до захвата, — всё до вершины и до поля: номера, уровни, времена, контуры.
        Assert.Equal(Content(beforeBoris), Content(hidden));

        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        Assert.Equal(2, await VisitAsync(api, run.RunId)); // захват публичен — визиты легли на оба куска Бориса
        List<long> visits;
        await using (var db = database.CreateContext())
        {
            var land = await db.Parcels.AsNoTracking().Where(p => p.OwnerId == borisId).ToListAsync(Cancel);
            Assert.All(land, p => Assert.True(p.LastVisitAt > capture.EffectiveAt));
            visits = [.. land.Select(p => p.LastVisitAt.ToUnixTimeMilliseconds() / 3_600_000 * 3_600_000).Order()];
        }

        var revealed = await TileAsync(vera, tile, known: hidden.Version);

        // После раскрытия — захват и визиты по нему (время чужого визита — до часа).
        var borisLand = revealed.Parcels.Where(p => p.OwnerId == borisId).ToList();
        Assert.InRange(borisLand.Sum(p => AreaOf(p.Exterior)), 9_500, 10_500);
        Assert.Equal(visits, borisLand.Select(p => p.LastVisitAtMs).Order());
    }

    [Fact]
    public async Task Victims_straight_run_over_a_hidden_crack_waits_for_the_reveal_and_leaves_no_seam()
    {
        // Бывший остаток BE-01 (territory-map.md), обычная игра: Анна (L2) пробежала напрямик через свой квадрат, а Борис
        // треснул его правую часть раньше, чем её визиты засчитаны (граница публичности ещё не дошла до конца её забега).
        // У каждого куска свой порог и своё время визита: по треснувшей части 60 м — визит, по остатку 40 м — нет. Раньше
        // визит ложился сразу, и у Веры треснувшая часть была L3 рядом с L2 на остатке — ступенька уровня ровно по линии
        // скрытой петли, до раскрытия. Теперь визиты ждут раскрытия: в журнале захвата земля Анны.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (vera, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromHours(21));
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId); // L2
        api.Time.Advance(TimeSpan.FromHours(23)); // визит поднял бы уровень: с повышения больше 20 ч
        var beforeBoris = await TileAsync(vera, tile);
        var whole = Assert.Single(beforeBoris.Parcels);
        Assert.Equal((annaId, (short)2), (whole.OwnerId, whole.Level));

        var walk = await WalkAndFinishAsync(
            Cancel, api, anna, [(area.X - 250, area.Y + 50), (area.X + 450, area.Y + 50)], startedAgo: TimeSpan.FromMinutes(90));
        Assert.Equal(1, await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Rectangle(area, 40, -10, 100, 120))).RunId));
        Assert.Null(await VisitAsync(api, walk.Id)); // конец забега публичен, но захват земли Анны ещё скрыт

        var hidden = await TileAsync(vera, tile);

        // Шва нет: Вера видит квадрат Анны таким, каким он был до захвата, — до вершины и до поля.
        Assert.Equal(Content(beforeBoris), Content(hidden));

        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        Assert.Equal(1, await VisitAsync(api, walk.Id)); // захват публичен — визит лёг на треснувшую часть
        DateTimeOffset visitedAt;
        await using (var db = database.CreateContext())
        {
            var land = await db.Parcels.AsNoTracking().Where(p => p.OwnerId == annaId).ToListAsync(Cancel);
            Assert.Equal(2, land.Count);
            var cracked = Assert.Single(land, p => p.SiegeUntil is not null);
            Assert.Equal(1, cracked.Level); // в осаде визит уровень не поднял
            visitedAt = cracked.LastVisitAt;
            Assert.True(Assert.Single(land, p => p.SiegeUntil is null).LastVisitAt < visitedAt); // остаток — без визита
        }

        var revealed = await TileAsync(vera, tile, known: hidden.Version);

        // После раскрытия — настоящая земля: треснувшая часть с визитом Анны (до часа), остаток как был, взятое Борисом.
        var hour = visitedAt.ToUnixTimeMilliseconds() / 3_600_000 * 3_600_000;
        Assert.Contains(revealed.Parcels, p => p.OwnerId == annaId && p.Level == 1 && p.SiegeUntilMs is not null && p.LastVisitAtMs == hour);
        Assert.Contains(revealed.Parcels, p => p.OwnerId == annaId && p.Level == 2 && p.LastVisitAtMs == whole.LastVisitAtMs);
        Assert.Contains(revealed.Parcels, p => p.OwnerId == borisId);
    }

    /// <summary>Всё, что зритель получает о кусках тайла, — для сравнения «до поля».</summary>
    private static string Content(TileTerritory tile) => System.Text.Json.JsonSerializer.Serialize(tile.Parcels, Json);

    /// <summary>
    /// Анна — квадрат 100 × 100 м, Вера забрала его правую половину и ничью землю рядом: три куска с общими границами
    /// (у Веры два — со щитом и без). Оба захвата уже публичны.
    /// </summary>
    private async Task NeighboursAsync(ApiFactory api, HttpClient anna, HttpClient vera, (double X, double Y) area)
    {
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromMinutes(30));
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, vera, Square(area, 50, 0, 100))).RunId);
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
    }

    private static List<long> Ids(TileTerritory tile) => [.. tile.Parcels.Select(p => p.Id).Order()];

    [Fact]
    public async Task Damaged_journal_of_a_hidden_capture_empties_the_tile_instead_of_breaking_the_map()
    {
        // Запись журнала испорчена (ручная правка базы): проекция её не прочтёт. Карта не должна отвечать 500 каждому,
        // кто смотрит эти тайлы, — тайл уходит пустым до раскрытия, как при ошибке движка.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (vera, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        var claim = await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100));
        await ProcessAsync(api, claim.RunId);
        await using (var db = database.CreateContext())
        {
            await db.CaptureJournal
                .Where(j => j.CaptureId == claim.CaptureId)
                .ExecuteUpdateAsync(set => set.SetProperty(j => j.Footprint, new byte[] { 0 }), Cancel);
        }

        var response = await vera.GetAsync($"/territory?league=run&tiles={tile.X}:{tile.Y}", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = await response.Content.ReadFromJsonAsync<TerritoryResponse>(Json, Cancel);
        Assert.Empty(Assert.Single(seen!.Tiles).Parcels);
    }

    private async Task<bool> IsUnchangedAsync(HttpClient client, TileKey tile, long known)
    {
        var response = await client.GetFromJsonAsync<TerritoryResponse>(
            $"/territory?league=run&tiles={tile.X}:{tile.Y}@{known}", Json, Cancel);
        return response!.Tiles.Count == 0 && response.Unchanged.Single() == new TileRef(tile.X, tile.Y);
    }

    private async Task<TileTerritory> TileAsync(HttpClient client, TileKey tile, long? known = null)
    {
        var at = known is { } version ? $"@{version}" : "";
        var response = await client.GetFromJsonAsync<TerritoryResponse>($"/territory?league=run&tiles={tile.X}:{tile.Y}{at}", Json, Cancel);
        return Assert.Single(response!.Tiles);
    }

    private static async Task<TileTerritory> ReadAsync(ApiFactory api, TileKey tile)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<TerritoryReader>();
        return (await reader.ReadAsync(Gorodki.Domain.Leagues.League.Run, [(tile, null)], new TerritoryViewer(null, Immediate: true), CancellationToken.None))
            .Tiles.Single();
    }

    [Fact]
    public async Task Empty_tile_has_version_zero_and_no_parcels()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();

        var response = await client.GetFromJsonAsync<TerritoryResponse>("/territory?league=bike&tiles=1:1", Json, Cancel);

        var tile = Assert.Single(response!.Tiles);
        Assert.Equal((0L, 0), (tile.Version, tile.Parcels.Count));
    }

    [Fact]
    public async Task Broken_query_is_refused()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();

        var response = await client.GetAsync("/territory?league=swim&tiles=1:1", Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Площадь кольца [широта, долгота, …] — обратно в UTM и по формуле площади многоугольника.</summary>
    private static double AreaOf(IReadOnlyList<double> ring)
    {
        var coordinates = new Coordinate[ring.Count / 2];
        for (var i = 0; i < coordinates.Length; i++)
        {
            var (easting, northing) = Utm34.Forward(ring[2 * i], ring[(2 * i) + 1]);
            coordinates[i] = new Coordinate(easting, northing);
        }

        return GeoOps.Factory.CreatePolygon(coordinates).Area;
    }
}
