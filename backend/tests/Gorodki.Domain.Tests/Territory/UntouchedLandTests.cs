using Gorodki.Domain.Geo;
using Gorodki.Domain.Territory;
using NetTopologySuite.Geometries;
using static Gorodki.Domain.Tests.Geo.TestGeometry;

namespace Gorodki.Domain.Tests.Territory;

/// <summary>
/// Захват и откат не переписывают землю, состояние которой не меняют (аудит BE-01, PLAN.md §3.16). Узлование ставит
/// вершину в каждую точку, где граница куска пересеклась с петлёй. Если такой кусок переписать, у него меняются номер
/// и контур, а у тайла — версия, и чужие видят скрытую петлю раньше 20 минут: по новым вершинам ровно там, где она прошла.
/// </summary>
public sealed class UntouchedLandTests
{
    private static readonly Guid Anna = new("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Boris = new("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid Vera = new("00000000-0000-0000-0000-00000000000c");
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly TileKey Tile = TileKey.Of(OriginX + 1, OriginY + 1);

    private static CaptureResult Capture(TerritoryMap map, CaptureContext context, Geometry area)
    {
        var result = map.Apply(area, context);
        Assert.Empty(TerritoryInvariants.Check(map));
        return result;
    }

    private static CaptureContext Veteran(Guid player, DateTimeOffset at) => new(player, at, new HashSet<Guid>());

    /// <summary>Аккаунт моложе 48 ч: ничью землю берёт, чужую не трогает (§3.3).</summary>
    private static CaptureContext Newcomer(DateTimeOffset at) => new(Boris, at, new HashSet<Guid>(), CanRemoveLevels: false);

    /// <summary>Анна и Вера — соседи с общей границей.</summary>
    private static TerritoryMap Neighbours(Polygon anna, Polygon vera)
    {
        var map = new TerritoryMap();
        Capture(map, Veteran(Anna, T0), anna);
        Capture(map, Veteran(Vera, T0), vera);
        return map;
    }

    /// <summary>Куски тайла с номерами — как их хранит сервер.</summary>
    private static List<(long Id, Parcel Parcel)> Stored(TerritoryMap map) =>
        map.ParcelsIn(Tile).Select((p, i) => ((long)(i + 1), p)).ToList();

    private static TerritoryMap Copy(TerritoryMap map)
    {
        var copy = new TerritoryMap(map.Rules, map.Slivers);
        copy.Load(map.Parcels);
        return copy;
    }

    /// <summary>Куски те же до вершины и в том же порядке вершин: от них считаются номера кусков у зрителя.</summary>
    private static void AssertSamePieces(IReadOnlyList<Parcel> expected, IReadOnlyList<Parcel> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var piece in expected)
        {
            Assert.Contains(actual, p => p.State == piece.State && p.Geometry.EqualsExact(piece.Geometry));
        }
    }

    private static void AssertNothingRewritten(TerritoryMap map, CaptureContext context, Polygon loop, PieceOutcome outcome)
    {
        var before = Stored(map);

        var result = Capture(map, context, loop);

        Assert.Equal(loop.Area, result.Area(outcome), 1); // петля правда прошла по чужой земле
        var diff = ParcelDiff.Compute(before, map.ParcelsIn(Tile));
        Assert.True(diff.IsEmpty, $"переписано кусков: {diff.Removed.Count}, новых: {diff.Added.Count}");
        AssertSamePieces([.. before.Select(b => b.Parcel)], map.ParcelsIn(Tile));
        Assert.Empty(result.Changes);
        Assert.Empty(result.ChangedTiles); // и версию тайла поднимать не за что
    }

    // ── Захват, который не меняет ни одного состояния ────────────────────────

    [Fact]
    public void New_account_loop_over_neighbours_land_rewrites_nothing()
    {
        // Опыт аудитора: раньше оба куска получали по две вершины на общей границе и переписывались с новыми номерами,
        // а версия тайла росла без записи в журнале — всем зрителям сразу.
        var map = Neighbours(RectanglePolygon(100, 100, 100, 100), RectanglePolygon(200, 100, 100, 100));

        AssertNothingRewritten(map, Newcomer(T0.AddHours(1)), RectanglePolygon(150, 120, 100, 60), PieceOutcome.NewAccountLimited);
    }

