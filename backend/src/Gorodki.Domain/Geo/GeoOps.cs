using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Operation.Buffer;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;
using NetTopologySuite.Operation.Polygonize;
using NetTopologySuite.Precision;

namespace Gorodki.Domain.Geo;

/// <summary>
/// Единственная точка входа во все геометрические операции над участками (PLAN.md, §7.3, «Геометрический контракт»).
/// </summary>
/// <remarks>
/// Контракт:
/// <list type="bullet">
/// <item>координаты — метры UTM 34N (<see cref="Utm34"/>);</item>
/// <item>все вершины лежат на сетке 0,1 м;</item>
/// <item>каждое пересечение, разность и объединение — через OverlayNG со snap-rounding на этой сетке.</item>
/// </list>
/// Snap-rounding избавляет от исключений TopologyException и «почти совпадающих» точек.
/// Обычные методы <c>Geometry.Intersection()</c>, <c>Difference()</c>, <c>Union()</c> в NTS по умолчанию
/// используют старый алгоритм без фиксированной сетки, поэтому вызывать их в обход фасада запрещено
/// (архитектурный тест <c>GeoOpsArchitectureTests</c>).
/// </remarks>
public static class GeoOps
{
    /// <summary>Сетка 0,1 м: масштаб 10 означает «десять делений на метр».</summary>
    public static PrecisionModel Grid { get; } = new(10.0);

    public static GeometryFactory Factory { get; } = new(Grid, Utm34.Srid);

    /// <summary>
    /// Новый пустой многоугольник. Каждый раз новый, а не общий: геометрии NTS изменяемы
    /// (например, кэшируют рамку), и общий объект между потоками — лишний риск.
    /// </summary>
    public static Polygon EmptyPolygon() => Factory.CreatePolygon();

    // ── Площадные операции: результат — только многоугольники ────────────────

    public static Geometry Intersection(Geometry a, Geometry b) =>
        Polygonal(OverlayNG.Overlay(a, b, SpatialFunction.Intersection, Grid));

    public static Geometry Difference(Geometry a, Geometry b) =>
        Polygonal(OverlayNG.Overlay(a, b, SpatialFunction.Difference, Grid));

    public static Geometry Union(Geometry a, Geometry b) =>
        Polygonal(OverlayNG.Overlay(a, b, SpatialFunction.Union, Grid));

    /// <summary>Объединение набора многоугольников в один (соседи сливаются).</summary>
    public static Geometry UnionAll(IEnumerable<Geometry> geometries)
    {
        var collection = Factory.BuildGeometry(geometries.ToList());
        return collection.IsEmpty ? EmptyPolygon() : Polygonal(UnaryUnionNG.Union(collection, Grid));
    }

    // ── Линии: узлование и сборка граней ─────────────────────────────────────

    /// <summary>
    /// Узлование: разрезает линии во всех точках пересечения и касания, убирает повторы.
    /// Результат — правильный вход для <see cref="Polygonize"/>.
    /// </summary>
    public static Geometry Node(IEnumerable<Geometry> lines)
    {
        var collection = Factory.BuildGeometry(lines.ToList());
        return UnaryUnionNG.Union(collection, Grid);
    }

    /// <summary>Все ограниченные грани, которые образуют узлованные линии («разбиение плоскости»).</summary>
    public static IReadOnlyList<Polygon> Polygonize(Geometry nodedLines)
    {
        var polygonizer = new Polygonizer(extractOnlyPolygonal: false);
        polygonizer.Add(nodedLines);
        return polygonizer.GetPolygons().OfType<Polygon>().Where(p => !p.IsEmpty).ToList();
    }

    // ── Вспомогательное ──────────────────────────────────────────────────────

    /// <summary>Оставляет от результата только многоугольники (без линий и точек касания).</summary>
    public static Geometry Polygonal(Geometry geometry)
    {
        var polygons = PolygonExtracter.GetPolygons(geometry).OfType<Polygon>().Where(p => !p.IsEmpty).ToList();
        return polygons.Count switch
        {
            0 => EmptyPolygon(),
            1 => polygons[0],
            _ => Factory.CreateMultiPolygon(polygons.ToArray()),
        };
    }

    /// <summary>
    /// Точка строго внутри многоугольника — без округления до сетки.
    /// </summary>
    /// <remarks>
    /// Встроенное <c>Geometry.InteriorPoint</c> в NTS (как и в JTS) округляет найденную точку до сетки фабрики,
    /// у нас — до 0,1 м. Для клина уже ~14 см округлённая точка может оказаться снаружи, и грань получает
    /// чужого хозяина. Найдено property-тестом (ADR 0003), поэтому <c>.InteriorPoint</c> в обход фасада запрещён.
    /// </remarks>
    public static Coordinate InteriorPoint(Geometry area) => InteriorPointArea.GetInteriorPoint(area);

