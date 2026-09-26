using Gorodki.Domain.Fog;
using Gorodki.Domain.Osm;

namespace Gorodki.Domain.Tests.Osm;

/// <summary>
/// «% Бреста» = popcount(explored &amp; reachable) / popcount(reachable) (PLAN.md, §7.3; osm-pipeline.md, «Проверка»,
/// «Сервер»): пустой туман — 0 %, туман, равный «достижимому», — 100 %, открытое вне «достижимого» процент не меняет,
/// «Всего» ≤ 100 %.
/// </summary>
public sealed class ReachableAreaTests
{
    private static readonly FogTileKey A = new(9_400, 5_400);
    private static readonly FogTileKey B = new(9_401, 5_400);
    private static readonly FogTileKey Outside = new(9_500, 5_500);

    private static FogTileBits Bits(params int[] set)
    {
        var bits = new FogTileBits();
        foreach (var bit in set)
        {
            bits.Set(bit);
        }

        return bits;
    }

    private static ReachableArea Reachable() => new(new Dictionary<FogTileKey, FogTileBits>
    {
        [A] = Bits(0, 1, 2, 3),
        [B] = Bits(100, 200, 300, 400, 500, 600),
    });

    [Fact]
    public void Empty_fog_is_zero_percent()
    {
        var share = Reachable().ShareOf(new Dictionary<FogTileKey, FogTileBits>());

        Assert.Equal(new ExploredShare(0, 10), share);
        Assert.Equal(0, share.Percent);
    }

    [Fact]
    public void Fog_equal_to_the_reachable_area_is_one_hundred_percent()
    {
        var reachable = Reachable();

        Assert.Equal(100, reachable.ShareOf(reachable.Tiles).Percent);
    }

    [Fact]
    public void Explored_cells_outside_the_reachable_area_do_not_count()
    {
        var explored = new Dictionary<FogTileKey, FogTileBits>
        {
            [A] = Bits(0, 1, 60_000), // клетка 60 000 — двор без дорожек: гектары есть, процента нет
            [Outside] = Bits(5, 6, 7), // пригород
        };

        var share = Reachable().ShareOf(explored);

        Assert.Equal(new ExploredShare(2, 10), share);
        Assert.Equal(20, share.Percent, 9);
    }

    [Fact]
    public void Total_of_both_layers_is_their_union_not_their_sum()
    {
        var foot = new Dictionary<FogTileKey, FogTileBits> { [A] = Bits(0, 1, 2, 3), [B] = Bits(100, 200, 300) };
        var bike = new Dictionary<FogTileKey, FogTileBits> { [A] = Bits(0, 1, 2, 3), [B] = Bits(100, 200, 300, 400, 500, 600) };
        var reachable = Reachable();

        var total = reachable.ShareOfUnion([foot, bike]);

        Assert.Equal(70, reachable.ShareOf(foot).Percent, 9);
        Assert.Equal(100, reachable.ShareOf(bike).Percent, 9);
        Assert.Equal(100, total.Percent, 9); // сумма дала бы 170 %
        Assert.Equal(4, foot[A].CountAnd(bike[A]));
    }

    [Fact]
    public void No_reachable_area_means_zero_not_division_by_zero()
    {
        var empty = new ReachableArea([]);

        Assert.Equal(0, empty.ShareOf(new Dictionary<FogTileKey, FogTileBits> { [A] = Bits(1) }).Percent);
    }

    [Fact]
    public void Same_tile_twice_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new ReachableArea([KeyValuePair.Create(A, Bits(1)), KeyValuePair.Create(A, Bits(2))]));
    }

    [Theory]
    [InlineData(MaskKind.Water, "water")]
    [InlineData(MaskKind.MajorRoad, "major_road")]
    [InlineData(MaskKind.Outside, "outside")]
    public void Mask_kinds_have_stable_codes(MaskKind kind, string code)
    {
        Assert.Equal(code, OsmCodes.Of(kind));
        Assert.Equal(kind, OsmCodes.MaskKindOf(code));
    }

    [Fact]
    public void Numbers_of_kinds_stored_in_the_database_do_not_change()
    {
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], Enum.GetValues<MaskKind>().Select(k => (int)k));
        Assert.Equal([1, 2, 3, 4], Enum.GetValues<DistrictKind>().Select(k => (int)k));
        Assert.Equal([0, 1, 2], Enum.GetValues<PlayZone>().Select(k => (int)k));
        Assert.Throws<FormatException>(() => OsmCodes.MaskKindOf("lava"));
    }
}