    [Fact]
    public void Late_loop_over_land_visited_since_rewrites_nothing()
    {
        // Петля из офлайна пришла позже визитов владельцев: чужие куски не тронуты (Superseded).
        var map = Neighbours(RectanglePolygon(100, 100, 100, 100), RectanglePolygon(200, 100, 100, 100));

        AssertNothingRewritten(map, Veteran(Boris, T0.AddHours(-1)), RectanglePolygon(150, 120, 100, 60), PieceOutcome.Superseded);
    }

    [Fact]
    public void Loop_across_a_slanted_border_rewrites_nothing_despite_snap_rounding()
    {
        // Точки пересечения с наклонной границей не лежат на сетке 0,1 м: snap-rounding сдвигает их и ломает
        // границу на сантиметры. Такой излом — уже не лишняя вершина на прямой, а другая геометрия.
        var annaLand = GeoOps.Factory.CreatePolygon([At(100, 100), At(300, 100), At(300, 170), At(100, 137), At(100, 100)]);
        var veraLand = GeoOps.Factory.CreatePolygon([At(100, 137), At(300, 170), At(300, 300), At(100, 300), At(100, 137)]);
        var map = Neighbours(annaLand, veraLand);

        AssertNothingRewritten(map, Newcomer(T0.AddHours(1)), RectanglePolygon(150, 110, 100, 140), PieceOutcome.NewAccountLimited);
    }

    // ── Захват, который меняет только часть тайла ────────────────────────────

    [Fact]
    public void Untouched_neighbour_of_newly_claimed_land_keeps_its_contour_and_id()
    {
        var map = new TerritoryMap();
        Capture(map, Veteran(Anna, T0), RectanglePolygon(100, 100, 100, 100));
        var before = Stored(map);

        // Новичок обвёл половину квадрата Анны и ничью землю рядом: ничья — его, квадрат Анны не тронут.
        var result = Capture(map, Newcomer(T0.AddHours(1)), RectanglePolygon(150, 120, 150, 60));

        Assert.Single(result.Changes);
        var diff = ParcelDiff.Compute(before, map.ParcelsIn(Tile));
        Assert.Equal([before.Single().Id], diff.Kept);
        Assert.Empty(diff.Removed);
        Assert.Equal(Boris, Assert.Single(diff.Added).State.OwnerId);
    }

    [Theory]
    [InlineData(400, 250)] // петля взяла ничью землю восточнее кусков
    [InlineData(250, 400)] // петля взяла ничью землю севернее кусков
    public void Loop_across_a_slanted_border_of_untouched_land_leaves_it_intact_when_it_claims_land_elsewhere(double east, double north)
    {
        // Петля новичка пересекла наклонную общую границу Анны и Веры (их землю не тронула) и взяла ничью землю рядом.
        // Точки пересечения не на сетке: если узловать нетронутую землю по петле, граница ломается на сантиметры,
        // и куски переписываются с новыми номерами, хотя с ними ничего не произошло.
        var annaLand = GeoOps.Factory.CreatePolygon([At(100, 100), At(300, 100), At(300, 170), At(100, 137), At(100, 100)]);
        var veraLand = GeoOps.Factory.CreatePolygon([At(100, 137), At(300, 170), At(300, 300), At(100, 300), At(100, 137)]);
        var map = Neighbours(annaLand, veraLand);
        var before = Stored(map);
        var copy = Copy(map);

        var result = Capture(map, Newcomer(T0.AddHours(1)), GeoOps.Factory.CreatePolygon(
            [At(150, 110), At(east, 110), At(east, north), At(150, north), At(150, 110)]));

        Assert.True(result.Area(PieceOutcome.ClaimedNeutral) > 0);
        var diff = ParcelDiff.Compute(before, map.ParcelsIn(Tile));
        Assert.Equal(before.Select(b => b.Id), diff.Kept);
        Assert.Equal(Boris, Assert.Single(diff.Added).State.OwnerId);
        var projection = Copy(map);
        projection.Restore(result.Changes);
        AssertSamePieces(copy.ParcelsIn(Tile), projection.ParcelsIn(Tile));
    }

