using System.Security.Cryptography;
using System.Text;
using Gorodki.Domain.Geo;
using NetTopologySuite.Algorithm.Locate;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Gorodki.Domain.Territory;

/// <summary>
/// Движок участков нашёл у себя несходящийся результат. Захват отклоняется целиком, карта не меняется;
/// на сервере транзакция откатывается, захват повторяется один раз, событие уходит в журнал ошибок.
/// </summary>
public sealed class TerritoryEngineException(string message) : Exception(message);

/// <summary>Кусок земли: один простой многоугольник внутри одного тайла и его состояние.</summary>
public sealed record Parcel(TileKey Tile, Polygon Geometry, ParcelState State);

/// <summary>Пороги осколков: такие куски поглощает сосед (PLAN.md, §7.3).</summary>
public sealed record SliverSettings
{
    public double MinAreaSquareMeters { get; init; } = 25;

    /// <summary>Уже 1,5 м везде — осколок.</summary>
    public double MinHalfWidthMeters { get; init; } = 0.75;
}

/// <summary>Земля одного состояния внутри следа захвата (может состоять из нескольких многоугольников).</summary>
public sealed record JournalPiece(Geometry Geometry, ParcelState State);

/// <summary>
/// Что захват изменил в одном тайле — запись журнала для отката (PLAN.md, §7.3, шаг B.5).
/// </summary>
/// <param name="Footprint">След: где состояние земли стало другим, включая осколки, отданные соседям.</param>
/// <param name="Before">Земля внутри следа до захвата (ничьей земли в списке нет).</param>
/// <param name="After">Земля внутри следа после захвата.</param>
public sealed record TileChange(TileKey Tile, Geometry Footprint, IReadOnlyList<JournalPiece> Before, IReadOnlyList<JournalPiece> After);

/// <summary>Итог применения захвата.</summary>
/// <param name="AreaByOutcome">Площадь, м², по видам последствий (сколько взято, треснуло, под щитом…).</param>
/// <param name="ChangedTiles">Тайлы, которые переписаны (их версии растут, клиенты перезапрашивают).</param>
/// <param name="SliverArea">Площадь осколков, отданных соседям или ставших ничьими, м².</param>
/// <param name="Changes">Что изменилось по тайлам — для журнала отката; тайлы без изменений не входят.</param>
public sealed record CaptureResult(
    IReadOnlyDictionary<PieceOutcome, double> AreaByOutcome,
    IReadOnlyList<TileKey> ChangedTiles,
    double SliverArea,
    IReadOnlyList<TileChange> Changes)
{
    public double Area(PieceOutcome outcome) => AreaByOutcome.GetValueOrDefault(outcome);
}

/// <summary>Итог отката.</summary>
/// <param name="RestoredArea">Возвращено прежнее состояние, м².</param>
/// <param name="SkippedArea">Земля следа, которую после захвата уже изменили (другой захват, смена сезона…), м²: не тронута.</param>
/// <param name="SliverArea">Площадь осколков, отданных соседям или ставших ничьими при сборке кусков, м².</param>
/// <param name="ChangedTiles">Тайлы, которые переписаны.</param>
public sealed record RestoreResult(double RestoredArea, double SkippedArea, double SliverArea, IReadOnlyList<TileKey> ChangedTiles);

/// <summary>
/// Карта участков одной лиги в памяти — шаг B захвата (PLAN.md, §7.3, «Движок участков»).
/// </summary>
/// <remarks>
/// Для каждого тайла, который задевает петля:
/// <list type="number">
/// <item>границы всех кусков тайла и граница петли узлуются разом;</item>
/// <item>Polygonize собирает из них грани — разбиение без наложений по построению;</item>
/// <item>каждая грань получает хозяина по внутренней точке, а грань внутри петли — решение <see cref="CaptureRules"/>;</item>
/// <item>соседние грани с одинаковым состоянием сливаются, осколки поглощаются соседом.</item>
/// </list>
/// В базе данных тот же алгоритм выполняется под блокировками тайлов в отсортированном порядке.
/// Откат (<see cref="Restore"/>) собирает грани так же — только состояние грани внутри следа берётся из журнала.
/// </remarks>
public sealed class TerritoryMap(TerritoryRules rules, SliverSettings slivers)
{
    private readonly SortedDictionary<TileKey, IReadOnlyList<Parcel>> _tiles = new();