    /// <summary>Отдельные многоугольники из (мульти)многоугольника.</summary>
    public static IEnumerable<Polygon> Polygons(Geometry geometry) =>
        PolygonExtracter.GetPolygons(geometry).OfType<Polygon>().Where(p => !p.IsEmpty);

    /// <summary>
    /// Тот же многоугольник (то же множество точек) без вершин, которые лежат на прямой между соседними, — в
    /// каноническом порядке обхода (<c>Normalized</c>).
    /// </summary>
    /// <remarks>
    /// Узлование ставит вершину в каждую точку, где граница пересеклась с другой линией, даже если граница там не
    /// изломилась. Такие вершины ничего не меняют в куске, но копятся и выдают, где прошла линия (PLAN.md, §3.16).
    /// «На прямой» проверяется точно, в целых дециметрах сетки; вершину вне сетки не трогаем — для неё точной проверки нет.
    /// </remarks>
    public static Polygon WithoutCollinearVertices(Polygon polygon)
    {
        var shell = WithoutCollinearVertices(polygon.Shell);
        var holes = polygon.Holes.Select(WithoutCollinearVertices).ToArray();
        return (Polygon)polygon.Factory.CreatePolygon(shell, holes).Normalized();
    }

    private static LinearRing WithoutCollinearVertices(LinearRing ring)
    {
        var points = ring.Coordinates;
        var kept = new List<Coordinate>(points.Length);
        foreach (var point in points.Take(points.Length - 1)) // последняя точка кольца повторяет первую
        {
            kept.Add(point);
            while (kept.Count >= 3 && IsStraightThrough(kept[^3], kept[^2], kept[^1]))
            {
                kept.RemoveAt(kept.Count - 2);
            }
        }

        // Стык кольца: последняя и первая вершины тоже могут лежать на прямой между соседями.
        while (kept.Count > 3)
        {
            if (IsStraightThrough(kept[^2], kept[^1], kept[0]))
            {
                kept.RemoveAt(kept.Count - 1);
            }
            else if (IsStraightThrough(kept[^1], kept[0], kept[1]))
            {
                kept.RemoveAt(0);
            }
            else
            {
                break;
            }
        }

        if (kept.Count == points.Length - 1)
        {
            return ring;
        }

        kept.Add(kept[0].Copy());
        return ring.Factory.CreateLinearRing(kept.ToArray());
    }

    /// <summary>Вершина <paramref name="b"/> лежит на отрезке ac строго между концами (на сетке — точно).</summary>
    private static bool IsStraightThrough(Coordinate a, Coordinate b, Coordinate c)
    {
        if (!IsOnGrid(a.X) || !IsOnGrid(a.Y) || !IsOnGrid(b.X) || !IsOnGrid(b.Y) || !IsOnGrid(c.X) || !IsOnGrid(c.Y))
        {
            return false;
        }

        long ax = Decimeters(a.X), ay = Decimeters(a.Y), bx = Decimeters(b.X), by = Decimeters(b.Y);
        long cx = Decimeters(c.X), cy = Decimeters(c.Y);
        var cross = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
        var forward = ((bx - ax) * (cx - bx)) + ((by - ay) * (cy - by));
        return cross == 0 && forward > 0;
    }

    private static long Decimeters(double value) => (long)Math.Round(value * 10);

    /// <summary>Переносит геометрию на сетку 0,1 м так, чтобы она осталась правильной.</summary>
    public static Geometry Snap(Geometry geometry) => GeometryPrecisionReducer.Reduce(geometry, Grid);

    /// <summary>Точка на сетке 0,1 м.</summary>
    public static Coordinate Snap(Coordinate coordinate)
    {
        var snapped = coordinate.Copy();
        Grid.MakePrecise(snapped);
        return snapped;
    }

    /// <summary>Фабрика без сетки — только для сжатия в <see cref="IsNarrowerThan"/>.</summary>
    private static readonly GeometryFactory Floating = new(new PrecisionModel(), Utm34.Srid);

