using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Territory;
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
    public async Task Authors_visits_while_his_capture_is_hidden_do_not_reveal_it()
    {
        // Аудит BE-01, «проблема 2»: забег из офлайна — петля и ещё путь по взятой земле, отправлен через час. Конец забега
        // давно публичен, и визиты засчитываются сразу, а захват применён только что и скрыт ещё 20 минут. Визиты автора
        // меняли записанные захватом куски, проекция считала эту землю «тронутой» и показывала её всем раньше времени.
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
        Assert.Equal(2, await VisitAsync(api, run.RunId)); // взятое у Анны и ничьё — оба куска Бориса
        List<long> visits;
        await using (var db = database.CreateContext())
        {
            var capture = await db.Captures.AsNoTracking().SingleAsync(c => c.Id == run.CaptureId, Cancel);
            Assert.Equal(CaptureStatus.Applied, capture.Status);
            var land = await db.Parcels.AsNoTracking().Where(p => p.OwnerId == borisId).ToListAsync(Cancel);
            Assert.Equal(2, land.Count);
            Assert.All(land, p => Assert.True(p.LastVisitAt > capture.EffectiveAt)); // визиты легли на скрытый захват
            visits = [.. land.Select(p => p.LastVisitAt.ToUnixTimeMilliseconds() / 3_600_000 * 3_600_000).Order()];
        }

        var hidden = await TileAsync(vera, tile);

        // Вера видит землю такой, какой она была до захвата, — всё до вершины и до поля: номера, уровни, времена, контуры.
        Assert.Equal(Content(beforeBoris), Content(hidden));

        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        var revealed = await TileAsync(vera, tile, known: hidden.Version);

        // После раскрытия — захват и визиты по нему (время чужого визита — до часа).
        var borisLand = revealed.Parcels.Where(p => p.OwnerId == borisId).ToList();
        Assert.InRange(borisLand.Sum(p => AreaOf(p.Exterior)), 9_500, 10_500);
        Assert.Equal(visits, borisLand.Select(p => p.LastVisitAtMs).Order());
    }

    [Fact]
    public async Task Victims_visit_with_one_time_on_both_parts_of_a_hidden_crack_does_not_reveal_it()
    {
        // Аудит BE-01: Борис треснул правую часть квадрата Анны (L2 → L1 и осада). Забег Анны кончился раньше, чем
        // применён захват, прошёл и по треснувшей части, и по остальной земле; визиты засчитаны, пока захват скрыт. Раньше
        // треснувшая часть с визитом оставалась в проекции как есть: отдельный кусок с уровнем −1 и осадой, шов ровно по
        // линии петли. Это случай одного времени визита у обеих частей — путь подобран так нарочно; в обычном забеге
        // времена разные, и шов остаётся (следующий тест).
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
        api.Time.Advance(TimeSpan.FromHours(2));
        var beforeBoris = Assert.Single((await TileAsync(vera, tile)).Parcels);
        Assert.Equal((annaId, (short)2), (beforeBoris.OwnerId, beforeBoris.Level));

        var claim = await WalkAndClaimAsync(Cancel, api, boris, Rectangle(area, 60, -10, 100, 120));
        Assert.Equal(1, await ProcessAsync(api, claim.RunId));

        // Анна: на восток по y = 30 через обе части, на север, на запад по y = 70 обратно через границу частей. Засчитанный
        // путь кончается (последние 200 м не в счёт) через 0,3 м после границы — на том же шаге пути, что её пересёк:
        // у обеих частей одно время визита, как у целого куска в мире без захвата.
        var border = await CrackBorderAsync(area, annaId, north: 70);
        var walk = await WalkAndFinishAsync(
            Cancel,
            api,
            anna,
            [
                (area.X - 300, area.Y + 30),
                (area.X + 90, area.Y + 30),
                (area.X + 90, area.Y + 70),
                (area.X + border - 0.8, area.Y + 70),
                (area.X + border - 200.3, area.Y + 70),
            ],
            startedAgo: TimeSpan.FromMinutes(90));
        Assert.Equal(2, await VisitAsync(api, walk.Id));
        DateTimeOffset visitedAt;
        await using (var db = database.CreateContext())
        {
            var land = await db.Parcels.AsNoTracking().Where(p => p.OwnerId == annaId).ToListAsync(Cancel);
            Assert.Equal(2, land.Count);
            Assert.Single(land, p => p.SiegeUntil is not null); // треснувшая часть в базе — L1 и осада
            visitedAt = Assert.Single(land.Select(p => p.LastVisitAt).Distinct());
        }

        var hidden = await TileAsync(vera, tile);

        // Один кусок Анны — тот же контур до вершины, уровень 2, без осады, визит (до часа) — как без захвата.
        var seen = Assert.Single(hidden.Parcels);
        Assert.Equal(
            (annaId, (short)2, (long?)null, (long?)null, visitedAt.ToUnixTimeMilliseconds() / 3_600_000 * 3_600_000),
            (seen.OwnerId, seen.Level, seen.ShieldUntilMs, seen.SiegeUntilMs, seen.LastVisitAtMs));
        Assert.Equal(beforeBoris.Exterior, seen.Exterior);
        Assert.Empty(seen.Holes);

        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        var revealed = await TileAsync(vera, tile, known: hidden.Version);

        Assert.Contains(revealed.Parcels, p => p.OwnerId == annaId && p.Level == 1 && p.SiegeUntilMs is not null);
        Assert.Contains(revealed.Parcels, p => p.OwnerId == borisId);
    }

    [Fact]
    public async Task Victims_straight_run_over_a_hidden_crack_is_projected_as_the_whole_piece_with_the_visit()
    {
        // Обычная игра (territory-map.md): Анна (L2) пробежала напрямик через свой квадрат, а Борис треснул его правую часть
        // раньше, чем её визиты засчитаны (граница публичности ещё не дошла до конца её забега). У каждого куска свой порог и
        // своё время визита (VisitProcessor): по треснувшей части 60 м — визит, по остатку 40 м — нет. Откат по граням давал
        // Вере ступеньку уровня ровно по линии скрытой петли (L3 на треснувшей части у L2 на остатке). Точный откат
        // возвращает строку целого квадрата с этим визитом — как без захвата: 100 м пути в квадрате, последний шаг — тот же,
        // визит и рост до L3.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (vera, veraId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromHours(21));
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId); // L2
        api.Time.Advance(TimeSpan.FromHours(23)); // визит поднял бы уровень: с повышения больше 20 ч
        var beforeBoris = Assert.Single((await TileAsync(vera, tile)).Parcels);
        Assert.Equal((annaId, (short)2), (beforeBoris.OwnerId, beforeBoris.Level));

        var walk = await WalkAndFinishAsync(
            Cancel, api, anna, [(area.X - 250, area.Y + 50), (area.X + 450, area.Y + 50)], startedAgo: TimeSpan.FromMinutes(90));
        Assert.Equal(1, await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Rectangle(area, 40, -10, 100, 120))).RunId));
        Assert.Equal(1, await VisitAsync(api, walk.Id)); // только треснувшая часть
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

        var hidden = await TileAsync(vera, tile);
        var (_, stats) = await ProjectedAsync(api, tile, veraId);

        // Один кусок — весь квадрат Анны до вершины, L3, визит (до часа), без осады; номер — как у настоящего такого куска.
        var expected = new ParcelView(
            0, annaId, beforeBoris.ColorIndex, 3, false, visitedAt.ToUnixTimeMilliseconds() / 3_600_000 * 3_600_000, null, null,
            beforeBoris.Exterior, beforeBoris.Holes);
        Assert.Equal(Content([expected with { Id = TerritoryReader.ContentId(tile, expected) }]), Content(hidden));
        Assert.Equal((1, 0), (stats.Exact, stats.Fallback));

        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        var revealed = await TileAsync(vera, tile, known: hidden.Version);

        Assert.Contains(revealed.Parcels, p => p.OwnerId == annaId && p.Level == 1 && p.SiegeUntilMs is not null);
        Assert.Contains(revealed.Parcels, p => p.OwnerId == borisId);
    }

    /// <summary>
    /// Где на высоте <paramref name="north"/> (метры от угла места теста) проходит западная граница треснувшей части земли
    /// игрока — её общая граница с остальной его землёй, метры от угла места.
    /// </summary>
    private async Task<double> CrackBorderAsync((double X, double Y) area, Guid ownerId, double north)
    {
        await using var db = database.CreateContext();
        var cracked = await db.Parcels.AsNoTracking().SingleAsync(p => p.OwnerId == ownerId && p.SiegeUntil != null, Cancel);
        var y = WalkOrigin.Y + area.Y + north;
        var line = GeoOps.Factory.CreateLineString([new Coordinate(WalkOrigin.X + area.X - 1_000, y), new Coordinate(WalkOrigin.X + area.X + 1_000, y)]);
        return cracked.Geometry.Intersection(line).Coordinates.Min(c => c.X) - WalkOrigin.X - area.X;
    }

    /// <summary>Всё, что зритель получает о кусках тайла, — для сравнения «до поля».</summary>
    private static string Content(TileTerritory tile) => Content(tile.Parcels);

    private static string Content(IReadOnlyList<ParcelView> parcels) => System.Text.Json.JsonSerializer.Serialize(parcels, Json);

    /// <summary>
    /// Тайл глазами игрока — через сервис, как <c>GET /territory</c>, — и то, как проекция откатывала скрытые от него
    /// захваты (<paramref name="beforeParcels"/> — вызов посреди чтения, после списка скрытых захватов).
    /// </summary>
    private static async Task<(TileTerritory Tile, ProjectionStats Stats)> ProjectedAsync(
        ApiFactory api, TileKey tile, Guid viewerId, Func<CancellationToken, Task>? beforeParcels = null)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<TerritoryReader>();
        reader.BeforeParcelsRead = beforeParcels;
        var response = await reader.ReadAsync(
            Gorodki.Domain.Leagues.League.Run, [(tile, null)], new TerritoryViewer(viewerId, Immediate: false), CancellationToken.None);
        return (response.Tiles.Single(), reader.Projections);
    }

    /// <summary>Куски тайла в базе — как их грузит проекция.</summary>
    private async Task<List<ProjectedParcel>> StoredAsync(TileKey tile)
    {
        await using var db = database.CreateContext();
        return [.. (await db.Parcels.AsNoTracking()
                .Where(p => p.League == Gorodki.Domain.Leagues.League.Run && p.TileX == tile.X && p.TileY == tile.Y)
                .ToListAsync(Cancel))
            .Select(p => new ProjectedParcel(
                p.Id,
                new Parcel(
                    tile,
                    p.Geometry,
                    new ParcelState
                    {
                        OwnerId = p.OwnerId,
                        Level = p.Level,
                        LastVisitAt = p.LastVisitAt,
                        LastLevelUpAt = p.LastLevelUpAt,
                        ShieldUntil = p.ShieldUntil,
                        SiegeUntil = p.SiegeUntil,
                        LossWindowSince = p.LossWindowSince,
                        LossAttackers = AttackerSet.Of(p.LossAttackers),
                    })))];
    }

    [Fact]
    public async Task While_hidden_a_cut_across_a_slanted_border_leaves_the_same_pieces_and_ids()
    {
        // Бывший остаток BE-01: петля Бориса пересекла наклонную сторону земли Анны не в узле сетки 0,1 м, snap-rounding
        // сломал сторону в точке пересечения, и остаток куска Анны записан с изломом. Откат по граням следа собирал землю
        // вместе с изломом — у Веры другие номера кусков и вершины там, где прошла петля. Точный откат возвращает строку
        // Анны как она лежала: Вера до раскрытия получает тайл тем же до поля, с той же версией.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        var (vera, veraId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Polygon(area, (0, 0), (200, 0), (200, 70), (0, 37)))).RunId);
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        var before = await TileAsync(vera, tile);
        var annaBefore = Assert.Single(await StoredAsync(tile)).Parcel;

        var claim = await WalkAndClaimAsync(Cancel, api, boris, Rectangle(area, 50, 20, 100, 130));
        Assert.Equal(1, await ProcessAsync(api, claim.RunId));

        // Сценарий отличает точный откат: запасной путь (по граням) вернул бы Анне кусок с изломом, а не тот, что лежал.
        await using (var db = database.CreateContext())
        {
            var journal = await CaptureJournal.LoadTileAsync(db, claim.CaptureId, tile, Cancel);
            var fallback = ExactUndo.Restore(tile, await StoredAsync(tile), journal.Change(), new TerritoryRules(), new SliverSettings());
            var annaFallback = Assert.Single(fallback, p => p.Parcel.State == annaBefore.State).Parcel;
            Assert.False(annaFallback.Geometry.EqualsExact(annaBefore.Geometry));
        }

        var hidden = await TileAsync(vera, tile);
        var (_, stats) = await ProjectedAsync(api, tile, veraId);

        Assert.Equal(before.Version, hidden.Version);
        Assert.Equal(Content(before), Content(hidden));
        Assert.Equal((1, 0, 0), (stats.Exact, stats.Fallback, stats.EmptyTiles));
    }

    [Fact]
    public async Task Stacked_hidden_captures_are_undone_exactly_for_each_kind_of_viewer()
    {
        // Борис взял часть земли Анны, через три минуты Глеб — часть остатка Анны (его вставил захват Бориса); оба захвата
        // ещё скрыты. Вера (не захватывала) видит тайл, каким он был до обоих; Борис — каким он был сразу после его захвата:
        // откат захвата Глеба возвращает остаток Анны с тем же номером строки, и захват Бориса откатывается по нему точно.
        // Глебу захват Бориса точно не откатить (остатка Анны с тем номером уже нет) — запасной путь, своя земля остаётся.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (gleb, glebId) = await api.CreatePlayerClientAsync();
        var (vera, veraId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 200))).RunId);
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        var beforeAll = await TileAsync(vera, tile);

        Assert.Equal(1, await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Rectangle(area, 100, 50, 150, 100))).RunId));
        var afterBoris = await TileAsync(boris, tile);
        api.Time.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(1, await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, gleb, Rectangle(area, 20, 20, 100, 60))).RunId));

        var (third, thirdStats) = await ProjectedAsync(api, tile, veraId);
        var (earlier, earlierStats) = await ProjectedAsync(api, tile, borisId);
        var (later, laterStats) = await ProjectedAsync(api, tile, glebId);

        Assert.Equal(Content(beforeAll), Content(third));
        Assert.Equal((2, 0), (thirdStats.Exact, thirdStats.Fallback));
        Assert.Equal(Content(afterBoris), Content(earlier));
        Assert.Equal((1, 0), (earlierStats.Exact, earlierStats.Fallback));
        Assert.Equal((0, 1, 1), (laterStats.Exact, laterStats.Fallback, laterStats.Paths.GetValueOrDefault(UndoPath.Missing)));
        Assert.DoesNotContain(later.Parcels, p => p.OwnerId == borisId);
        var glebStored = (await StoredAsync(tile)).Where(p => p.Parcel.State.OwnerId == glebId).Sum(p => p.Parcel.Geometry.Area);
        var glebSeen = later.Parcels.Where(p => p.OwnerId == glebId).Sum(p => AreaOf(p.Exterior));
        Assert.InRange(glebSeen, glebStored - 2, glebStored + 2); // контур у зрителя — в градусах до ~1 см
    }

    [Fact]
    public async Task Capture_committed_in_the_middle_of_a_read_is_not_seen_by_it()
    {
        // Всё читается одним снимком (REPEATABLE READ): версии, список скрытых захватов, куски и журнал. Захват Глеба
        // записан после списка скрытых захватов, но до чтения кусков. Иначе его земля попала бы к Вере в куски, а в список
        // скрытых — нет: захват был бы виден сразу, а версия тайла — не та.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        var (gleb, glebId) = await api.CreatePlayerClientAsync();
        var (vera, veraId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        var before = await TileAsync(vera, tile);
        Assert.Equal(1, await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100))).RunId));
        var glebClaim = await WalkAndClaimAsync(Cancel, api, gleb, Rectangle(area, 0, 150, 100, 100));

        var (seen, stats) = await ProjectedAsync(api, tile, veraId, async _ => Assert.Equal(1, await ProcessAsync(api, glebClaim.RunId)));

        Assert.Equal(before.Version, seen.Version);
        Assert.Equal(Content(before), Content(seen));
        Assert.Equal((1, 0), (stats.Exact, stats.Fallback));
        await using var db = database.CreateContext();
        Assert.True(await db.Parcels.AnyAsync(p => p.OwnerId == glebId, Cancel)); // захват Глеба правда записан посреди чтения
    }

    [Fact]
    public async Task Corrupt_exact_undo_row_falls_back_to_the_footprint_restore()
    {
        // Испорченная строка точного отката (ручная правка базы) не роняет карту и не опустошает тайл: откат идёт по граням
        // следа, как до точного отката, — квадрат Анны у Веры целый.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (vera, veraId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        var claim = await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100));
        await ProcessAsync(api, claim.RunId);
        await using (var db = database.CreateContext())
        {
            Assert.Equal(1, await db.CaptureJournalParcels
                .Where(r => r.CaptureId == claim.CaptureId && r.Replaced)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.Geometry, new byte[] { 0x03, 0x00, 0x05 }), Cancel));
        }

        var response = await vera.GetAsync($"/territory?league=run&tiles={tile.X}:{tile.Y}", Cancel);
        var (seen, stats) = await ProjectedAsync(api, tile, veraId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((0, 1, 1), (stats.Exact, stats.Fallback, stats.Paths.GetValueOrDefault(UndoPath.Exception)));
        Assert.Contains("FormatException", stats.Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain(seen.Parcels, p => p.OwnerId == borisId);
        Assert.InRange(seen.Parcels.Where(p => p.OwnerId == annaId).Sum(p => AreaOf(p.Exterior)), 9_500, 10_500);
    }

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
        // Запись журнала испорчена (ручная правка базы), а строк точного отката нет (захват записан до них): проекция её не
        // прочтёт. Карта не должна отвечать 500 каждому, кто смотрит эти тайлы, — тайл уходит пустым до раскрытия, как при
        // ошибке движка.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (vera, veraId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        var claim = await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100));
        await ProcessAsync(api, claim.RunId);
        await using (var db = database.CreateContext())
        {
            await db.CaptureJournal
                .Where(j => j.CaptureId == claim.CaptureId)
                .ExecuteUpdateAsync(set => set.SetProperty(j => j.Footprint, new byte[] { 0 }), Cancel);
            await db.CaptureJournalParcels.Where(r => r.CaptureId == claim.CaptureId).ExecuteDeleteAsync(Cancel);
        }

        var response = await vera.GetAsync($"/territory?league=run&tiles={tile.X}:{tile.Y}", Cancel);
        var (_, stats) = await ProjectedAsync(api, tile, veraId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = await response.Content.ReadFromJsonAsync<TerritoryResponse>(Json, Cancel);
        Assert.Empty(Assert.Single(seen!.Tiles).Parcels);
        Assert.Equal(1, stats.EmptyTiles);
    }

    [Fact]
    public async Task Damaged_footprint_does_not_matter_when_the_capture_is_undone_exactly()
    {
        // Запись «до/после» нужна только запасному пути: по строкам точного отката захват откатывается и без неё.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (vera, veraId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        var before = await TileAsync(vera, tile);
        var claim = await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100));
        await ProcessAsync(api, claim.RunId);
        await using (var db = database.CreateContext())
        {
            await db.CaptureJournal
                .Where(j => j.CaptureId == claim.CaptureId)
                .ExecuteUpdateAsync(set => set.SetProperty(j => j.Footprint, new byte[] { 0 }), Cancel);
        }

        var (seen, stats) = await ProjectedAsync(api, tile, veraId);

        Assert.Equal((before.Version, 0), (seen.Version, seen.Parcels.Count));
        Assert.Equal((1, 0, 0), (stats.Exact, stats.Fallback, stats.EmptyTiles));
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