    public TerritoryMap()
        : this(new TerritoryRules(), new SliverSettings())
    {
    }

    public TerritoryRules Rules { get; } = rules;

    public SliverSettings Slivers { get; } = slivers;

    public IEnumerable<TileKey> Tiles => _tiles.Keys;

    public IEnumerable<Parcel> Parcels => _tiles.Values.SelectMany(pieces => pieces);

    public IReadOnlyList<Parcel> ParcelsIn(TileKey tile) => _tiles.GetValueOrDefault(tile) ?? [];

    /// <summary>
    /// Загружает куски из хранилища: на сервере — из базы, только для тайлов, которые задевает петля.
    /// Тайлы из списка заменяются целиком, остальные не трогаются.
    /// </summary>
    public void Load(IEnumerable<Parcel> parcels)
    {
        foreach (var tile in parcels.GroupBy(p => p.Tile))
        {
            _tiles[tile.Key] = tile.ToList();
        }
    }

    /// <summary>Площадь земли игрока, м².</summary>
    public double AreaOf(Guid ownerId) =>
        Parcels.Where(p => p.State.OwnerId == ownerId).Sum(p => p.Geometry.Area);

    /// <summary>Состояние земли в точке (<c>null</c> — ничья).</summary>
    public ParcelState? StateAt(Coordinate point) =>
        ParcelsIn(TileKey.Of(point.X, point.Y))
            .FirstOrDefault(p => p.Geometry.EnvelopeInternal.Contains(point)
                && new IndexedPointInAreaLocator(p.Geometry).Locate(point) == Location.Interior)
            ?.State;

    /// <summary>
    /// Применяет захват: <paramref name="capture"/> — контур P из шага A.
    /// Всё или ничего: сначала считаются новые куски всех тайлов, и только потом карта меняется.
    /// </summary>
    /// <exception cref="TerritoryEngineException">Самопроверка не сошлась — карта не изменена.</exception>
    public CaptureResult Apply(Geometry capture, CaptureContext context)
    {
        var areas = new Dictionary<PieceOutcome, double>();
        var rebuilt = new List<(TileKey Tile, List<Parcel> Pieces)>();
        var changes = new List<TileChange>();
        var sliverArea = 0.0;

        foreach (var tile in TileKey.Covering(capture.EnvelopeInternal))
        {
            var tilePolygon = tile.ToPolygon();
            var captureInTile = GeoOps.Intersection(capture, tilePolygon);
            if (captureInTile.IsEmpty)
            {
                continue;
            }

            var (pieces, change) = CaptureTile(tile, captureInTile, context, areas, ref sliverArea);
            rebuilt.Add((tile, pieces));
            if (change is not null)
            {
                changes.Add(change);
            }
        }

        Commit(rebuilt);
        return new CaptureResult(areas, rebuilt.Select(r => r.Tile).ToList(), sliverArea, changes);
    }

    /// <summary>
    /// Откат захвата по записям журнала: внутри следа, там, где земля и сейчас такая, какой её оставил захват,
    /// возвращается прежнее состояние. Где её с тех пор изменили (другой захват, смена сезона, удаление игрока), всё
    /// остаётся как есть — «откатываются только куски, которых после этого никто не трогал» (PLAN.md, §3.9, слой 5).
    /// Всё или ничего, как у захвата.
    /// </summary>
    /// <param name="changes">Записи журнала захвата по тайлам.</param>
    /// <param name="adjust">Как поправить возвращаемое состояние (например, не засчитать жертве угасание, пока земля была отнята).</param>
    /// <exception cref="TerritoryEngineException">Самопроверка не сошлась — карта не изменена.</exception>
    public RestoreResult Restore(IReadOnlyList<TileChange> changes, Func<ParcelState, ParcelState>? adjust = null)
    {
        var rebuilt = new List<(TileKey Tile, List<Parcel> Pieces)>();
        var restored = 0.0;
        var skipped = 0.0;
        var sliverArea = 0.0;
        foreach (var change in changes.OrderBy(c => c.Tile))
        {
            if (change.Footprint.IsEmpty)
            {
                continue;
            }

            rebuilt.Add((change.Tile, RestoreTile(change, adjust ?? (state => state), ref restored, ref skipped, ref sliverArea)));
        }

        Commit(rebuilt);
        return new RestoreResult(restored, skipped, sliverArea, rebuilt.Select(r => r.Tile).ToList());
    }