    /// <summary>
    /// «Уже, чем 2·<paramref name="halfWidth"/> везде»: после сжатия внутрь на halfWidth ничего не остаётся.
    /// Соединения «митра» сохраняют углы, поэтому проверка не зависит от скруглений.
    /// </summary>
    /// <remarks>
    /// Сжимается копия без сетки. Буфер NTS берёт точность у геометрии: на сетке 0,1 м он округляет точки пересечения
    /// контура до сетки без snap-rounding и изредка молча выдаёт пустой результат — петля в 56 000 м² оказалась «уже
    /// 1,5 м», движок принял её за осколок, и захват пропал (issue #113). Без сетки NTS узлует точно, а если не сходится,
    /// сам переходит на snap-rounding.
    /// </remarks>
    public static bool IsNarrowerThan(Geometry area, double halfWidth) =>
        BufferOp.Buffer(Floating.CreateGeometry(area), -halfWidth, new BufferParameters
        {
            JoinStyle = JoinStyle.Mitre,
            MitreLimit = 2.0,
            EndCapStyle = EndCapStyle.Flat,
        }).IsEmpty;

    /// <summary>Часть линии вне области.</summary>
    /// <remarks>
    /// Сетка здесь тоже есть: OverlayNG без явной точности берёт её у первой геометрии, а линии строятся через
    /// <see cref="Factory"/>. Поэтому это тот же snap-rounding на 0,1 м, что и у остальных операций фасада: точки линии
    /// сдвигаются не дальше полудиагонали клетки (0,0707 м) — для визитов (от 50 м пути) это ничто.
    /// </remarks>
    public static Geometry LineOutside(Geometry line, Geometry area) =>
        OverlayNG.Overlay(line, area, SpatialFunction.Difference);

    /// <summary>Длина части линии внутри многоугольника, метры (граница — тоже «внутри»; сетка — как у <see cref="LineOutside"/>).</summary>
    public static double LengthInside(Geometry line, Geometry area) =>
        OverlayNG.Overlay(line, area, SpatialFunction.Intersection).Length;

    /// <summary>Длина общей границы двух многоугольников, метры.</summary>
    public static double SharedBoundaryLength(Geometry a, Geometry b) =>
        OverlayNG.Overlay(a.Boundary, b.Boundary, SpatialFunction.Intersection, Grid).Length;

    /// <summary>
    /// Площадь наложения двух многоугольников на сетке, как их видит движок, — при любом разбиении общей границы на вершины,
    /// м². Для проверок: куски так не строятся.
    /// </summary>
    /// <remarks>
    /// Угол одного куска может лежать на стороне другого, где у того вершины нет (нетронутый кусок не переписывается, новый
    /// хранится без вершин на прямых). Snap-rounding находит там точку пересечения не точно, отрезок от неё задевает клетку
    /// соседней вершины, и сторона уходит к соседу — «наложение» 0,01 м², которого нет (issue #101). Поэтому сначала в
    /// стороны каждого вставляются вершины другого, которые на них лежат (точно, в дециметрах сетки): общая граница записана
    /// у обоих одинаково, и snap-rounding сдвигает её у обоих одинаково.
    /// </remarks>
    public static double OverlapArea(Geometry a, Geometry b) =>
        Intersection(WithVerticesOf(a, b), WithVerticesOf(b, a)).Area;

    /// <summary><paramref name="target"/> с вершинами <paramref name="source"/>, которые лежат на его сторонах.</summary>
    private static Geometry WithVerticesOf(Geometry target, Geometry source)
    {
        var vertices = source.Coordinates.Where(c => target.EnvelopeInternal.Contains(c)).Distinct().ToList();
        return new GeometryEditor(target.Factory).Edit(target, new InsertVertices(vertices));
    }

    private sealed class InsertVertices(List<Coordinate> vertices) : GeometryEditor.CoordinateOperation
    {
        public override Coordinate[] Edit(Coordinate[] coordinates, Geometry geometry)
        {
            var result = new List<Coordinate>(coordinates.Length) { coordinates[0] };
            for (var i = 1; i < coordinates.Length; i++)
            {
                var (from, to) = (coordinates[i - 1], coordinates[i]);
                result.AddRange(vertices.Where(v => IsStraightThrough(from, v, to)).OrderBy(v => v.Distance(from)).Select(v => v.Copy()));
                result.Add(to);
            }

            return [.. result];
        }
    }

    /// <summary>Все вершины лежат на сетке 0,1 м (с допуском на представление чисел double).</summary>
    public static bool IsOnGrid(Geometry geometry) =>
        geometry.Coordinates.All(c => IsOnGrid(c.X) && IsOnGrid(c.Y));

    private static bool IsOnGrid(double value) => Math.Abs(value * 10 - Math.Round(value * 10)) < 1e-6;
}
