using Gorodki.Domain.Geo;
using Gorodki.Domain.Territory;
using NetTopologySuite.Geometries;
using static Gorodki.Domain.Tests.Geo.TestGeometry;

namespace Gorodki.Domain.Tests.Territory;

/// <summary>
/// Точный откат скрытого захвата в публичной проекции (аудит BE-01, PLAN.md §3.16): пока захват скрыт, зритель получает
/// землю до него — те же строки хранилища до вершины, с теми же номерами и состоянием целиком, а визиты владельцев,
/// засчитанные за это время, — перенесёнными на прежние куски. Эталон — то же хранилище без скрытого захвата
/// (<see cref="FakeTerritoryStore.Clone"/> до захвата, те же визиты по номерам строк).
/// </summary>
public sealed class ExactUndoTests
{
    private static readonly Guid Anna = new("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Boris = new("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid Vera = new("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid Gleb = new("00000000-0000-0000-0000-00000000000d");
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly TileKey Tile = TileKey.Of(OriginX + 1, OriginY + 1);

    private static FakeTerritoryStore.Journaled Capture(FakeTerritoryStore store, Guid player, DateTimeOffset at, Polygon area)
    {
        var entry = store.Capture(area, new CaptureContext(player, at, new HashSet<Guid>()));
        Assert.Empty(TerritoryInvariants.Check(MapOf(store.Rows.Select(r => r.Parcel))));
        return entry;
    }

    private static TerritoryMap MapOf(IEnumerable<Parcel> parcels)
    {
        var map = new TerritoryMap();
        map.Load(parcels);
        return map;
    }

    /// <summary>Квадрат Анны 200 × 200 м уровня 2 (повышение в T0 + 21 ч).</summary>
    private static FakeTerritoryStore LandOfAnnaAtLevelTwo()
    {
        var store = new FakeTerritoryStore();
        Capture(store, Anna, T0, RectanglePolygon(100, 100, 200, 200));
        Capture(store, Anna, T0.AddHours(21), RectanglePolygon(100, 100, 200, 200));
        Assert.Equal(2, Assert.Single(store.Rows).Parcel.State.Level);
        return store;
    }

    /// <summary>
    /// Проекция — ровно эти строки: номер, состояние целиком и контур до вершины и порядка обхода (от них зависит номер
    /// куска у зрителя), и больше никаких.
    /// </summary>
    internal static void AssertSameRows(IReadOnlyList<(long Id, Parcel Parcel)> expected, IReadOnlyList<ProjectedParcel> actual)
    {
        Assert.True(
            expected.Count == actual.Count,
            $"строк {actual.Count}, а ждали {expected.Count}: {string.Join("; ", actual.Select(p => $"{p.Id} {p.Parcel.State}"))}");
        foreach (var (id, parcel) in expected)
        {
            Assert.True(
                actual.Any(p => p.Id == id && p.Parcel.State == parcel.State && p.Parcel.Geometry.EqualsExact(parcel.Geometry)),
                $"нет строки {id} {parcel.State} {parcel.Geometry}; есть: {string.Join("; ", actual.Select(p => $"{p.Id} {p.Parcel.State} {p.Parcel.Geometry}"))}");
        }
    }

    private static long IdOf(FakeTerritoryStore store, Func<ParcelState, bool> which) =>
        Assert.Single(store.Rows, r => which(r.Parcel.State)).Id;

    // ── Визиты, засчитанные, пока захват скрыт ───────────────────────────────

    [Fact]
    public void Victims_run_over_both_parts_of_a_cracked_piece_comes_back_as_one_piece()
    {
        // Обычная игра (territory-map.md): Анна (L2) пробежала по своему квадрату, а через пару минут Борис треснул его
        // правую половину; визиты засчитаны, когда публичен конец её забега, — захват ещё скрыт. У каждой части своё время
        // визита (последний шаг пути в ней). Откат по граням оставлял шов по линии петли; точный — один кусок, та же строка.
        var store = LandOfAnnaAtLevelTwo();
        var whole = Assert.Single(store.Rows);
        var withoutCapture = store.Clone();
        var capturedAt = T0.AddHours(42);
        var hidden = Capture(store, Boris, capturedAt, RectanglePolygon(200, 90, 200, 220));
        var cracked = IdOf(store, s => s.SiegeUntil is not null);
        var rest = IdOf(store, s => s.OwnerId == Anna && s.SiegeUntil is null);

        Assert.Equal([rest], store.Visit([rest], capturedAt.AddMinutes(-7)));
        Assert.Equal([cracked], store.Visit([cracked], capturedAt.AddMinutes(-6)));
        withoutCapture.Visit([whole.Id], capturedAt.AddMinutes(-6)); // без захвата — один кусок, визит по последнему шагу в нём

        var projection = store.Project(Tile, [hidden]);

        Assert.Equal([UndoPath.Exact], projection.Paths);
        var expected = Assert.Single(withoutCapture.Rows);
        var seen = Assert.Single(projection.Pieces);
        Assert.Equal(whole.Id, seen.Id);
        Assert.True(seen.Parcel.Geometry.EqualsExact(whole.Parcel.Geometry));

        // Оба визита легли на целый кусок по порядку: уровень вырос в −7 мин, последний визит — в −6. Всё, что видит зритель
        // (уровень, время визита, щит, осада), — как без захвата; время повышения уровня (его зритель не видит) раньше на
        // минуту: одним визитом или двумя пробежан кусок, по временам не отличить (остаток в territory-map.md).
        var replayed = VisitReplay.Apply(whole.Parcel.State, [capturedAt.AddMinutes(-7), capturedAt.AddMinutes(-6)], store.Rules);
        Assert.Equal(replayed, seen.Parcel.State);
        Assert.Equal(expected.Parcel.State with { LastLevelUpAt = capturedAt.AddMinutes(-7) }, seen.Parcel.State);
        Assert.Equal((3, capturedAt.AddMinutes(-6)), (seen.Parcel.State.Level, seen.Parcel.State.LastVisitAt));
    }

    [Fact]
    public void Victims_visit_to_the_cracked_part_only_is_replayed_on_the_whole_piece()
    {
        // Анна пробежала только по треснувшей части (по остатку меньше 50 м). Откат по граням давал ступеньку: L3 на
        // треснувшей части у L2 на остатке. Точный — целый кусок с этим визитом, в точности как без захвата.
        var store = LandOfAnnaAtLevelTwo();
        var whole = Assert.Single(store.Rows);
        var withoutCapture = store.Clone();
        var capturedAt = T0.AddHours(42);
        var hidden = Capture(store, Boris, capturedAt, RectanglePolygon(200, 90, 200, 220));
        var cracked = IdOf(store, s => s.SiegeUntil is not null);
        var at = capturedAt.AddMinutes(-6);
        store.Visit([cracked], at);
        withoutCapture.Visit([whole.Id], at);

        var projection = store.Project(Tile, [hidden]);

        Assert.Equal([UndoPath.Exact], projection.Paths);
        AssertSameRows(withoutCapture.Rows, projection.Pieces);
        Assert.Equal(3, Assert.Single(projection.Pieces).Parcel.State.Level);

        // Запасной путь (откат по граням) здесь даёт ступеньку уровня по линии петли — поэтому и нужен точный.
        var fallback = ExactUndo.Restore(Tile, [.. store.RowsIn(Tile).Select(r => new ProjectedParcel(r.Id, r.Parcel))], hidden.Changes[Tile], store.Rules, new SliverSettings());
        Assert.Equal([2, 3], fallback.Select(p => p.Parcel.State.Level).Order());
    }

    [Fact]
    public void Visit_within_the_same_hour_and_level_is_replayed_in_full()
    {
        // Уровень Анны уже 3, а визит — в тот же час, что и прежний: у зрителя (время чужого визита — до часа) номер куска
        // был бы тем же и без визита. Но состояние — целиком: иначе после раскрытия и в следующих откатах визит потерян.
        var store = new FakeTerritoryStore();
        Capture(store, Anna, T0, RectanglePolygon(100, 100, 200, 200));
        Capture(store, Anna, T0.AddHours(21), RectanglePolygon(100, 100, 200, 200));
        Capture(store, Anna, T0.AddHours(42), RectanglePolygon(100, 100, 200, 200));
        var whole = Assert.Single(store.Rows);
        Assert.Equal(3, whole.Parcel.State.Level);
        var withoutCapture = store.Clone();
        var hidden = Capture(store, Boris, T0.AddHours(42).AddMinutes(10), RectanglePolygon(200, 90, 200, 220));
        var at = T0.AddHours(42).AddMinutes(20);
        store.Visit([IdOf(store, s => s.SiegeUntil is not null)], at);
        withoutCapture.Visit([whole.Id], at);

        var projection = store.Project(Tile, [hidden]);

        Assert.Equal([UndoPath.Exact], projection.Paths);
        AssertSameRows(withoutCapture.Rows, projection.Pieces);
        Assert.Equal(at, Assert.Single(projection.Pieces).Parcel.State.LastVisitAt);
        Assert.Equal(whole.Parcel.State.LastVisitAt.ToUnixTimeMilliseconds() / 3_600_000, at.ToUnixTimeMilliseconds() / 3_600_000);
    }

    [Fact]
    public void Authors_visits_to_the_land_he_took_are_not_replayed_on_the_victims_piece()
    {
        // «Проблема 2»: забег Бориса из офлайна продолжился по взятому, визиты засчитаны, пока захват скрыт. В мире без
        // захвата эта земля — Анны (и ничья), визитов Бориса на ней нет: визит другого владельца на прежний кусок не ложится.
        var store = new FakeTerritoryStore();
        Capture(store, Anna, T0, RectanglePolygon(100, 100, 200, 200));
        var withoutCapture = store.Clone();
        var hidden = Capture(store, Boris, T0.AddHours(1), RectanglePolygon(200, 150, 200, 100));
        Assert.NotEmpty(store.VisitAll(Boris, T0.AddHours(1).AddMinutes(10)));

        var projection = store.Project(Tile, [hidden]);

        Assert.Equal([UndoPath.Exact], projection.Paths);
        AssertSameRows(withoutCapture.Rows, projection.Pieces);
    }

    [Fact]
    public void Visit_to_a_neighbouring_piece_of_the_same_owner_is_not_replayed_on_the_other_one()
    {
        // У Анны два соседних куска разного состояния; петля Бориса треснула часть одного (L2) и забрала часть другого (L1).
        // Анна пробежала только по остатку второго. Визит ложится только на второй: рамки остатка второго и первого куска
        // пересекаются (общая граница), но остаток над первым не лежит.
        var store = new FakeTerritoryStore();
        Capture(store, Anna, T0, RectanglePolygon(100, 100, 100, 200));
        Capture(store, Anna, T0.AddHours(21), RectanglePolygon(100, 100, 100, 200)); // левый — L2
        Capture(store, Anna, T0.AddHours(22), RectanglePolygon(200, 100, 100, 200)); // правый — L1, другое время
        Assert.Equal(2, store.Rows.Count);
        var withoutCapture = store.Clone();
        var right = IdOf(withoutCapture, s => s.Level == 1);
        var hidden = Capture(store, Boris, T0.AddHours(43), RectanglePolygon(150, 150, 100, 100));
        Assert.Contains(store.Rows, r => r.Parcel.State.SiegeUntil is not null); // левый треснул
        var rightRest = IdOf(store, s => s.OwnerId == Anna && s.Level == 1 && s.SiegeUntil is null); // треснувшая часть — тоже L1
        var at = T0.AddHours(43).AddMinutes(5);
        store.Visit([rightRest], at);
        withoutCapture.Visit([right], at);

        var projection = store.Project(Tile, [hidden]);

        Assert.Equal([UndoPath.Exact], projection.Paths);
        AssertSameRows(withoutCapture.Rows, projection.Pieces);
    }

    [Fact]
    public void Visit_to_a_piece_that_merged_two_replaced_pieces_is_replayed_on_both()
    {
        // У Анны два соседних куска разного состояния (разное время визита). Её скрытая петля освежила часть обоих — и
        // освежённые части получили одно состояние: один вставленный кусок поверх двух удалённых. Его внутренняя точка
        // лежит на их общей границе, а внутренние точки удалённых — на его границе: по точкам он не лежит ни над одним. Визит
        // Анны на нём ложится на оба (какой из них она пробежала, по строкам не узнать — остаток в territory-map.md).
        var left = new ParcelState { OwnerId = Anna, Level = 1, LastVisitAt = T0, LastLevelUpAt = T0 };
        var right = left with { LastVisitAt = T0.AddHours(1) };
        var store = new FakeTerritoryStore();
        var leftId = store.Seed(new Parcel(Tile, RectanglePolygon(100, 100, 100, 100), left));
        var rightId = store.Seed(new Parcel(Tile, RectanglePolygon(200, 100, 100, 100), right));
        var before = store.RowsIn(Tile);
        var refreshedAt = T0.AddHours(10);
        var hidden = Capture(store, Anna, refreshedAt, RectanglePolygon(150, 150, 100, 100));
        var merged = IdOf(store, s => s.LastVisitAt == refreshedAt && s.LastLevelUpAt == T0);
        Assert.Equal(5_000, store.Rows.Single(r => r.Id == merged).Parcel.Geometry.Area, 1); // одна освежённая часть на оба
        var at = refreshedAt.AddMinutes(5);
        Assert.Equal([merged], store.Visit([merged], at));

        var projection = store.Project(Tile, [hidden]);

        Assert.Equal([UndoPath.Exact], projection.Paths);
        AssertSameRows(
            [
                (leftId, before[0].Parcel with { State = CaptureRules.Visit(left, at, store.Rules)! }),
                (rightId, before[1].Parcel with { State = CaptureRules.Visit(right, at, store.Rules)! }),
            ],
            projection.Pieces);
    }

    [Fact]
    public void Neighbours_visit_to_his_rewritten_piece_is_replayed_on_the_original()
    {
        // Новичок взял ничью землю за наклонным краем куска Анны, её землю не тронул, но переписал её кусок с изломом
        // snap-rounding (то же состояние, другой контур, новая строка). Анна пробежала по нему, пока захват скрыт. Прежний
        // кусок возвращается до вершины, с прежним номером и с этим визитом.
        var store = new FakeTerritoryStore();
        Capture(store, Anna, T0, GeoOps.Factory.CreatePolygon([At(100, 100), At(300, 100), At(300, 170), At(100, 137), At(100, 100)]));
        var original = Assert.Single(store.Rows).Id;
        var withoutCapture = store.Clone();
        var hidden = store.Capture(
            RectanglePolygon(150, 110, 100, 140), new CaptureContext(Boris, T0.AddHours(1), new HashSet<Guid>(), CanRemoveLevels: false));
        Assert.DoesNotContain(store.Rows, r => r.Id == original); // кусок Анны переписан
        Assert.Contains(store.Rows, r => r.Parcel.State == withoutCapture.Rows.Single().Parcel.State); // с тем же состоянием
        var at = T0.AddHours(1).AddMinutes(10);
        store.VisitAll(Anna, at);
        withoutCapture.VisitAll(Anna, at);

        var projection = store.Project(Tile, [hidden]);

        Assert.Equal([UndoPath.Exact], projection.Paths);
        AssertSameRows(withoutCapture.Rows, projection.Pieces);
    }

    // ── Захваты друг на друге и разные зрители ───────────────────────────────

    /// <summary>
    /// Анна и Вера — соседи с наклонной границей; скрытый захват Бориса взял часть земли Анны и ничью, следом скрытый
    /// захват Глеба — часть остатка Анны (его вставил захват Бориса) и часть земли Веры.
    /// </summary>
    private static (FakeTerritoryStore Store, IReadOnlyList<(long, Parcel)> Before, IReadOnlyList<(long, Parcel)> AfterBoris,
        FakeTerritoryStore.Journaled ByBoris, FakeTerritoryStore.Journaled ByGleb) Stacked()
    {
        var store = new FakeTerritoryStore();
        Capture(store, Anna, T0, GeoOps.Factory.CreatePolygon([At(100, 100), At(300, 100), At(300, 170), At(100, 137), At(100, 100)]));
        Capture(store, Vera, T0, GeoOps.Factory.CreatePolygon([At(100, 137), At(300, 170), At(300, 300), At(100, 300), At(100, 137)]));
        var before = store.RowsIn(Tile);
        var byBoris = Capture(store, Boris, T0.AddHours(1), RectanglePolygon(250, 90, 150, 60));
        var afterBoris = store.RowsIn(Tile);
        var byGleb = Capture(store, Gleb, T0.AddHours(1).AddMinutes(5), RectanglePolygon(120, 110, 60, 120));
        Assert.Contains(byGleb.Swaps[Tile]!.Replaced, r => byBoris.Swaps[Tile]!.Written.Any(w => w.ParcelId == r.ParcelId));
        return (store, before, afterBoris, byBoris, byGleb);
    }

    [Fact]
    public void Stacked_hidden_captures_are_undone_exactly_for_a_third_viewer()
    {
        // Сначала откатывается захват Глеба: он возвращает остаток Анны с тем же номером строки — по нему точно
        // откатывается и захват Бориса.
        var (store, before, _, byBoris, byGleb) = Stacked();

        var projection = store.Project(Tile, [byBoris, byGleb]);

        Assert.Equal([UndoPath.Exact, UndoPath.Exact], projection.Paths);
        AssertSameRows(before, projection.Pieces);
    }

    [Fact]
    public void Earlier_author_sees_the_land_exactly_as_right_after_his_capture()
    {
        var (store, _, afterBoris, _, byGleb) = Stacked();

        var projection = store.Project(Tile, [byGleb]);

        Assert.Equal([UndoPath.Exact], projection.Paths);
        AssertSameRows(afterBoris, projection.Pieces);
    }

    [Fact]
    public void Later_author_keeps_his_land_and_the_earlier_capture_falls_back()
    {
        // Захват Глеба заменил кусок, вставленный захватом Бориса: для Глеба захват Бориса точно не откатить (его строки
        // нет) — запасной путь. Своя земля Глеба при этом не трогается, карта без наложений.
        var (store, _, _, byBoris, _) = Stacked();

        var projection = store.Project(Tile, [byBoris]);

        Assert.Equal([UndoPath.Missing], projection.Paths);
        var map = MapOf(projection.Pieces.Select(p => p.Parcel));
        Assert.Empty(TerritoryInvariants.Check(map));
        var own = GeoOps.UnionAll(store.Rows.Where(r => r.Parcel.State.OwnerId == Gleb).Select(r => (Geometry)r.Parcel.Geometry));
        Assert.Equal(own.Area, map.AreaOf(Gleb), 1);
        Assert.Equal(0, map.AreaOf(Boris), 1);
    }

    // ── Запасной путь ────────────────────────────────────────────────────────

    [Fact]
    public void Legacy_piece_replaced_by_a_hidden_capture_comes_back_verbatim()
    {
        // Кусок, записанный до исправления BE-01: лишние вершины и неканонический порядок обхода. Откат по граням собрал бы
        // его заново в каноническом виде — с другим номером у зрителя. Точный откат возвращает строку как она была.
        var legacy = GeoOps.Factory.CreatePolygon(
            [At(200, 200), At(100, 200), At(100, 100), At(150, 100), At(200, 100), At(200, 160), At(200, 200)]);
        Assert.False(legacy.EqualsExact(legacy.Normalized()));
        var store = new FakeTerritoryStore();
        var id = store.Seed(new Parcel(Tile, legacy, new ParcelState { OwnerId = Anna, Level = 1, LastVisitAt = T0, LastLevelUpAt = T0 }));
        var before = store.RowsIn(Tile);
        var hidden = Capture(store, Boris, T0.AddHours(1), RectanglePolygon(150, 120, 150, 60));

        var projection = store.Project(Tile, [hidden]);

        Assert.Equal([UndoPath.Exact], projection.Paths);
        AssertSameRows(before, projection.Pieces);
        Assert.Equal(id, Assert.Single(projection.Pieces).Id);
    }

    [Fact]
    public void Corrupt_row_falls_back_to_the_footprint_restore()
    {
        // Испорченная строка точного отката (ручная правка базы): проекция не падает и не отдаёт пустой тайл — идёт
        // запасным путём, как без строк.
        var store = new FakeTerritoryStore();
        Capture(store, Anna, T0, RectanglePolygon(100, 100, 200, 200));
        var hidden = Capture(store, Boris, T0.AddHours(1), RectanglePolygon(200, 150, 200, 100));
        var swap = hidden.Swaps[Tile]!;
        hidden.Swaps[Tile] = swap with { Replaced = [.. swap.Replaced.Select(r => r with { Geometry = [0x03, 0x00, 0x05] })] };
        var stored = store.RowsIn(Tile).Select(r => new ProjectedParcel(r.Id, r.Parcel)).ToList();

        var projection = store.Project(Tile, [hidden]);

        Assert.Equal([UndoPath.Exception], projection.Paths);
        Assert.IsType<FormatException>(Assert.Single(projection.Errors));
        var fallback = ExactUndo.Restore(Tile, stored, hidden.Changes[Tile], store.Rules, new SliverSettings());
        Assert.Equal(fallback.Count, projection.Pieces.Count);
        Assert.All(fallback, f => Assert.Contains(projection.Pieces, p => p.Parcel.State == f.Parcel.State && p.Parcel.Geometry.EqualsExact(f.Parcel.Geometry)));
        Assert.Equal(40_000, MapOf(projection.Pieces.Select(p => p.Parcel)).AreaOf(Anna), 1);
    }

    [Fact]
    public void Written_piece_changed_by_more_than_visits_falls_back()
    {
        var store = new FakeTerritoryStore();
        Capture(store, Anna, T0, RectanglePolygon(100, 100, 200, 200));
        Capture(store, Anna, T0.AddHours(21), RectanglePolygon(100, 100, 200, 200));
        var hidden = Capture(store, Boris, T0.AddHours(42), RectanglePolygon(200, 90, 200, 220));
        var current = store.RowsIn(Tile)
            .Select(r => new ProjectedParcel(r.Id, r.Parcel.State.SiegeUntil is { } siege ? r.Parcel with { State = r.Parcel.State with { SiegeUntil = siege.AddHours(1) } } : r.Parcel))
            .ToList();

        var (pieces, path, error) = ExactUndo.TryUndo(current, hidden.Swaps[Tile]!, store.Rules);

        Assert.Equal((null, UndoPath.Changed, null), (pieces, path, error));
    }

    [Fact]
    public void Returned_piece_that_would_overlap_remaining_land_falls_back()
    {
        // Строки журнала не сходятся с хранилищем (прежний кусок налез бы на кусок, которого захват не касался): точный откат
        // дал бы наложение — запасной путь.
        var anna = new ParcelState { OwnerId = Anna, Level = 1, LastVisitAt = T0, LastLevelUpAt = T0 };
        var vera = anna with { OwnerId = Vera };
        var current = new List<ProjectedParcel> { new(1, new Parcel(Tile, RectanglePolygon(100, 100, 100, 100), vera)) };
        var swap = new ParcelSwap(Tile, [new JournalParcel(2, anna, ParcelSwap.Encode(RectanglePolygon(150, 100, 100, 100)))], []);
        var footprint = RectanglePolygon(200, 100, 50, 100);
        var change = new TileChange(Tile, footprint, [new JournalPiece(footprint, anna)], []);

        var projection = ExactUndo.Project(Tile, current, [new HiddenTileChange(swap, () => change)], new TerritoryRules(), new SliverSettings());

        Assert.Equal([UndoPath.Overlap], projection.Paths);
        Assert.Empty(TerritoryInvariants.Check(MapOf(projection.Pieces.Select(p => p.Parcel))));
        Assert.Equal(1, projection.Pieces.Single(p => p.Parcel.State == vera).Id); // нетронутый кусок — тот же объект
    }

    [Theory]
    [InlineData(0.03)] // вершина не на сетке — TWKB её не запишет
    [InlineData(0.00000004)] // «почти на сетке» (в допуске IsOnGrid): TWKB прочтёт ровно k/10 — другой контур
    public void Contour_that_does_not_round_trip_through_twkb_gets_no_rows_and_falls_back(double offset)
    {
        var off = GeoOps.Factory.CreatePolygon([At(100, 100), At(200 + offset, 100), At(200, 200), At(100, 200), At(100, 100)]);
        var store = new FakeTerritoryStore();
        store.Seed(new Parcel(Tile, off, new ParcelState { OwnerId = Anna, Level = 1, LastVisitAt = T0, LastLevelUpAt = T0 }));

        var hidden = store.Capture(RectanglePolygon(150, 120, 150, 60), new CaptureContext(Boris, T0.AddHours(1), new HashSet<Guid>()));

        Assert.Null(hidden.Swaps[Tile]);
        Assert.Equal([UndoPath.NoRows], store.Project(Tile, [hidden]).Paths);
    }

    [Fact]
    public void Swap_needs_the_ids_the_store_gave_to_the_new_pieces()
    {
        // Номера новых строк известны только после сохранения; без них (нули) точный откат не найдёт куски по номерам.
        var store = new FakeTerritoryStore();
        Capture(store, Anna, T0, RectanglePolygon(100, 100, 200, 200));
        var before = store.RowsIn(Tile);
        var map = MapOf(before.Select(r => r.Parcel));
        map.Apply(RectanglePolygon(200, 150, 200, 100), new CaptureContext(Boris, T0.AddHours(1), new HashSet<Guid>()));
        var diff = ParcelDiff.Compute(before, map.ParcelsIn(Tile));

        Assert.Throws<ArgumentException>(() => diff.Swap(Tile, before, []));
        var swap = diff.Swap(Tile, before, [.. diff.Added.Select((_, i) => 100L + i)])!;
        Assert.Equal(before.Select(b => b.Id), swap.Replaced.Select(r => r.ParcelId));
        Assert.Equal(diff.Added.Select((_, i) => 100L + i), swap.Written.Select(w => w.ParcelId));
        Assert.Equal(diff.Added.Select(p => p.State), swap.Written.Select(w => w.State));
        Assert.All(swap.Written, w => Assert.Null(w.Geometry));
        Assert.True(Twkb.Read(Assert.Single(swap.Replaced).Geometry!).EqualsExact(Assert.Single(before).Parcel.Geometry));
    }

    // ── Удаление аккаунта, пока захват скрыт ─────────────────────────────────

    [Fact]
    public void Deleted_non_author_is_scrubbed_on_both_sides_and_the_undo_stays_exact()
    {
        // Вера раньше сняла уровень с куска Анны (её номер — в списке снявших), потом скрытый захват Бориса треснул его
        // ещё раз. Аккаунт Веры стёрт, пока захват скрыт: её номер уходит и из кусков, и из строк журнала, её земля —
        // и из хранилища, и из строк. Проекция — как хранилище без захвата с тем же удалением.
        var store = new FakeTerritoryStore();
        Capture(store, Anna, T0, RectanglePolygon(100, 100, 200, 200));
        Capture(store, Anna, T0.AddHours(21), RectanglePolygon(100, 100, 200, 200));
        Capture(store, Anna, T0.AddHours(42), RectanglePolygon(100, 100, 200, 200)); // L3
        Capture(store, Vera, T0.AddHours(43), RectanglePolygon(100, 100, 200, 200)); // трещина: L2, Вера в списке
        Capture(store, Vera, T0.AddHours(43), RectanglePolygon(400, 100, 100, 100)); // и своя земля рядом
        var withoutCapture = store.Clone();
        var hidden = Capture(store, Boris, T0.AddHours(44), RectanglePolygon(200, 90, 250, 220));
        Assert.Contains(store.Rows, r => r.Parcel.State.LossAttackers.Contains(Vera) && r.Parcel.State.LossAttackers.Contains(Boris));
        Assert.Contains(hidden.Swaps[Tile]!.Replaced, r => r.State.OwnerId == Vera);

        store.Delete(Vera);
        withoutCapture.Delete(Vera);

        var projection = store.Project(Tile, [hidden]);

        Assert.Equal([UndoPath.Exact], projection.Paths);
        AssertSameRows(withoutCapture.Rows, projection.Pieces);
        Assert.DoesNotContain(projection.Pieces, p => p.Parcel.State.OwnerId == Vera || p.Parcel.State.LossAttackers.Contains(Vera));
    }
}
