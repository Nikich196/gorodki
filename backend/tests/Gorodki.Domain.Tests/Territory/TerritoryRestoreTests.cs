using CsCheck;
using Gorodki.Domain.Config;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Territory;
using NetTopologySuite.Algorithm.Locate;
using NetTopologySuite.Geometries;
using static Gorodki.Domain.Tests.Geo.TestGeometry;

namespace Gorodki.Domain.Tests.Territory;

/// <summary>
/// Откат захвата по журналу (PLAN.md, §3.9, слой 5; §7.3, шаг B.5): возвращается прежнее состояние только там,
/// где земля и сейчас такая, какой её оставил захват.
/// </summary>
public sealed class TerritoryRestoreTests
{
    private static readonly Guid Anna = new("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Boris = new("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid Vera = new("00000000-0000-0000-0000-00000000000c");
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static CaptureResult Capture(TerritoryMap map, Guid player, DateTimeOffset at, Polygon area)
    {
        var result = map.Apply(area, new CaptureContext(player, at, new HashSet<Guid>()));
        Assert.Empty(TerritoryInvariants.Check(map));
        return result;
    }

    private static RestoreResult Restore(TerritoryMap map, CaptureResult capture, Func<ParcelState, ParcelState?>? adjust = null)
    {
        var result = map.Restore(capture.Changes, adjust);
        Assert.Empty(TerritoryInvariants.Check(map));
        return result;
    }

    // ── Сценарии ─────────────────────────────────────────────────────────────

    [Fact]
    public void Rolling_back_a_capture_returns_the_land_it_took_and_frees_what_it_claimed()
    {
        var map = new TerritoryMap();
        Capture(map, Anna, T0, RectanglePolygon(0, 0, 200, 200));
        var cheat = Capture(map, Boris, T0.AddHours(1), RectanglePolygon(100, 0, 200, 200));
        Assert.Equal(20_000, map.AreaOf(Anna), 1);

        var result = Restore(map, cheat);

        Assert.Equal(40_000, map.AreaOf(Anna), 1);
        Assert.Equal(0, map.AreaOf(Boris), 1);
        Assert.Equal(40_000, result.RestoredArea, 1); // 20 000 м² вернулись Анне, 20 000 м² снова ничьи
        Assert.Equal(0, result.SkippedArea, 1);
        var anna = Assert.Single(map.Parcels);
        Assert.Equal(T0, anna.State.LastVisitAt); // то же состояние — куски снова слились в один
    }

    [Fact]
    public void Land_changed_after_the_capture_is_left_as_it_is()
    {
        var map = new TerritoryMap();
        Capture(map, Anna, T0, RectanglePolygon(0, 0, 200, 200));
        var cheat = Capture(map, Boris, T0.AddHours(1), RectanglePolygon(100, 0, 200, 200));
        Capture(map, Vera, T0.AddHours(14), RectanglePolygon(150, 50, 100, 100)); // после щита Бориса

        var result = Restore(map, cheat);

        Assert.Equal(10_000, map.AreaOf(Vera), 1); // земля Веры не тронута
        Assert.Equal(35_000, map.AreaOf(Anna), 1); // Анне вернулось всё, чего Вера не коснулась
        Assert.Equal(0, map.AreaOf(Boris), 1);
        Assert.Equal(10_000, result.SkippedArea, 1);
    }

    [Fact]
    public void Sliver_given_to_the_capturer_is_returned_too()
    {
        // Борис накрыл квадрат Анны, кроме полоски в 1 м: движок отдал полоску-осколок Борису. Откат возвращает и её.
        var map = new TerritoryMap();
        Capture(map, Anna, T0, RectanglePolygon(100, 100, 100, 100));
        var cheat = Capture(map, Boris, T0.AddHours(1), RectanglePolygon(50, 50, 149, 200));
        Assert.Equal(100, cheat.SliverArea, 1);
        Assert.Equal(0, map.AreaOf(Anna), 1);

        var result = Restore(map, cheat);

        Assert.Equal(10_000, map.AreaOf(Anna), 1);
        Assert.Equal(0, map.AreaOf(Boris), 1);
        Assert.Equal(0, result.SkippedArea, 1);
    }

    [Fact]
    public void Two_captures_of_one_player_roll_back_newest_first()
    {
        var map = new TerritoryMap();
        Capture(map, Anna, T0, RectanglePolygon(0, 0, 200, 200));
        var first = Capture(map, Boris, T0.AddHours(1), RectanglePolygon(100, 0, 200, 200));
        var second = Capture(map, Boris, T0.AddHours(2), RectanglePolygon(150, 0, 200, 200)); // освежил свою и взял ещё

        Restore(map, second);
        Restore(map, first);

        Assert.Equal(40_000, map.AreaOf(Anna), 1);
        Assert.Equal(0, map.AreaOf(Boris), 1);
        Assert.Single(map.Parcels);
    }

    [Fact]
    public void Returned_state_can_be_adjusted()
    {
        var map = new TerritoryMap();
        Capture(map, Anna, T0, RectanglePolygon(0, 0, 200, 200));
        var cheat = Capture(map, Boris, T0.AddHours(1), RectanglePolygon(100, 0, 200, 200));

        // Например, угасание не идёт, пока земля была отнята: визит сдвигается на время кражи.
        Restore(map, cheat, state => state with { LastVisitAt = state.LastVisitAt.AddDays(2) });

        Assert.Equal(40_000, map.AreaOf(Anna), 1);
        var visits = map.Parcels.Select(p => (p.State.LastVisitAt, Area: Math.Round(p.Geometry.Area))).OrderBy(v => v.LastVisitAt).ToList();
        Assert.Equal([(T0, 20_000.0), (T0.AddDays(2), 20_000.0)], visits);
    }

    [Fact]
    public void Land_of_an_owner_who_is_gone_comes_back_neutral()
    {
        // Аккаунт Анны удалён: вернуть ей землю некому — взятое у неё становится ничьим.
        var map = new TerritoryMap();
        Capture(map, Anna, T0, RectanglePolygon(0, 0, 200, 200));
        var cheat = Capture(map, Boris, T0.AddHours(1), RectanglePolygon(100, 0, 200, 200));

        var result = Restore(map, cheat, state => state.OwnerId == Anna ? null : state);

        Assert.Equal(20_000, map.AreaOf(Anna), 1); // нетронутая половина — как была
        Assert.Equal(0, map.AreaOf(Boris), 1);
        Assert.Equal(40_000, result.RestoredArea, 1);
        Assert.Equal(20_000, map.Parcels.Sum(p => p.Geometry.Area), 1);
    }

    [Fact]
    public void Land_changed_by_anything_else_than_a_capture_is_not_rolled_back()
    {
        // Смена сезона, удаление игрока — всё, что поменяло землю не захватом, тоже защищает её от отката.
        var map = new TerritoryMap();
        Capture(map, Anna, T0, RectanglePolygon(0, 0, 200, 200));
        var cheat = Capture(map, Boris, T0.AddHours(1), RectanglePolygon(100, 0, 200, 200));
        var reset = new TerritoryMap();
        reset.Load(map.Parcels.Select(p => p with { State = p.State with { ShieldUntil = null, LastVisitAt = T0.AddDays(3) } }));

        var result = Restore(reset, cheat);

        Assert.Equal(0, result.RestoredArea, 1);
        Assert.Equal(40_000, result.SkippedArea, 1);
        Assert.Equal(40_000, reset.AreaOf(Boris), 1); // всё, что взял Борис (у Анны и ничьё), осталось у него
    }

    [Fact]
    public void Own_check_keeps_the_cheater_from_laundering_the_land_by_visiting_it()
    {
        // Борис отнял половину у Анны и через сутки прошёл по ней снова — уровень вырос. Для обычной проверки это
        // «изменение после захвата», и откат оставил бы землю ему. Откат нарушителя свои касания нарушителя не считает.
        var map = new TerritoryMap();
        Capture(map, Anna, T0, RectanglePolygon(0, 0, 200, 200));
        var cheat = Capture(map, Boris, T0.AddHours(1), RectanglePolygon(100, 0, 200, 200));
        Capture(map, Boris, T0.AddHours(22), RectanglePolygon(100, 0, 200, 200));
        var strict = new TerritoryMap();
        strict.Load(map.Parcels);

        var skipped = strict.Restore(cheat.Changes);
        var result = map.Restore(
            cheat.Changes,
            untouched: (current, after) => current == after || (current?.OwnerId == Boris && after?.OwnerId == Boris));

        Assert.Equal(40_000, skipped.SkippedArea, 1);
        Assert.Empty(TerritoryInvariants.Check(map));
        Assert.Equal(40_000, result.RestoredArea, 1);
        Assert.Equal(40_000, map.AreaOf(Anna), 1);
        Assert.Equal(0, map.AreaOf(Boris), 1);
    }

    // ── Визиты после захвата (replayVisits) ──────────────────────────────────
    // Откат по журналу — у нарушителя и в публичной проекции скрытого захвата (TerritoryReader.ProjectAsync). Визиты,
    // засчитанные после захвата, меняют записанные им куски, и без переноса визитов откат оставлял бы их как есть.
    // Эталон — тот же мир без захвата с теми же визитами: куски те же до вершины и состояния. VisitAll даёт всем кускам
    // владельца одно время визита — у сервера так бывает редко: там у каждого куска своё время (последний шаг пути в нём)
    // и свой порог 50 м, это моделирует Run. Тесты с Run ниже держат, что остаётся (шов). Сервер визиты на землю ещё
    // скрытого захвата не засчитывает (VisitProcessor ждёт раскрытия), так что в проекции перенос — только страховка.

    [Fact]
    public void Victims_visit_with_one_time_on_both_parts_of_a_cracked_piece_is_replayed_on_the_whole_piece_and_leaves_no_seam()
    {
        // Анна (L2) пробежала по своей земле, пока захват Бориса, треснувший её правую половину, скрыт. Без переноса
        // визитов треснувшая часть осталась бы в проекции отдельным куском с уровнем −1 и осадой — скрытая петля видна.
        // Визит ложится на целый кусок так, как лёг бы без захвата: осады нет, и уровень растёт до 3. Шва нет только
        // потому, что у обеих частей одно время визита (VisitAll); с разным временем он остаётся — см. тесты с Run.
        var map = new TerritoryMap();
        Capture(map, Anna, T0, RectanglePolygon(100, 100, 200, 200));
        Capture(map, Anna, T0.AddHours(21), RectanglePolygon(100, 100, 200, 200)); // L2
        var withoutCapture = Copy(map);
        var hidden = Capture(map, Boris, T0.AddHours(42), RectanglePolygon(200, 90, 200, 220));
        Assert.Equal(20_000, hidden.Area(PieceOutcome.Cracked), 1);
        var at = T0.AddHours(42).AddMinutes(10);
        VisitAll(map, Anna, at);
        VisitAll(withoutCapture, Anna, at);

        var projection = Copy(map);
        var result = projection.Restore(hidden.Changes, replayVisits: true);

        Assert.Empty(TerritoryInvariants.Check(projection));
        Assert.Equal(0, result.SkippedArea, 1);
        AssertSameLand(withoutCapture, projection);
        var anna = Assert.Single(projection.Parcels);
        Assert.Equal((3, (DateTimeOffset?)null, at), (anna.State.Level, anna.State.SiegeUntil, anna.State.LastVisitAt));

        // Без переноса (по умолчанию, как у отката по растровому оракулу) треснувшая часть осталась бы как есть.
        var legacy = Copy(map);
        Assert.Equal(20_000, legacy.Restore(hidden.Changes).SkippedArea, 1);
        Assert.Contains(legacy.Parcels, p => p.State.SiegeUntil is not null);
    }

    [Fact]
    public void Authors_visits_to_land_of_a_hidden_capture_are_replayed_only_where_he_owned_it_before()
    {
        // Борис взял половину квадрата Анны и ничью землю и освежил часть своей старой земли; забег продолжился по
        // взятому, и визиты засчитались, пока захват скрыт. Взятое в проекции — снова Анны и ничьё (без визитов Бориса:
        // в мире без захвата этой земли у него нет), а освежённая часть — его прежняя земля с тем же визитом. Освежённая
        // часть и остаток его старого куска здесь получают одно время визита (VisitAll); с разным — шов, см. тест с Run.
        var map = new TerritoryMap();
        Capture(map, Anna, T0, RectanglePolygon(100, 100, 200, 200));
        Capture(map, Boris, T0, RectanglePolygon(400, 100, 200, 200));
        var withoutCapture = Copy(map);
        var hidden = Capture(map, Boris, T0.AddHours(21), RectanglePolygon(200, 150, 300, 100));
        Assert.Equal(10_000, hidden.Area(PieceOutcome.Transferred), 1);
        Assert.Equal(10_000, hidden.Area(PieceOutcome.ClaimedNeutral), 1);
        Assert.Equal(10_000, hidden.Area(PieceOutcome.Refreshed), 1);
        var at = T0.AddHours(21).AddMinutes(10);
        VisitAll(map, Boris, at);
        VisitAll(withoutCapture, Boris, at);

        var projection = Copy(map);
        var result = projection.Restore(hidden.Changes, replayVisits: true);

        Assert.Empty(TerritoryInvariants.Check(projection));
        Assert.Equal(0, result.SkippedArea, 1);
        AssertSameLand(withoutCapture, projection);
        Assert.Equal(40_000, projection.AreaOf(Boris), 1); // только старая земля — одним куском, уровень поднят визитом
        var boris = Assert.Single(projection.Parcels, p => p.State.OwnerId == Boris);
        Assert.Equal((2, at), (boris.State.Level, boris.State.LastLevelUpAt));

        var legacy = Copy(map);
        Assert.Equal(30_000, legacy.Restore(hidden.Changes).SkippedArea, 1);
        Assert.Equal(60_000, legacy.AreaOf(Boris), 1); // без переноса скрытый захват виден целиком
    }

    [Fact]
    public void Land_changed_by_another_capture_is_not_returned_even_with_visit_replay()
    {
        // Вера после щита забрала часть земли, взятой Борисом: это не визит, и откат её не трогает.
        var map = new TerritoryMap();
        Capture(map, Anna, T0, RectanglePolygon(100, 100, 200, 200));
        var cheat = Capture(map, Boris, T0.AddHours(1), RectanglePolygon(200, 100, 200, 200));
        Capture(map, Vera, T0.AddHours(14), RectanglePolygon(250, 150, 100, 100));
        VisitAll(map, Boris, T0.AddHours(14).AddMinutes(10));

        var result = map.Restore(cheat.Changes, replayVisits: true);

        Assert.Empty(TerritoryInvariants.Check(map));
        Assert.Equal(10_000, result.SkippedArea, 1);
        Assert.Equal(10_000, map.AreaOf(Vera), 1);
        Assert.Equal(35_000, map.AreaOf(Anna), 1);
        Assert.Equal(0, map.AreaOf(Boris), 1);
    }

    [Fact]
    public void Victims_visit_to_the_rest_of_a_transferred_piece_leaves_a_seam_in_the_fallback()
    {
        // Известный остаток отката по граням (docs/architecture/territory-map.md): Борис взял половину квадрата Анны,
        // а Анна пробежала по оставшейся половине. Взятая половина возвращается без визита (на ней Анна не была — там
        // земля Бориса), оставшаяся — с визитом: два куска Анны, шов по линии петли. В мире без захвата визит лёг бы на
        // весь квадрат. Закрывает это точный откат по исходным кускам (следующий шаг BE-01); когда этот тест упадёт,
        // значит, откат по граням научился большему — обнови документ.
        var map = new TerritoryMap();
        Capture(map, Anna, T0, RectanglePolygon(100, 100, 200, 200));
        var hidden = Capture(map, Boris, T0.AddHours(1), RectanglePolygon(200, 100, 200, 200));
        var at = T0.AddHours(1).AddMinutes(10);
        VisitAll(map, Anna, at);

        map.Restore(hidden.Changes, replayVisits: true);

        Assert.Empty(TerritoryInvariants.Check(map));
        var anna = map.Parcels.Where(p => p.State.OwnerId == Anna).OrderBy(p => p.State.LastVisitAt).ToList();
        Assert.Equal([T0, at], anna.Select(p => p.State.LastVisitAt));
        Assert.Equal(40_000, map.AreaOf(Anna), 1);
    }

    // Остаток отката по граням в обычной игре: Анна (L2) пробежала по своему квадрату, а через пару минут Борис треснул
    // его правую половину; визиты Анны засчитаны, когда граница публичности дошла до конца её забега, — захват Бориса ещё
    // скрыт. Визиты ложатся на настоящие куски: треснувшую часть и остаток, каждому своё время и свой порог (Run, как
    // VisitProcessor). Остаток лежит вне следа захвата, в журнал он не попал, и откат по граням его не видит: в проекции
    // два куска Анны вместо одного, шов ровно по линии скрытой петли. Лечить это слиянием соседних кусков вне следа
    // нельзя — откат не отличит остаток куска от законного соседнего куска того же владельца и сотрёт настоящий шов.
    // В проекции шов закрыт на сервере: визиты забега ждут, пока скрыт захват с землёй игрока в журнале (VisitProcessor,
    // интеграционные тесты проекции перевёрнуты). Эти тесты — предел отката по граням: у отката нарушителя это
    // неточность остатка на один визит.

    [Fact]
    public void Victims_run_over_both_parts_of_a_cracked_piece_leaves_a_seam_in_the_fallback()
    {
        var map = LandOfAnnaAtLevelTwo();
        var whole = Assert.Single(map.Parcels).State;
        var withoutCapture = Copy(map);
        var capturedAt = T0.AddHours(42);
        var hidden = Capture(map, Boris, capturedAt, RectanglePolygon(200, 90, 200, 220));
        var cracked = Assert.Single(map.Parcels, p => p.State.SiegeUntil is not null);
        var rest = Assert.Single(map.Parcels, p => p.State == whole);

        // С запада на восток по y = 200, закончил за 6 минут до петли Бориса: у остатка последний шаг — через линию петли
        // (−7 мин), у треснувшей части — следующий (−6 мин). По 100 м в каждом куске.
        (double, double, DateTimeOffset)[] path =
        [
            (50, 200, capturedAt.AddMinutes(-9)),
            (150, 200, capturedAt.AddMinutes(-8)),
            (250, 200, capturedAt.AddMinutes(-7)),
            (350, 200, capturedAt.AddMinutes(-6)),
        ];
        Run(map, Anna, path);
        Run(withoutCapture, Anna, path);

        var projection = Copy(map);
        var result = projection.Restore(hidden.Changes, replayVisits: true);

        Assert.Empty(TerritoryInvariants.Check(projection));
        Assert.Equal(0, result.SkippedArea, 1);
        var expected = Assert.Single(withoutCapture.Parcels); // без захвата — один кусок с визитом по последнему шагу в нём
        Assert.Equal(CaptureRules.Visit(whole, capturedAt.AddMinutes(-6), map.Rules), expected.State);

        // Сейчас: ни уровня −1, ни осады, но два куска Анны (оба L3, разное время визита и повышения) — шов по петле.
        AssertPieces(
            projection,
            (CaptureRules.Visit(whole, capturedAt.AddMinutes(-7), map.Rules)!, rest.Geometry),
            (CaptureRules.Visit(whole, capturedAt.AddMinutes(-6), map.Rules)!, cracked.Geometry));
    }

    [Fact]
    public void Victims_run_only_over_the_cracked_part_leaves_a_level_step_in_the_fallback()
    {
        // Тот же забег, но по остатку лишь 20 м — меньше порога визита. Без захвата весь квадрат получил бы визит и вырос
        // до L3; в проекции треснувшая часть — L3 с визитом, остаток — L2 без него: ступенька уровня по линии петли видна
        // всем, и без разбора геометрии.
        var map = LandOfAnnaAtLevelTwo();
        var whole = Assert.Single(map.Parcels).State;
        var withoutCapture = Copy(map);
        var capturedAt = T0.AddHours(42);
        var hidden = Capture(map, Boris, capturedAt, RectanglePolygon(200, 90, 200, 220));
        var cracked = Assert.Single(map.Parcels, p => p.State.SiegeUntil is not null);
        var rest = Assert.Single(map.Parcels, p => p.State == whole);
        (double, double, DateTimeOffset)[] path =
        [
            (180, 200, capturedAt.AddMinutes(-9)),
            (230, 200, capturedAt.AddMinutes(-8)),
            (350, 200, capturedAt.AddMinutes(-6)),
        ];
        Run(map, Anna, path);
        Run(withoutCapture, Anna, path);
        Assert.Contains(map.Parcels, p => p.State == whole); // остаток без визита: 20 м — меньше порога
        Assert.Contains(map.Parcels, p => p.State.SiegeUntil is not null && p.State.LastVisitAt == capturedAt.AddMinutes(-6));

        var projection = Copy(map);
        projection.Restore(hidden.Changes, replayVisits: true);

        Assert.Empty(TerritoryInvariants.Check(projection));
        var expected = Assert.Single(withoutCapture.Parcels);
        Assert.Equal((3, capturedAt.AddMinutes(-6)), (expected.State.Level, expected.State.LastVisitAt));
        var visited = CaptureRules.Visit(whole, capturedAt.AddMinutes(-6), map.Rules)!;
        Assert.Equal((3, 2), (visited.Level, whole.Level));
        AssertPieces(projection, (whole, rest.Geometry), (visited, cracked.Geometry));
    }

    [Fact]
    public void Authors_run_over_his_refreshed_part_and_the_rest_of_his_piece_leaves_a_seam_in_the_fallback()
    {
        // Борис (L1) петлёй освежил часть своего куска и взял ничью землю рядом; забег (из офлайна — захват применён позже
        // его конца) продолжился по освежённой части и по остатку куска, визиты засчитаны, пока захват скрыт. Освежённая
        // часть возвращается его прежним куском с её визитом, остаток вне следа — со своим: два куска, шов по петле.
        var map = new TerritoryMap();
        Capture(map, Boris, T0, RectanglePolygon(400, 100, 200, 200));
        var old = Assert.Single(map.Parcels).State;
        var withoutCapture = Copy(map);
        var capturedAt = T0.AddHours(21);
        var hidden = Capture(map, Boris, capturedAt, RectanglePolygon(300, 150, 200, 100));
        Assert.Equal(10_000, hidden.Area(PieceOutcome.Refreshed), 1);
        Assert.Equal(10_000, hidden.Area(PieceOutcome.ClaimedNeutral), 1);
        var refreshed = Assert.Single(map.Parcels, p => p.State.Level == 2);
        var rest = Assert.Single(map.Parcels, p => p.State == old);

        // После петли — на восток по y = 200: 60 м по освежённой части (последний шаг в ней кончился через 2 минуты после
        // петли), 100 м по остатку (через 3 минуты).
        (double, double, DateTimeOffset)[] path =
        [
            (440, 200, capturedAt.AddMinutes(1)),
            (550, 200, capturedAt.AddMinutes(2)),
            (650, 200, capturedAt.AddMinutes(3)),
        ];
        Run(map, Boris, path);
        Run(withoutCapture, Boris, path);

        var projection = Copy(map);
        var result = projection.Restore(hidden.Changes, replayVisits: true);

        Assert.Empty(TerritoryInvariants.Check(projection));
        Assert.Equal(0, result.SkippedArea, 1);
        var expected = Assert.Single(withoutCapture.Parcels);
        Assert.Equal(CaptureRules.Visit(old, capturedAt.AddMinutes(3), map.Rules), expected.State);
        AssertPieces(
            projection,
            (CaptureRules.Visit(old, capturedAt.AddMinutes(2), map.Rules)!, refreshed.Geometry),
            (CaptureRules.Visit(old, capturedAt.AddMinutes(3), map.Rules)!, rest.Geometry));
    }

    [Fact]
    public void Victims_own_refreshing_capture_over_a_cracked_part_is_replayed_as_a_visit()
    {
        // Анна, не зная о скрытой трещине, замкнула свою петлю внутри треснувшей части. Освежение — тот же
        // CaptureRules.Visit, от визита его не отличить, и откат переносит его как визит: внутри её петли — прежний кусок с
        // этим визитом (L3), вокруг — прежний кусок. Так же, как без захвата Бориса. Без переноса эта земля «тронута» и
        // осталась бы треснувшей — с осадой, на месте её петли.
        var map = LandOfAnnaAtLevelTwo();
        var withoutCapture = Copy(map);
        var hidden = Capture(map, Boris, T0.AddHours(42), RectanglePolygon(200, 90, 200, 220));
        var refreshedAt = T0.AddHours(42).AddMinutes(10);
        Assert.Equal(3_600, Capture(map, Anna, refreshedAt, RectanglePolygon(220, 120, 60, 60)).Area(PieceOutcome.Refreshed), 1);
        Capture(withoutCapture, Anna, refreshedAt, RectanglePolygon(220, 120, 60, 60));

        var projection = Copy(map);
        var result = projection.Restore(hidden.Changes, replayVisits: true);

        Assert.Empty(TerritoryInvariants.Check(projection));
        Assert.Equal(0, result.SkippedArea, 1);
        AssertSameLand(withoutCapture, projection);
        Assert.Contains(projection.Parcels, p => p.State.Level == 3 && p.State.LastVisitAt == refreshedAt);

        var legacy = Copy(map);
        Assert.Equal(3_600, legacy.Restore(hidden.Changes).SkippedArea, 1);
        Assert.Contains(legacy.Parcels, p => p.State.SiegeUntil is not null);
    }

    /// <summary>Квадрат Анны 200 × 200 м уровня 2 (повышение в T0 + 21 ч).</summary>
    private static TerritoryMap LandOfAnnaAtLevelTwo()
    {
        var map = new TerritoryMap();
        Capture(map, Anna, T0, RectanglePolygon(100, 100, 200, 200));
        Capture(map, Anna, T0.AddHours(21), RectanglePolygon(100, 100, 200, 200));
        Assert.Equal(2, Assert.Single(map.Parcels).State.Level);
        return map;
    }

    [Fact]
    public void Rollback_with_visit_replay_returns_a_visited_cracked_part_and_keeps_the_cheater_exemption()
    {
        // Откат нарушителя (CaptureRollback): Анна пробежала по треснувшей части — раньше это «касание», и трещина с
        // осадой оставалась у жертвы после отката. Свои визиты Бориса по взятому по-прежнему не защищают его: он дважды
        // поднял на нём уровень, а два повышения переносом визитов не объяснить (VisitReplay.Trace — null) — землю у него
        // забирает только исключение для нарушителя.
        var map = new TerritoryMap();
        Capture(map, Anna, T0, RectanglePolygon(100, 100, 200, 200));
        Capture(map, Anna, T0.AddHours(21), RectanglePolygon(100, 100, 200, 200)); // L2
        Capture(map, Vera, T0, RectanglePolygon(100, 300, 200, 100));
        Capture(map, Vera, T0.AddHours(21), RectanglePolygon(100, 300, 200, 100)); // L2
        var cheat = Capture(map, Boris, T0.AddHours(42), RectanglePolygon(200, 90, 200, 350)); // трещины у Анны и Веры
        Assert.Equal(30_000, cheat.Area(PieceOutcome.Cracked), 1);
        var at = T0.AddHours(43);
        VisitAll(map, Anna, at);
        VisitAll(map, Boris, T0.AddHours(62));
        VisitAll(map, Boris, T0.AddHours(82));
        Assert.All(map.Parcels.Where(p => p.State.OwnerId == Boris), p => Assert.Equal(3, p.State.Level));
        var withoutExemption = Copy(map);

        var result = map.Restore(
            cheat.Changes,
            untouched: (current, after) => current == after || (current?.OwnerId == Boris && after?.OwnerId == Boris),
            replayVisits: true);

        // Контроль: без исключения земля Бориса «тронута» и осталась бы у него.
        Assert.Equal(40_000, withoutExemption.Restore(cheat.Changes, replayVisits: true).SkippedArea, 1);
        Assert.Equal(40_000, withoutExemption.AreaOf(Boris), 1);
        Assert.Empty(TerritoryInvariants.Check(map));
        Assert.Equal(0, result.SkippedArea, 1);
        Assert.Equal(0, map.AreaOf(Boris), 1);
        var anna = Assert.Single(map.Parcels, p => p.State.OwnerId == Anna);
        Assert.Equal((3, (DateTimeOffset?)null, at), (anna.State.Level, anna.State.SiegeUntil, anna.State.LastVisitAt));
        Assert.Equal(40_000, anna.Geometry.Area, 1);
        var vera = Assert.Single(map.Parcels, p => p.State.OwnerId == Vera); // Вера не бегала — прежнее состояние
        Assert.Equal((2, (DateTimeOffset?)null), (vera.State.Level, vera.State.SiegeUntil));
    }

    /// <summary>
    /// Визит владельца на все его куски в один момент <paramref name="at"/>, на месте, как <c>VisitProcessor</c>. Одно время
    /// на все куски — упрощение: у сервера время у каждого куска своё (<see cref="Run"/>).
    /// </summary>
    private static void VisitAll(TerritoryMap map, Guid owner, DateTimeOffset at) => VisitEach(map, owner, _ => at);

    /// <summary>
    /// Визит владельца на каждый его кусок со своим временем: <paramref name="at"/> — по номеру куска владельца (null — без
    /// визита).
    /// </summary>
    private static void VisitEach(TerritoryMap map, Guid owner, Func<int, DateTimeOffset?> at)
    {
        var index = 0;
        var parcels = new List<Parcel>();
        foreach (var piece in map.Parcels)
        {
            var visited = piece.State.OwnerId == owner && at(index++) is { } time ? CaptureRules.Visit(piece.State, time, map.Rules) : null;
            parcels.Add(visited is null ? piece : piece with { State = visited });
        }

        map.Load(parcels);
    }

    /// <summary>
    /// Засчитанный путь забега владельца (метры от начала тестов, время точки) — как <c>VisitProcessor</c>: визит получает
    /// каждый его кусок, внутри которого не меньше <c>territory.visitMinMeters</c> пути, и время у каждого своё — конец
    /// последнего шага пути в этом куске (<see cref="Visits.Inside"/>). Обрезки 200 м у концов забега нет: путь — уже она.
    /// </summary>
    private static void Run(TerritoryMap map, Guid owner, IReadOnlyList<(double X, double Y, DateTimeOffset Time)> points)
    {
        var path = points
            .Zip(points.Skip(1), (from, to) => new Visits.Step(At(from.X, from.Y), At(to.X, to.Y), to.Time.ToUnixTimeMilliseconds()))
            .ToList();
        var own = map.Parcels.Where(p => p.State.OwnerId == owner).ToList();
        var inside = Visits.Inside(path, [.. own.Select(p => p.Geometry)]);
        VisitEach(map, owner, i => inside.TryGetValue(i, out var visit) && visit.Meters >= GameConfig.Default.Territory.VisitMinMeters
            ? DateTimeOffset.FromUnixTimeMilliseconds(visit.LastTimeMs)
            : null);
    }

    /// <summary>На карте ровно эти куски: состояние целиком и геометрия до вершины и порядка обхода.</summary>
    private static void AssertPieces(TerritoryMap map, params (ParcelState State, Polygon Geometry)[] expected)
    {
        var actual = map.Parcels.ToList();
        Assert.Equal(expected.Length, actual.Count);
        foreach (var (state, geometry) in expected)
        {
            Assert.True(
                actual.Any(p => p.State == state && p.Geometry.EqualsExact(geometry)),
                $"нет куска {state} {geometry}; есть: {string.Join("; ", actual.Select(p => $"{p.State} {p.Geometry}"))}");
        }
    }

    /// <summary>Та же земля: куски те же до вершины и порядка обхода и с тем же состоянием целиком.</summary>
    private static void AssertSameLand(TerritoryMap expected, TerritoryMap actual)
    {
        Assert.Equal(expected.Tiles, actual.Tiles);
        foreach (var tile in expected.Tiles)
        {
            Assert.Equal(expected.ParcelsIn(tile).Count, actual.ParcelsIn(tile).Count);
            foreach (var piece in expected.ParcelsIn(tile))
            {
                Assert.True(
                    actual.ParcelsIn(tile).Any(p => p.State == piece.State && p.Geometry.EqualsExact(piece.Geometry)),
                    $"нет куска {piece.State} {piece.Geometry}");
            }
        }
    }

    // ── Property-тесты ───────────────────────────────────────────────────────

    private static readonly Guid[] Players = [Anna, Boris, Vera, new("00000000-0000-0000-0000-00000000000d")];
    private static readonly CaptureShapeSettings ShapeSettings = new();

    private static long Iterations =>
        long.TryParse(Environment.GetEnvironmentVariable("GORODKI_GEO_ITERATIONS"), out var n) ? n : 150;

    public sealed record Step(int Player, double CenterX, double CenterY, double[] Radii, double Rotation, double HoursLater)
    {
        public override string ToString() => $"игрок {Player}, центр ({CenterX:0.#}; {CenterY:0.#}), лучей {Radii.Length}, через {HoursLater:0.#} ч";
    }

    // Петли вокруг угла четырёх тайлов — чтобы откат часто задевал несколько тайлов.
    private static readonly Gen<Step> StepGen =
        from player in Gen.Int[0, Players.Length - 1]
        from x in Gen.Double[-300, 300]
        from y in Gen.Double[-300, 300]
        from radii in Gen.Double[30, 200].Array[6, 16]
        from rotation in Gen.Double[0, 2 * Math.PI]
        from hours in Gen.Double[0, 30]
        select new Step(player, x, y, radii, rotation, hours);

    private static readonly Gen<(Step[] History, int Cheat)> HistoryGen =
        from history in StepGen.Array[1, 7]
        from cheat in Gen.Int[0, 6]
        select (history, cheat % history.Length);

    private static Geometry? ShapeOf(Step step)
    {
        var vertices = step.Radii
            .Select((r, k) =>
            {
                var angle = step.Rotation + 2 * Math.PI * k / step.Radii.Length;
                return (step.CenterX + r * Math.Cos(angle), step.CenterY + r * Math.Sin(angle));
            })
            .ToList();
        var shape = CaptureShapeBuilder.Build(Trail(vertices), 40, null, ShapeSettings);
        return shape.IsAccepted ? shape.Area : null;
    }

    /// <summary>
    /// Каждая третья петля — «большая» (§3.3, #48): пометка «спорная» тоже откатывается по журналу. Признак — из числа
    /// лучей, а не из генератора: так истории прежних seed не меняются.
    /// </summary>
    private static CaptureContext ContextOf(Step step, DateTimeOffset time) =>
        new(Players[step.Player], time, new HashSet<Guid>(), BigLoop: step.Radii.Length % 3 == 0);

    private static TerritoryMap Copy(TerritoryMap map)
    {
        var copy = new TerritoryMap(map.Rules, map.Slivers);
        copy.Load(map.Parcels);
        return copy;
    }

    [Fact]
    public void Rolling_back_the_last_capture_returns_the_map_before_it()
    {
        HistoryGen.Sample(sample =>
        {
            var (history, _) = sample;
            var map = new TerritoryMap();
            var time = T0;
            TerritoryMap? before = null;
            CaptureResult? last = null;
            foreach (var step in history)
            {
                time = time.AddHours(step.HoursLater);
                if (ShapeOf(step) is not { } area)
                {
                    continue;
                }

                before = Copy(map);
                last = map.Apply(area, ContextOf(step, time));
            }

            if (last is null || before is null)
            {
                return;
            }

            var restore = map.Restore(last.Changes);

            Assert.Empty(TerritoryInvariants.Check(map));
            var tolerance = 2 * last.Changes.Sum(c => TerritoryMap.SnapTolerance(c.Footprint)) + restore.SliverArea + 1;
            Assert.True(restore.SkippedArea <= tolerance, $"пропущено {restore.SkippedArea:0.##} м² сразу после захвата");
            foreach (var state in before.Parcels.Concat(map.Parcels).Select(p => p.State).Distinct())
            {
                var expected = GeoOps.UnionAll(before.Parcels.Where(p => p.State == state).Select(p => (Geometry)p.Geometry));
                var actual = GeoOps.UnionAll(map.Parcels.Where(p => p.State == state).Select(p => (Geometry)p.Geometry));
                var difference = GeoOps.Difference(expected, actual).Area + GeoOps.Difference(actual, expected).Area;
                Assert.True(
                    difference <= tolerance,
                    $"{state.OwnerId}: расхождение с картой до захвата {difference:0.##} м² (допуск {tolerance:0.##})");
            }
        }, iter: Iterations);
    }

    /// <summary>
    /// Растровый оракул отката захвата из середины истории: в точке следа, где земля и сейчас такая, какой её оставил
    /// захват, — прежнее состояние, в остальных точках — текущее. Сравнение по пикселям 1 × 1 м.
    /// </summary>
    [Fact]
    public void Rollback_restores_exactly_the_untouched_part_of_the_footprint()
    {
        const double pixel = 1.0;
        HistoryGen.Sample(sample =>
        {
            var (history, cheatIndex) = sample;
            var map = new TerritoryMap();
            var time = T0;
            CaptureResult? cheat = null;
            for (var i = 0; i < history.Length; i++)
            {
                time = time.AddHours(history[i].HoursLater);
                if (ShapeOf(history[i]) is not { } area)
                {
                    continue;
                }

                var result = map.Apply(area, ContextOf(history[i], time));
                if (i == cheatIndex)
                {
                    cheat = result;
                }
            }

            if (cheat is null || cheat.Changes.Count == 0)
            {
                return;
            }

            var current = Sampler.Of(map.Parcels.Select(p => new JournalPiece(p.Geometry, p.State)));
            var before = Sampler.Of(cheat.Changes.SelectMany(c => c.Before));
            var after = Sampler.Of(cheat.Changes.SelectMany(c => c.After));
            var footprint = GeoOps.UnionAll(cheat.Changes.Select(c => c.Footprint));
            var boundaries = footprint.Boundary.Length
                + cheat.Changes.SelectMany(c => c.Before.Concat(c.After)).Sum(p => p.Geometry.Boundary.Length)
                + map.Parcels.Where(p => p.Geometry.EnvelopeInternal.Intersects(footprint.EnvelopeInternal))
                    .Sum(p => p.Geometry.Boundary.Length);

            var restore = map.Restore(cheat.Changes);

            Assert.Empty(TerritoryInvariants.Check(map));
            var restored = Sampler.Of(map.Parcels.Select(p => new JournalPiece(p.Geometry, p.State)));
            var inside = new IndexedPointInAreaLocator(footprint);
            var envelope = footprint.EnvelopeInternal;
            var mismatched = 0;
            for (var x = Math.Floor(envelope.MinX) - 5; x <= envelope.MaxX + 5; x += pixel)
            {
                for (var y = Math.Floor(envelope.MinY) - 5; y <= envelope.MaxY + 5; y += pixel)
                {
                    var point = new Coordinate(x + pixel / 2, y + pixel / 2);
                    var now = current.At(point);
                    var expected = inside.Locate(point) == Location.Interior && now == after.At(point) ? before.At(point) : now;
                    if (restored.At(point) != expected)
                    {
                        mismatched++;
                    }
                }
            }

            // Ошибка растра — не больше полоски в пиксель вдоль всех границ плюс осколки, отданные соседям.
            var tolerance = boundaries * pixel * 0.2 + restore.SliverArea + 5;
            Assert.True(
                mismatched * pixel * pixel <= tolerance,
                $"захват {cheatIndex}: не совпало {mismatched} пикселей (допуск {tolerance:0.#} м²)");
        }, iter: Math.Max(20, Iterations / 3));
    }

    /// <summary>
    /// Issue #101 (ночной прогон, seed d9kvVvCld684, история сокращена до трёх захватов): после отката первого захвата угол
    /// куска Анны лежит на стороне нового куска Бориса — вершину на прямой новый кусок не хранит. Проверка со snap-rounding
    /// уводила эту сторону к соседней вершине Анны и находила «наложение» 0,01 м², которого в кусках нет.
    /// </summary>
    [Fact]
    public void Rollback_leaves_no_overlap_where_a_corner_lies_on_a_neighbours_side()
    {
        Step[] history =
        [
            new(
                Player: 2,
                CenterX: -45.06757136546571,
                CenterY: 46.69230769230769,
                Radii:
                [
                    168.0063034454596, 127.66666666666667, 79, 50.372549019607845, 49, 183.97420514334857, 67.83747923791671,
                    117.03333333333333, 66, 165.0528442116837, 50, 180, 73, 44.628070931516646,
                ],
                Rotation: 0,
                HoursLater: 25.002001926996414),
            new(
                Player: 1,
                CenterX: 0,
                CenterY: 187.19542912961788,
                Radii:
                [
                    48.52340525362342, 146, 71.51612903225806, 196, 110.66666666666667, 66.50877192982456, 177, 197.97689687737937,
                    59.93150684931507, 72.23076923076923, 124, 76.21299457692166, 127.11429988465954, 48,
                ],
                Rotation: 5,
                HoursLater: 10.761363636363637),
            new(
                Player: 0,
                CenterX: 0,
                CenterY: 25.4,
                Radii:
                [
                    188, 71, 38.08498774200106, 194.39237289845562, 54.964326109077916, 140.88, 66.88695014960163, 131.0857142857143,
                    144.50422229417023, 159, 109.85464569696879, 143, 192, 52.646636727142806, 137.69626379404872,
                ],
                Rotation: 5,
                HoursLater: 26.308866906130895),
        ];
        var map = new TerritoryMap();
        var time = T0;
        var captures = new List<CaptureResult>();
        foreach (var step in history)
        {
            time = time.AddHours(step.HoursLater);
            var area = ShapeOf(step);
            Assert.NotNull(area);
            captures.Add(map.Apply(area, new CaptureContext(Players[step.Player], time, new HashSet<Guid>())));
        }

        map.Restore(captures[0].Changes);

        Assert.Empty(TerritoryInvariants.Check(map));
        var pieces = map.ParcelsIn(new TileKey(684, 5775));
        var snapped = pieces.SelectMany((a, i) => pieces.Skip(i + 1).Select(b => GeoOps.Intersection(a.Geometry, b.Geometry).Area));
        Assert.Equal(0.01, snapped.Max(), 3); // со snap-rounding пара соседей всё ещё «налезает» — история не устарела
    }

    /// <summary>
    /// Растровый оракул отката с переносом визитов (публичная проекция скрытого захвата): после последнего захвата истории
    /// случайные владельцы пробежали по всем своим кускам, у каждого куска своё время визита. В точке следа, где земля
    /// такая, какой её оставил захват, — прежнее состояние; где её меняли только визиты — прежнее с теми же визитами
    /// (владелец до и после один) или просто прежнее (другой); в остальных точках — текущее.
    /// </summary>
    /// <remarks>
    /// Ожидаемое оракул считает теми же <see cref="VisitReplay.Trace"/> и <see cref="VisitReplay.Apply"/>: он проверяет
    /// учёт граней (какая грань какое состояние получает, самопроверки, сборку кусков), а не смысл переноса. Совпадение
    /// с миром без захвата проверяют сценарии выше; независимый оракул — «хранилище без скрытых захватов с визитами по
    /// номерам кусков, как у VisitProcessor» — шаг 9 BE-01. Этот тест доказательством такого совпадения не считать.
    /// </remarks>
    [Fact]
    public void Rollback_with_visit_replay_matches_a_raster_oracle()
    {
        const double pixel = 1.0;
        var samples =
            from sample in HistoryGen
            from visitors in Gen.Int[1, (1 << Players.Length) - 1]
            from minutes in Gen.Double[1, 25].Array[1, 6]
            select (sample.History, Visitors: visitors, Minutes: minutes);
        samples.Sample(sample =>
        {
            var (history, visitors, minutes) = sample;
            var map = new TerritoryMap();
            var time = T0;
            CaptureResult? hidden = null;
            foreach (var step in history)
            {
                time = time.AddHours(step.HoursLater);
                if (ShapeOf(step) is { } area)
                {
                    hidden = map.Apply(area, ContextOf(step, time));
                }
            }

            if (hidden is null || hidden.Changes.Count == 0)
            {
                return;
            }

            for (var i = 0; i < Players.Length; i++)
            {
                if ((visitors & (1 << i)) != 0)
                {
                    VisitEach(map, Players[i], piece => time.AddMinutes(minutes[piece % minutes.Length]));
                }
            }

            var current = Sampler.Of(map.Parcels.Select(p => new JournalPiece(p.Geometry, p.State)));
            var before = Sampler.Of(hidden.Changes.SelectMany(c => c.Before));
            var after = Sampler.Of(hidden.Changes.SelectMany(c => c.After));
            var footprint = GeoOps.UnionAll(hidden.Changes.Select(c => c.Footprint));
            var boundaries = footprint.Boundary.Length
                + hidden.Changes.SelectMany(c => c.Before.Concat(c.After)).Sum(p => p.Geometry.Boundary.Length)
                + map.Parcels.Where(p => p.Geometry.EnvelopeInternal.Intersects(footprint.EnvelopeInternal))
                    .Sum(p => p.Geometry.Boundary.Length);

            var restore = map.Restore(hidden.Changes, replayVisits: true);

            Assert.Empty(TerritoryInvariants.Check(map));
            var restored = Sampler.Of(map.Parcels.Select(p => new JournalPiece(p.Geometry, p.State)));
            var inside = new IndexedPointInAreaLocator(footprint);
            var envelope = footprint.EnvelopeInternal;
            var mismatched = 0;
            for (var x = Math.Floor(envelope.MinX) - 5; x <= envelope.MaxX + 5; x += pixel)
            {
                for (var y = Math.Floor(envelope.MinY) - 5; y <= envelope.MaxY + 5; y += pixel)
                {
                    var point = new Coordinate(x + pixel / 2, y + pixel / 2);
                    var now = current.At(point);
                    var expected = now;
                    if (inside.Locate(point) == Location.Interior)
                    {
                        var written = after.At(point);
                        var previous = before.At(point);
                        if (now == written)
                        {
                            expected = previous;
                        }
                        else if (now is not null && written is not null && VisitReplay.Trace(written, now, map.Rules) is { } visits)
                        {
                            expected = previous is not null && previous.OwnerId == written.OwnerId
                                ? VisitReplay.Apply(previous, visits, map.Rules)
                                : previous;
                        }
                    }

                    if (restored.At(point) != expected)
                    {
                        mismatched++;
                    }
                }
            }

            var tolerance = boundaries * pixel * 0.2 + restore.SliverArea + 5;
            Assert.True(
                mismatched * pixel * pixel <= tolerance,
                $"не совпало {mismatched} пикселей (допуск {tolerance:0.#} м²), пропущено {restore.SkippedArea:0.#} м²");
        }, iter: Math.Max(20, Iterations / 3));
    }

    /// <summary>Состояние в точке по набору кусков (локаторы строятся один раз).</summary>
    private sealed class Sampler(List<(JournalPiece Piece, IndexedPointInAreaLocator Locator)> pieces)
    {
        public static Sampler Of(IEnumerable<JournalPiece> pieces) =>
            new(pieces.Select(p => (p, new IndexedPointInAreaLocator(p.Geometry))).ToList());

        public ParcelState? At(Coordinate point) =>
            pieces
                .FirstOrDefault(p => p.Piece.Geometry.EnvelopeInternal.Contains(point) && p.Locator.Locate(point) == Location.Interior)
                .Piece?.State;
    }
}
