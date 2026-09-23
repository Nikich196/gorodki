using System.Security.Cryptography;
using System.Text;
using Gorodki.Domain.Geo;
using NetTopologySuite.Algorithm.Locate;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Gorodki.Domain.Territory;

/// <summary>Кусок земли: один простой многоугольник внутри одного тайла и его состояние.</summary>
public sealed record Parcel(TileKey Tile, Polygon Geometry, ParcelState State);

/// <summary>Пороги осколков: такие куски поглощает сосед (PLAN.md, §7.3).</summary>
public sealed record SliverSettings
{
    public double MinAreaSquareMeters { get; init; } = 25;

    /// <summary>Уже 1,5 м везде — осколок.</summary>
    public double MinHalfWidthMeters { get; init; } = 0.75;
}

/// <summary>Итог применения захвата.</summary>
/// <param name="AreaByOutcome">Площадь, м², по видам последствий (сколько взято, треснуло, под щитом…).</param>
/// <param name="ChangedTiles">Тайлы, которые переписаны (их версии растут, клиенты перезапрашивают).</param>
/// <param name="SliverArea">Площадь осколков, отданных соседям или ставших ничьими, м².</param>
public sealed record CaptureResult(
    IReadOnlyDictionary<PieceOutcome, double> AreaByOutcome,
    IReadOnlyList<TileKey> ChangedTiles,
    double SliverArea)
{
    public double Area(PieceOutcome outcome) => AreaByOutcome.GetValueOrDefault(outcome);
}

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

    /// <summary>Площадь земли игрока, м².</summary>
    public double AreaOf(Guid ownerId) =>
        Parcels.Where(p => p.State.OwnerId == ownerId).Sum(p => p.Geometry.Area);

    /// <summary>Применяет захват: <paramref name="capture"/> — контур P из шага A.</summary>
    public CaptureResult Apply(Geometry capture, CaptureContext context)
    {
        var areas = new Dictionary<PieceOutcome, double>();
        var changed = new List<TileKey>();
        var sliverArea = 0.0;

        foreach (var tile in TileKey.Covering(capture.EnvelopeInternal))
        {
            var tilePolygon = tile.ToPolygon();
            var captureInTile = GeoOps.Intersection(capture, tilePolygon);
            if (captureInTile.IsEmpty)
            {
                continue;
            }

            var pieces = RebuildTile(tile, captureInTile, context, areas, ref sliverArea);
            if (pieces.Count == 0)
            {
                _tiles.Remove(tile);
            }
            else
            {
                _tiles[tile] = pieces;
            }

            changed.Add(tile);
        }

        return new CaptureResult(areas, changed, sliverArea);
    }

    private List<Parcel> RebuildTile(
        TileKey tile,
        Geometry captureInTile,
        CaptureContext context,
        Dictionary<PieceOutcome, double> areas,
        ref double sliverArea)
    {
        var old = ParcelsIn(tile);

        // 1–2. Узлуем все границы тайла вместе с границей петли и собираем грани.
        var lines = old.Select(p => p.Geometry.Boundary).Append(captureInTile.Boundary);
        var faces = GeoOps.Polygonize(GeoOps.Node(lines));

        // 3. Хозяин и судьба каждой грани — по её внутренней точке.
        var captureLocator = new IndexedPointInAreaLocator(captureInTile);
        var oldLocators = old.Select(p => (Parcel: p, Locator: new IndexedPointInAreaLocator(p.Geometry))).ToList();
        var groups = new Dictionary<ParcelState, List<Geometry>>();

        foreach (var face in faces)
        {
            var point = face.InteriorPoint.Coordinate;
            var owner = oldLocators
                .FirstOrDefault(o => o.Parcel.Geometry.EnvelopeInternal.Contains(point)
                    && o.Locator.Locate(point) == Location.Interior)
                .Parcel;

            var (state, outcome) = captureLocator.Locate(point) == Location.Interior
                ? CaptureRules.Decide(owner?.State, context, Rules)
                : (owner?.State, PieceOutcome.Untouched);

            if (outcome != PieceOutcome.Untouched)
            {
                areas[outcome] = areas.GetValueOrDefault(outcome) + face.Area;
            }

            if (state is null)
            {
                continue; // ничья земля не хранится
            }

            if (!groups.TryGetValue(state, out var group))
            {
                groups[state] = group = [];
            }

            group.Add(face);
        }

        // 4. Сливаем грани с одинаковым состоянием, убираем осколки.
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
