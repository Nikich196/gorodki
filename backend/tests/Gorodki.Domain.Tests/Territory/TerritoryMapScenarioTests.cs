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
}
