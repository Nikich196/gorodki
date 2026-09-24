using CsCheck;
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
    // Публичная проекция скрытого захвата — откат по журналу (TerritoryReader.ProjectAsync). Визиты, засчитанные, пока
    // захват скрыт, меняют записанные им куски, и без переноса визитов откат оставлял бы их как есть — захват был бы
    // виден раньше 20 минут. Эталон — тот же мир без захвата с теми же визитами: куски те же до вершины и состояния.

    [Fact]
    public void Victims_visit_to_a_cracked_part_is_replayed_on_the_whole_piece_and_leaves_no_seam()
    {
        // Анна (L2) пробежала по своей земле, пока захват Бориса, треснувший её правую половину, скрыт. Без переноса
        // визитов треснувшая часть осталась бы в проекции отдельным куском с уровнем −1 и осадой — скрытая петля видна.
        // Визит ложится на целый кусок так, как лёг бы без захвата: осады нет, и уровень растёт до 3.
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
        // в мире без захвата этой земли у него нет), а освежённая часть — его прежняя земля с тем же визитом.
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

    [Fact]
    public void Rollback_with_visit_replay_returns_a_visited_cracked_part_and_keeps_the_cheater_exemption()
    {
        // Откат нарушителя (CaptureRollback): Анна пробежала по треснувшей части — раньше это «касание», и трещина с
        // осадой оставалась у жертвы после отката. Свои визиты Бориса по взятому по-прежнему не защищают его.
        var map = new TerritoryMap();
        Capture(map, Anna, T0, RectanglePolygon(100, 100, 200, 200));
        Capture(map, Anna, T0.AddHours(21), RectanglePolygon(100, 100, 200, 200)); // L2
        Capture(map, Vera, T0, RectanglePolygon(100, 300, 200, 100));
        Capture(map, Vera, T0.AddHours(21), RectanglePolygon(100, 300, 200, 100)); // L2
        var cheat = Capture(map, Boris, T0.AddHours(42), RectanglePolygon(200, 90, 200, 350)); // трещины у Анны и Веры
        Assert.Equal(30_000, cheat.Area(PieceOutcome.Cracked), 1);
        var at = T0.AddHours(43);
        VisitAll(map, Anna, at);
        VisitAll(map, Boris, at);

        var result = map.Restore(
            cheat.Changes,
            untouched: (current, after) => current == after || (current?.OwnerId == Boris && after?.OwnerId == Boris),
            replayVisits: true);

        Assert.Empty(TerritoryInvariants.Check(map));
        Assert.Equal(0, result.SkippedArea, 1);
        Assert.Equal(0, map.AreaOf(Boris), 1);
        var anna = Assert.Single(map.Parcels, p => p.State.OwnerId == Anna);
        Assert.Equal((3, (DateTimeOffset?)null, at), (anna.State.Level, anna.State.SiegeUntil, anna.State.LastVisitAt));
        Assert.Equal(40_000, anna.Geometry.Area, 1);
        var vera = Assert.Single(map.Parcels, p => p.State.OwnerId == Vera); // Вера не бегала — прежнее состояние
        Assert.Equal((2, (DateTimeOffset?)null), (vera.State.Level, vera.State.SiegeUntil));
    }

    /// <summary>Визит владельца на все его куски в момент <paramref name="at"/> — как <c>VisitProcessor</c>: на месте.</summary>
    private static void VisitAll(TerritoryMap map, Guid owner, DateTimeOffset at) =>
        map.Load(map.Parcels
            .Select(p => p.State.OwnerId == owner && CaptureRules.Visit(p.State, at, map.Rules) is { } visited ? p with { State = visited } : p)
            .ToList());

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
                last = map.Apply(area, new CaptureContext(Players[step.Player], time, new HashSet<Guid>()));
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

                var result = map.Apply(area, new CaptureContext(Players[history[i].Player], time, new HashSet<Guid>()));
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
    /// Растровый оракул отката с переносом визитов (публичная проекция скрытого захвата): после последнего захвата истории
    /// случайные владельцы пробежали по всем своим кускам. В точке следа, где земля такая, какой её оставил захват, —
    /// прежнее состояние; где её меняли только визиты — прежнее с теми же визитами (владелец до и после один) или просто
    /// прежнее (другой); в остальных точках — текущее.
    /// </summary>
    [Fact]
    public void Rollback_with_visit_replay_matches_a_raster_oracle()
    {
        const double pixel = 1.0;
        var samples =
            from sample in HistoryGen
            from visitors in Gen.Int[1, (1 << Players.Length) - 1]
            from minutes in Gen.Double[1, 25]
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
                    hidden = map.Apply(area, new CaptureContext(Players[step.Player], time, new HashSet<Guid>()));
                }
            }

            if (hidden is null || hidden.Changes.Count == 0)
            {
                return;
            }

            var at = time.AddMinutes(minutes);
            for (var i = 0; i < Players.Length; i++)
            {
                if ((visitors & (1 << i)) != 0)
                {
                    VisitAll(map, Players[i], at);
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
