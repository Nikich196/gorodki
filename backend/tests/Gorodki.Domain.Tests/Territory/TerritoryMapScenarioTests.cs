using Gorodki.Domain.Geo;
using Gorodki.Domain.Territory;
using NetTopologySuite.Geometries;
using static Gorodki.Domain.Tests.Geo.TestGeometry;

namespace Gorodki.Domain.Tests.Territory;

/// <summary>Сценарии правил земли (PLAN.md, §3.3) на настоящей геометрии.</summary>
public sealed class TerritoryMapScenarioTests
{
    private static readonly Guid Anna = new("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Boris = new("00000000-0000-0000-0000-00000000000b");
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static CaptureResult Capture(TerritoryMap map, Guid player, DateTimeOffset at, Polygon area, params Guid[] clan)
    {
        var result = map.Apply(area, new CaptureContext(player, at, clan.ToHashSet()));
        Assert.Empty(TerritoryInvariants.Check(map));
        return result;
    }

    private static IEnumerable<Parcel> LandOf(TerritoryMap map, Guid player) =>
        map.Parcels.Where(p => p.State.OwnerId == player);

    [Fact]
    public void Map_loaded_from_storage_continues_exactly_like_the_original()
    {
        // Так работает сервер: куски задетых тайлов читаются из базы в новую карту, захват применяется к ней.
        var original = new TerritoryMap();
        Capture(original, Anna, T0, RectanglePolygon(-100, -100, 200, 200));
        Capture(original, Boris, T0.AddHours(1), RectanglePolygon(-50, -50, 120, 80));
        var loaded = new TerritoryMap();
        loaded.Load(original.Parcels);

        var next = RectanglePolygon(20, -150, 100, 300);
        var fromOriginal = Capture(original, Boris, T0.AddHours(2), next);
        var fromLoaded = Capture(loaded, Boris, T0.AddHours(2), next);

        Assert.Equal(original.StateHash(), loaded.StateHash());
        Assert.Equal(fromOriginal.ChangedTiles, fromLoaded.ChangedTiles);
    }

    [Fact]
    public void Neutral_capture_becomes_level_one_land()
    {
        var map = new TerritoryMap();

        var result = Capture(map, Anna, T0, RectanglePolygon(100, 100, 100, 100));

        Assert.Equal(10_000, map.AreaOf(Anna), 3);
        Assert.Equal(10_000, result.Area(PieceOutcome.ClaimedNeutral), 3);
        var piece = Assert.Single(map.Parcels);
        Assert.Equal(1, piece.State.Level);
        Assert.Null(piece.State.ShieldUntil);
    }

    [Fact]
    public void Capture_across_a_tile_corner_is_split_into_four_tiles()
    {
        var map = new TerritoryMap();

        var result = Capture(map, Anna, T0, RectanglePolygon(-100, -100, 200, 200));

        Assert.Equal(4, result.ChangedTiles.Count);
        Assert.Equal(4, map.Parcels.Count());
        Assert.Equal(40_000, map.AreaOf(Anna), 3);
        Assert.All(map.Parcels, p => Assert.Equal(10_000, p.Geometry.Area, 3));
    }

    [Fact]
    public void Enemy_level_one_inside_the_loop_changes_hands_with_a_shield()
    {
        var map = new TerritoryMap();
        Capture(map, Anna, T0, RectanglePolygon(100, 100, 100, 100));

        var result = Capture(map, Boris, T0.AddHours(1), RectanglePolygon(150, 100, 100, 100));

        Assert.Equal(5_000, map.AreaOf(Anna), 3);
        Assert.Equal(10_000, map.AreaOf(Boris), 3);
        Assert.Equal(5_000, result.Area(PieceOutcome.Transferred), 3);
        var taken = LandOf(map, Boris).Single(p => p.State.ShieldUntil is not null);
        Assert.Equal(T0.AddHours(13), taken.State.ShieldUntil);
    }

    [Fact]
    public void Enemy_level_two_only_cracks_and_goes_under_siege()
    {
        var map = new TerritoryMap();
        Capture(map, Anna, T0, RectanglePolygon(100, 100, 100, 100));
        Capture(map, Anna, T0.AddHours(21), RectanglePolygon(100, 100, 100, 100)); // визит через 21 ч → L2

        var result = Capture(map, Boris, T0.AddHours(22), RectanglePolygon(150, 100, 100, 100));

        Assert.Equal(10_000, map.AreaOf(Anna), 3); // земля осталась у Анны
        Assert.Equal(5_000, map.AreaOf(Boris), 3); // Борис взял только ничью половину
        Assert.Equal(5_000, result.Area(PieceOutcome.Cracked), 3);
        var cracked = LandOf(map, Anna).Single(p => p.State.Level == 1);
        Assert.Equal(T0.AddHours(46), cracked.State.SiegeUntil);
        Assert.Equal(5_000, cracked.Geometry.Area, 3);
        Assert.Equal(5_000, LandOf(map, Anna).Single(p => p.State.Level == 2).Geometry.Area, 3);
    }

    [Fact]
    public void Own_land_levels_up_at_most_once_per_twenty_hours()
    {
        var map = new TerritoryMap();
        var block = RectanglePolygon(100, 100, 100, 100);
        Capture(map, Anna, T0, block);

        Capture(map, Anna, T0.AddHours(5), block);
        Assert.Equal(1, Assert.Single(map.Parcels).State.Level);
        Assert.Equal(T0.AddHours(5), map.Parcels.Single().State.LastVisitAt);

        Capture(map, Anna, T0.AddHours(21), block);
        Assert.Equal(2, map.Parcels.Single().State.Level);

        Capture(map, Anna, T0.AddHours(42), block);
        Capture(map, Anna, T0.AddHours(63), block);
        Assert.Equal(3, map.Parcels.Single().State.Level); // потолок — уровень 3
    }

    [Fact]
    public void Shield_protects_freshly_taken_land()
    {
        var map = new TerritoryMap();
        Capture(map, Anna, T0, RectanglePolygon(100, 100, 100, 100));
        Capture(map, Boris, T0.AddHours(1), RectanglePolygon(100, 100, 100, 100)); // забрал всё, щит до T0+13

        var early = Capture(map, Anna, T0.AddHours(3), RectanglePolygon(100, 100, 100, 100));
        Assert.Equal(10_000, early.Area(PieceOutcome.Shielded), 3);
        Assert.Equal(10_000, map.AreaOf(Boris), 3);

        Capture(map, Anna, T0.AddHours(14), RectanglePolygon(100, 100, 100, 100));
        Assert.Equal(10_000, map.AreaOf(Anna), 3);
    }

    [Fact]
    public void Clan_mates_land_is_refreshed_not_taken()
    {
        var map = new TerritoryMap();
        Capture(map, Anna, T0, RectanglePolygon(100, 100, 100, 100));

        var result = Capture(map, Boris, T0.AddHours(2), RectanglePolygon(150, 100, 100, 100), Anna);

        Assert.Equal(10_000, map.AreaOf(Anna), 3);
        Assert.Equal(5_000, result.Area(PieceOutcome.RefreshedForClanMate), 3);
        Assert.Contains(LandOf(map, Anna), p => p.State.LastVisitAt == T0.AddHours(2));
    }

    [Fact]
    public void Loop_through_the_middle_splits_enemy_land_into_parts()
    {
        var map = new TerritoryMap();
        Capture(map, Anna, T0, RectanglePolygon(100, 100, 300, 100));

        Capture(map, Boris, T0.AddHours(1), RectanglePolygon(200, 50, 100, 200));

        Assert.Equal(2, LandOf(map, Anna).Count());
        Assert.Equal(20_000, map.AreaOf(Anna), 3);
        Assert.Equal(20_000, map.AreaOf(Boris), 3);
    }

    [Fact]
    public void Thin_leftover_is_absorbed_by_the_neighbour()
    {
        var map = new TerritoryMap();
        Capture(map, Anna, T0, RectanglePolygon(100, 100, 100, 100));

        // Борис обвёл почти всё, оставив Анне полоску 0,3 м.
        Capture(map, Boris, T0.AddHours(1), RectanglePolygon(100.3, 50, 200, 200));

        Assert.Empty(LandOf(map, Anna));
        Assert.Equal(40_000 + 30, map.AreaOf(Boris), 3);
    }

    [Fact]
    public void Thin_wedge_at_a_tile_edge_gets_a_decision()
    {
        // Кусок петли в тайле — клин ~5 см шириной на высоте внутренней точки. Раньше грань считалась «вне петли»
        // из-за округления внутренней точки до сетки, и самопроверка отклоняла захват (seed 6DWzKFVPPhQo).
        var wedge = GeoOps.Factory.CreatePolygon(
        [
            new Coordinate(683993, 5774584.8), new Coordinate(683984.1, 5774574.9), new Coordinate(684000, 5774592.8),
            new Coordinate(684000, 5774587.8), new Coordinate(683993, 5774584.8),
        ]);
        var map = new TerritoryMap();

        var result = Capture(map, Anna, T0, wedge);

        Assert.Equal(wedge.Area, result.Area(PieceOutcome.ClaimedNeutral), 3);
        Assert.Equal(wedge.Area, map.AreaOf(Anna), 3);
    }

    [Fact]
    public void Same_history_gives_the_same_map()
    {
        TerritoryMap Play()
        {
            var map = new TerritoryMap();
            Capture(map, Anna, T0, RectanglePolygon(-150, -120, 260, 240));
            Capture(map, Boris, T0.AddHours(1), RectanglePolygon(-40, -300, 170, 350));
            Capture(map, Anna, T0.AddHours(30), RectanglePolygon(60, -60, 200, 100));
            return map;
        }

        Assert.Equal(Play().StateHash(), Play().StateHash());
    }

    /// <summary>
    /// Карта Анны для петли Бориса: L1-квадрат, L2-квадрат и L1-квадрат наполовину за краем петли 700 × 800 м
    /// (0,56 км² — больше порога 0,5 км²) или 700 × 700 м (0,49 км² — меньше).
    /// </summary>
    private static TerritoryMap AnnasLand()
    {
        var map = new TerritoryMap();
        Capture(map, Anna, T0, RectanglePolygon(100, 100, 100, 100));
        Capture(map, Anna, T0, RectanglePolygon(300, 100, 100, 100));
        Capture(map, Anna, T0.AddHours(21), RectanglePolygon(300, 100, 100, 100)); // визит через 21 ч → L2
        Capture(map, Anna, T0, RectanglePolygon(650, 300, 100, 100));
        return map;
    }

    private static CaptureResult BorisLoop(TerritoryMap map, Polygon loop)
    {
        var bigLoop = Gorodki.Domain.Config.GameConfig.Default.Territory.IsBigLoop(Gorodki.Domain.Leagues.League.Run, loop.Area);
        var result = map.Apply(loop, new CaptureContext(Boris, T0.AddHours(22), new HashSet<Guid>(), BigLoop: bigLoop));
        Assert.Empty(TerritoryInvariants.Check(map));
        return result;
    }

    [Fact]
    public void Big_loop_marks_enemy_land_inside_contested_and_takes_only_neutral_land()
    {
        var map = AnnasLand();
        var loop = RectanglePolygon(0, 0, 700, 800); // 0,56 км²

        var result = BorisLoop(map, loop);

        Assert.Equal(30_000, map.AreaOf(Anna), 3);
        Assert.Equal(loop.Area - 25_000, map.AreaOf(Boris), 3);
        Assert.Equal(25_000, result.Area(PieceOutcome.Contested), 3);
        Assert.Equal(0, result.Area(PieceOutcome.Transferred) + result.Area(PieceOutcome.Cracked));

        // Пометка — только на части внутри петли; уровни, щиты, осада и счётчики снятия — как были.
        var marked = LandOf(map, Anna).Where(p => p.State.ContestedUntil is not null).ToList();
        Assert.Equal(25_000, marked.Sum(p => p.Geometry.Area), 3);
        Assert.All(marked, p => Assert.Equal(T0.AddHours(46), p.State.ContestedUntil));
        Assert.Equal([1, 1, 2], marked.Select(p => p.State.Level).Order());
        Assert.All(LandOf(map, Anna), p => Assert.True(p.State is { SiegeUntil: null, ShieldUntil: null, LossWindowSince: null }));
        Assert.Equal(5_000, LandOf(map, Anna).Where(p => p.State.ContestedUntil is null).Sum(p => p.Geometry.Area), 3);

        // Пометка записана в журнал: откат возвращает землю без неё (так же её прячет публичная проекция).
        map.Restore(result.Changes);
        Assert.Equal(30_000, map.AreaOf(Anna), 3);
        Assert.All(map.Parcels, p => Assert.Null(p.State.ContestedUntil));
        Assert.Equal(0, map.AreaOf(Boris), 3);
    }

    [Fact]
    public void Loop_just_under_the_threshold_takes_and_cracks_as_before()
    {
        var map = AnnasLand();

        var result = BorisLoop(map, RectanglePolygon(0, 0, 700, 700)); // 0,49 км²

        Assert.Equal(0, result.Area(PieceOutcome.Contested));
        Assert.Equal(15_000, result.Area(PieceOutcome.Transferred), 3); // L1 и половина L1 у края
        Assert.Equal(10_000, result.Area(PieceOutcome.Cracked), 3);
        Assert.All(map.Parcels, p => Assert.Null(p.State.ContestedUntil));
    }
}