    private void Commit(List<(TileKey Tile, List<Parcel> Pieces)> rebuilt)
    {
        foreach (var (tile, pieces) in rebuilt)
        {
            if (pieces.Count == 0)
            {
                _tiles.Remove(tile);
            }
            else
            {
                _tiles[tile] = pieces;
            }
        }
    }

    /// <summary>Грань разбиения тайла: её внутренняя точка и прежнее и новое состояние.</summary>
    private sealed record Face(Polygon Geometry, Coordinate Point, ParcelState? Old)
    {
        public ParcelState? New { get; set; }
    }

    private (List<Parcel> Pieces, TileChange? Change) CaptureTile(
        TileKey tile,
        Geometry captureInTile,
        CaptureContext context,
        Dictionary<PieceOutcome, double> areas,
        ref double sliverArea)
    {
        // 1–2. Узлуем все границы тайла вместе с границей петли и собираем грани.
        var faces = FacesOf(tile, [captureInTile.Boundary]);

        // 3. Судьба каждой грани — по её внутренней точке.
        var captureLocator = new IndexedPointInAreaLocator(captureInTile);
        var decidedInTile = 0.0;
        foreach (var face in faces)
        {
            var (state, outcome) = captureLocator.Locate(face.Point) == Location.Interior
                ? CaptureRules.Decide(face.Old, context, Rules)
                : (face.Old, PieceOutcome.Untouched);
            face.New = state;

            if (outcome != PieceOutcome.Untouched)
            {
                areas[outcome] = areas.GetValueOrDefault(outcome) + face.Geometry.Area;
                decidedInTile += face.Geometry.Area;
            }
        }

        // Самопроверка: каждый квадратный метр петли в тайле получил решение. Расхождение допустимо только
        // от snap-rounding: он сдвигает любую точку не дальше полудиагонали клетки сетки (0,0707 м), поэтому
        // площадь меняется не больше чем на 0,0707 × длину границы. Сюда входят и изгибы отрезков к соседним
        // вершинам, и полоски уже ~14 см, которые на сетке схлопываются в линию.
        var tolerance = SnapTolerance(captureInTile);
        if (Math.Abs(decidedInTile - captureInTile.Area) > tolerance)
        {
            throw new TerritoryEngineException(
                $"Тайл {tile}: решено {decidedInTile:0.##} м² из {captureInTile.Area:0.##} м² петли (допуск {tolerance:0.##}).");
        }

        // 4. Сливаем грани с одинаковым состоянием, убираем осколки.
        var pieces = Assemble(tile, faces, ref sliverArea);
        return (pieces, ChangeOf(tile, faces, pieces));
    }