    [Theory]
    [InlineData(false)] // новичок: квадрат Анны не тронут, взята только ничья земля
    [InlineData(true)] // бывалый: часть квадрата Анны перешла к Борису
    public void Public_projection_of_a_hidden_capture_shows_the_land_exactly_as_before(bool takesAnnasLand)
    {
        // Пока захват скрыт, зритель получает тайл, откаченный по журналу (TerritoryReader.ProjectAsync). Кусок Анны
        // должен прийти тем же до вершины: иначе у него другой номер и видны точки, где прошла петля.
        var map = new TerritoryMap();
        Capture(map, Veteran(Anna, T0), RectanglePolygon(100, 100, 100, 100));
        var before = Copy(map);
        var context = takesAnnasLand ? Veteran(Boris, T0.AddHours(1)) : Newcomer(T0.AddHours(1));
        var result = Capture(map, context, RectanglePolygon(150, 120, 150, 60));
        Assert.Equal(takesAnnasLand, result.Area(PieceOutcome.Transferred) > 0);

        var projection = Copy(map);
        projection.Restore(result.Changes);

        Assert.Empty(TerritoryInvariants.Check(projection));
        AssertSamePieces(before.ParcelsIn(Tile), projection.ParcelsIn(Tile));
    }

    [Fact]
    public void Stored_piece_is_kept_verbatim_even_if_its_vertices_are_not_canonical()
    {
        // Куски из базы, записанные до исправления, могут иметь лишние вершины и любой порядок обхода. Их тоже нельзя
        // «причёсывать» мимоходом: это та же перезапись с новым номером.
        var legacy = GeoOps.Factory.CreatePolygon(
            [At(100, 100), At(150, 100), At(200, 100), At(200, 160), At(200, 200), At(100, 200), At(100, 100)]);
        var anna = new Parcel(Tile, legacy, new ParcelState { OwnerId = Anna, Level = 1, LastVisitAt = T0, LastLevelUpAt = T0 });
        var map = new TerritoryMap();
        map.Load([anna]);

        var result = Capture(map, Newcomer(T0.AddHours(1)), RectanglePolygon(150, 120, 150, 60));
        var projection = Copy(map);
        projection.Restore(result.Changes);

        Assert.Contains(map.ParcelsIn(Tile), p => p.State == anna.State && p.Geometry.EqualsExact(legacy));
        AssertSamePieces([anna], projection.ParcelsIn(Tile));
    }

    // ── Откат ────────────────────────────────────────────────────────────────

    [Fact]
    public void Restore_that_returns_nothing_leaves_the_tile_as_it_was()
    {
        // Всё в следе с тех пор изменилось (например, зритель сам захватил это место) — откат ничего не возвращает
        // и не должен переузловать тайл по границе следа: на наклонной границе это изломы и новые номера.
        var annaLand = GeoOps.Factory.CreatePolygon([At(100, 100), At(300, 100), At(300, 170), At(100, 137), At(100, 100)]);
        var veraLand = GeoOps.Factory.CreatePolygon([At(100, 137), At(300, 170), At(300, 300), At(100, 300), At(100, 137)]);
        var map = Neighbours(annaLand, veraLand);
        var before = Stored(map);
        var footprint = RectanglePolygon(150, 110, 100, 140);
        var borisState = new ParcelState { OwnerId = Boris, Level = 1, LastVisitAt = T0, LastLevelUpAt = T0 };
        var change = new TileChange(Tile, footprint, [], [new JournalPiece(footprint, borisState)]);

        var result = map.Restore([change]);

        Assert.Equal(footprint.Area, result.SkippedArea, 1);
        Assert.True(ParcelDiff.Compute(before, map.ParcelsIn(Tile)).IsEmpty);
        AssertSamePieces([.. before.Select(b => b.Parcel)], map.ParcelsIn(Tile));
        Assert.Empty(result.ChangedTiles);
    }
}
