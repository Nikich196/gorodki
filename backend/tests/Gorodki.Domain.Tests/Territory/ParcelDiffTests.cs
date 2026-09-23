using Gorodki.Domain.Geo;
using Gorodki.Domain.Territory;
using static Gorodki.Domain.Tests.Geo.TestGeometry;

namespace Gorodki.Domain.Tests.Territory;

public sealed class ParcelDiffTests
{
    private static readonly Guid Anna = new("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Boris = new("00000000-0000-0000-0000-00000000000b");
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Untouched_pieces_keep_their_ids_and_only_the_change_is_written()
    {
        var map = new TerritoryMap();
        map.Apply(RectanglePolygon(100, 100, 100, 100), new CaptureContext(Anna, T0, new HashSet<Guid>()));
        map.Apply(RectanglePolygon(400, 400, 100, 100), new CaptureContext(Anna, T0, new HashSet<Guid>()));
        var tile = map.Tiles.Single();
        var before = map.ParcelsIn(tile).Select((p, i) => ((long)(i + 1), p)).ToList();

        // Борис берёт половину первого квадрата Анны; второй квадрат не тронут.
        map.Apply(RectanglePolygon(150, 100, 100, 100), new CaptureContext(Boris, T0.AddHours(1), new HashSet<Guid>()));
        var diff = ParcelDiff.Compute(before, map.ParcelsIn(tile));

        var untouched = before.Single(b => b.Item2.Geometry.EnvelopeInternal.MinX > OriginX + 300).Item1;
        Assert.Equal(new[] { untouched }, diff.Kept);
        Assert.Single(diff.Removed);
        Assert.Equal(map.ParcelsIn(tile).Count - 1, diff.Added.Count);
    }

    [Fact]
    public void Same_pieces_give_an_empty_diff_even_if_vertices_start_elsewhere()
    {
        var piece = new Parcel(
            TileKey.Of(OriginX, OriginY),
            RectanglePolygon(100, 100, 50, 50),
            new ParcelState { OwnerId = Anna, Level = 1, LastVisitAt = T0, LastLevelUpAt = T0 });
        var reordered = piece with { Geometry = (NetTopologySuite.Geometries.Polygon)piece.Geometry.Reverse() };

        var diff = ParcelDiff.Compute([(7, piece)], [reordered]);

        Assert.True(diff.IsEmpty);
        Assert.Equal(new[] { 7L }, diff.Kept);
    }

    [Fact]
    public void Changed_state_is_a_change_even_with_the_same_shape()
    {
        var piece = new Parcel(
            TileKey.Of(OriginX, OriginY),
            RectanglePolygon(100, 100, 50, 50),
            new ParcelState { OwnerId = Anna, Level = 1, LastVisitAt = T0, LastLevelUpAt = T0 });

        var diff = ParcelDiff.Compute([(7, piece)], [piece with { State = piece.State with { Level = 2 } }]);

        Assert.Equal(new[] { 7L }, diff.Removed);
        Assert.Single(diff.Added);
    }
}