    private List<Parcel> RestoreTile(
        TileChange change, Func<ParcelState, ParcelState> adjust, ref double restored, ref double skipped, ref double sliverArea)
    {
        var tile = change.Tile;
        var footprint = GeoOps.Intersection(change.Footprint, tile.ToPolygon());
        var before = Located(change.Before);
        var after = Located(change.After);

        // Узлуем текущие границы вместе с границами журнала: грань целиком «до», «после» и «сейчас» одного состояния.
        var faces = FacesOf(
            tile,
            [.. change.Before.Select(p => p.Geometry.Boundary), .. change.After.Select(p => p.Geometry.Boundary), footprint.Boundary]);

        var footprintLocator = new IndexedPointInAreaLocator(footprint);
        var insideArea = 0.0;
        var restoredByState = new Dictionary<ParcelState, double>();
        foreach (var face in faces)
        {
            face.New = face.Old;
            if (footprintLocator.Locate(face.Point) != Location.Interior)
            {
                continue;
            }

            var area = face.Geometry.Area;
            insideArea += area;
            if (face.Old != StateAt(after, face.Point))
            {
                skipped += area; // после захвата здесь уже что-то поменялось — не трогаем
                continue;
            }

            var previous = StateAt(before, face.Point);
            face.New = previous is null ? null : adjust(previous);
            restored += area;
            if (previous is not null)
            {
                restoredByState[previous] = restoredByState.GetValueOrDefault(previous) + area;
            }
        }

        // Самопроверки: грани покрывают весь след, и ни одному прежнему состоянию не отдано больше, чем у него было.
        var tolerance = SnapTolerance(footprint);
        if (Math.Abs(insideArea - footprint.Area) > tolerance)
        {
            throw new TerritoryEngineException(
                $"Тайл {tile}: грани покрыли {insideArea:0.##} м² из {footprint.Area:0.##} м² следа (допуск {tolerance:0.##}).");
        }

        foreach (var (state, area) in restoredByState)
        {
            var had = before.Where(b => b.Piece.State == state).Sum(b => b.Piece.Geometry.Area);
            var stateTolerance = SnapTolerance(footprint) + before.Where(b => b.Piece.State == state).Sum(b => SnapTolerance(b.Piece.Geometry));
            if (area > had + stateTolerance)
            {
                throw new TerritoryEngineException(
                    $"Тайл {tile}: состоянию возвращено {area:0.##} м², а было {had:0.##} м² (допуск {stateTolerance:0.##}).");
            }
        }

        return Assemble(tile, faces, ref sliverArea);
    }

    /// <summary>Грани разбиения тайла текущими границами и дополнительными линиями; у каждой — текущее состояние.</summary>
    private List<Face> FacesOf(TileKey tile, IEnumerable<Geometry> extraLines)
    {
        var old = ParcelsIn(tile);
        var lines = old.Select(p => p.Geometry.Boundary).Concat(extraLines);
        var oldLocators = old.Select(p => (Parcel: p, Locator: new IndexedPointInAreaLocator(p.Geometry))).ToList();
        return GeoOps.Polygonize(GeoOps.Node(lines))
            .Select(face =>
            {
                var point = GeoOps.InteriorPoint(face);
                var owner = oldLocators
                    .FirstOrDefault(o => o.Parcel.Geometry.EnvelopeInternal.Contains(point)
                        && o.Locator.Locate(point) == Location.Interior)
                    .Parcel;
                return new Face(face, point, owner?.State);
            })
            .ToList();
    }

    /// <summary>Сливает грани с одинаковым новым состоянием, убирает осколки, упорядочивает куски.</summary>
    private List<Parcel> Assemble(TileKey tile, List<Face> faces, ref double sliverArea)
    {
        var groups = new Dictionary<ParcelState, List<Geometry>>();
        foreach (var face in faces)
        {
            if (face.New is not { } state)
            {
                continue; // ничья земля не хранится
            }

            if (!groups.TryGetValue(state, out var group))
            {
                groups[state] = group = [];
            }

            group.Add(face.Geometry);
        }

        var pieces = groups
            .SelectMany(g => GeoOps.Polygons(GeoOps.UnionAll(g.Value)).Select(polygon => new Parcel(tile, polygon, g.Key)))
            .ToList();
        sliverArea += AbsorbSlivers(pieces, tile.ToPolygon());

        // Детерминированный порядок: одинаковая история захватов даёт одинаковую карту.
        return pieces
            .OrderBy(p => p.Geometry.EnvelopeInternal.MinX)
            .ThenBy(p => p.Geometry.EnvelopeInternal.MinY)
            .ThenBy(p => p.Geometry.Area)
            .ThenBy(p => p.State.OwnerId)
            .ToList();
    }

