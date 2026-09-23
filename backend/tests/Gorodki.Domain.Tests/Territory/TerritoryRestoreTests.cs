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