    /// <summary>
    /// Запись журнала по тайлу: грани, чьё состояние стало другим (с учётом осколков, отданных соседям), и земля до и после
    /// на них. Считается по тем же граням, что и захват, — без второго узлования.
    /// </summary>
    private static TileChange? ChangeOf(TileKey tile, List<Face> faces, List<Parcel> pieces)
    {
        var final = Located(pieces.Select(p => new JournalPiece(p.Geometry, p.State)).ToList());
        var changed = faces
            .Select(face => (Face: face, Final: StateAt(final, face.Point)))
            .Where(f => f.Face.Old != f.Final)
            .ToList();
        if (changed.Count == 0)
        {
            return null;
        }

        static List<JournalPiece> Group(IEnumerable<(Polygon Geometry, ParcelState? State)> faces) =>
            faces
                .Where(f => f.State is not null)
                .GroupBy(f => f.State!)
                .Select(g => new JournalPiece(GeoOps.UnionAll(g.Select(f => (Geometry)f.Geometry)), g.Key))
                .Where(p => !p.Geometry.IsEmpty)
                .ToList();

        return new TileChange(
            tile,
            GeoOps.UnionAll(changed.Select(f => (Geometry)f.Face.Geometry)),
            Group(changed.Select(f => (f.Face.Geometry, f.Face.Old))),
            Group(changed.Select(f => (f.Face.Geometry, f.Final))));
    }

    private static List<(JournalPiece Piece, IndexedPointInAreaLocator Locator)> Located(IReadOnlyList<JournalPiece> pieces) =>
        pieces.Select(p => (p, new IndexedPointInAreaLocator(p.Geometry))).ToList();

    private static ParcelState? StateAt(List<(JournalPiece Piece, IndexedPointInAreaLocator Locator)> pieces, Coordinate point) =>
        pieces
            .FirstOrDefault(p => p.Piece.Geometry.EnvelopeInternal.Contains(point) && p.Locator.Locate(point) == Location.Interior)
            .Piece?.State;

    /// <summary>
    /// Осколок (меньше 25 м² или уже 1,5 м) поглощает сосед с самой длинной общей границей.
    /// Куски у края тайла не трогаем: у края тонкий кусок — это законная часть большого участка из соседнего тайла.
    /// </summary>
    private double AbsorbSlivers(List<Parcel> pieces, Polygon tilePolygon)
    {
        var total = 0.0;
        bool absorbed;
        do
        {
            absorbed = false;
            for (var i = 0; i < pieces.Count; i++)
            {
                var sliver = pieces[i].Geometry;
                if (!IsSliver(sliver) || GeoOps.SharedBoundaryLength(sliver, tilePolygon) > 0)
                {
                    continue;
                }

                var best = -1;
                var bestLength = 0.0;
                for (var j = 0; j < pieces.Count; j++)
                {
                    if (j == i || !pieces[j].Geometry.EnvelopeInternal.Intersects(sliver.EnvelopeInternal))
                    {
                        continue;
                    }

                    var length = GeoOps.SharedBoundaryLength(sliver, pieces[j].Geometry);
                    if (length > bestLength)
                    {
                        best = j;
                        bestLength = length;
                    }
                }

                if (best >= 0)
                {
                    var merged = GeoOps.Polygons(GeoOps.UnionAll([pieces[best].Geometry, sliver]))
                        .OrderByDescending(p => p.Area)
                        .First();
                    pieces[best] = pieces[best] with { Geometry = merged };
                }

                // Осколок без соседей просто становится ничьей землёй.
                total += sliver.Area;
                pieces.RemoveAt(i);
                absorbed = true;
                break;
            }
        }
        while (absorbed);

        return total;
    }

    /// <summary>
    /// Насколько snap-rounding на сетке 0,1 м может изменить площадь фигуры: 0,075 × длину её границы
    /// (полудиагональ клетки 0,0707 м с запасом), м².
    /// </summary>
    public static double SnapTolerance(Geometry area) => 0.075 * area.Boundary.Length + 0.01;

    /// <summary>Осколок ли кусок по порогам <see cref="Slivers"/>.</summary>
    public bool IsSliver(Geometry piece) =>
        piece.Area < Slivers.MinAreaSquareMeters || GeoOps.IsNarrowerThan(piece, Slivers.MinHalfWidthMeters);

    /// <summary>
    /// Отпечаток всей карты: одинаковая история захватов обязана давать одинаковый отпечаток.
    /// </summary>
    public string StateHash()
    {
        var writer = new WKBWriter();
        using var sha = SHA256.Create();
        var builder = new StringBuilder();
        foreach (var (tile, pieces) in _tiles)
        {
            foreach (var piece in pieces)
            {
                builder.Append(tile).Append('|').Append(piece.State).Append('|')
                    .Append(Convert.ToHexString(writer.Write(piece.Geometry.Normalized()))).Append('\n');
            }
        }

        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
