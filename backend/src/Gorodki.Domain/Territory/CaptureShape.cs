using Gorodki.Domain.Geo;
using NetTopologySuite.Geometries;
using NetTopologySuite.Simplify;

namespace Gorodki.Domain.Territory;

/// <summary>Почему петля не засчитана. Причина показывается игроку.</summary>
public enum CaptureRejection
{
    None,
    /// <summary>Меньше четырёх точек — это не петля.</summary>
    TooFewPoints,
    /// <summary>Конец следа слишком далеко от точки, с которой петля начиналась.</summary>
    NotClosed,
    /// <summary>След ничего не обвёл: «туда-обратно» или всё внутри закрытых зон.</summary>
    Empty,
    /// <summary>Площадь меньше минимальной.</summary>
    TooSmall,
    /// <summary>Обведённая область слишком узкая: нигде нет «пятна» нужного размера.</summary>
    TooNarrow,
    /// <summary>Кольцо больше 3,5 км² — не засчитывается (PLAN.md, §3.2).</summary>
    TooLarge,
}

/// <summary>Числа шага A. Хранятся в игровом конфиге и калибруются на полевом тесте №1.</summary>
public sealed record CaptureShapeSettings
{
    /// <summary>Упрощение следа перед построением контура (Дуглас — Пекер), метры.</summary>
    public double SimplifyToleranceMeters { get; init; } = 2.0;

    /// <summary>
    /// Грань уже 2·этого значения — узкая. Узкая грань входит в захват, только если хотя бы
    /// <see cref="ThinFaceMinSharedBoundary"/> её границы — общая с широкой частью захвата
    /// (например, «рамка» между двумя витками вокруг квартала). Узкие грани, которые висят снаружи, —
    /// это отростки «туда-обратно», и площади они не дают.
    /// </summary>
    public double SpurHalfWidthMeters { get; init; } = 4.0;

    /// <summary>Доля периметра узкой грани, общая с захватом, чтобы грань вошла в захват.</summary>
    public double ThinFaceMinSharedBoundary { get; init; } = 0.25;

    /// <summary>
    /// «Шип»: путь приходит в точку и почти разворачивается назад (угол меньше этого).
    /// Такие точки — выбросы GPS, их выбрасываем, иначе они дают тонкие выступы.
    /// </summary>
    public double SpikeMaxAngleDegrees { get; init; } = 20.0;

    /// <summary>A_min, м².</summary>
    public double MinAreaSquareMeters { get; init; } = 2_500;

    /// <summary>Потолок площади одной петли, м².</summary>
    public double MaxAreaSquareMeters { get; init; } = 3_500_000;

    /// <summary>R_min: в захвате должно найтись место для «пятна» радиусом R_min, метры.</summary>
    public double MinHalfWidthMeters { get; init; } = 9.0;
}

/// <summary>Результат шага A: контур захвата или причина отказа.</summary>
public sealed record CaptureShape(Geometry Area, CaptureRejection Rejection)
{
    public bool IsAccepted => Rejection == CaptureRejection.None;

    public double AreaSquareMeters => Area.Area;
}

/// <summary>
/// Шаг A захвата (PLAN.md, §3.2 и §7.3): из проверенного следа петли строит контур P.
/// Работает до всяких блокировок и зависит только от самого забега.
/// </summary>
/// <remarks>
/// Захват — это «всё, что обведено следом»: след разрезается во всех самопересечениях (узлование),
/// из кусков собираются все замкнутые грани, и их объединение и есть P.
/// Поэтому восьмёрка даёт оба лепестка, двойной виток — одну область, а петля,
/// пробежанная сначала по часовой, потом против, не оставляет «дыры» (чем грешит MakeValid).
/// </remarks>
public static class CaptureShapeBuilder
{
    /// <param name="trail">Точки петли в UTM 34N, по порядку, уже прошедшие античит.</param>
    /// <param name="closingToleranceMeters">R: насколько конец может не дойти до начала (20–50 м).</param>
    /// <param name="masks">Зоны, которые не захватываются (вода, ж/д, мемориалы…), или null.</param>
    public static CaptureShape Build(
        IReadOnlyList<Coordinate> trail,
        double closingToleranceMeters,
        Geometry? masks,
        CaptureShapeSettings settings)
    {
        if (trail.Count < 4)
        {
            return Rejected(CaptureRejection.TooFewPoints);
        }

        if (trail[0].Distance(trail[^1]) > closingToleranceMeters)
        {
            return Rejected(CaptureRejection.NotClosed);
        }

        // 1. След на сетку 0,1 м с замыкающей хордой к первой точке. Сначала убираем «шипы» на сырых точках
        //    (после упрощения острый шип превращается в пологий треугольник и уже не узнаётся), потом упрощаем ~2 м.
        var points = trail.Select(GeoOps.Snap).ToList();
        points.Add(points[0].Copy());
        var withoutSpikes = RemoveSpikes(points, settings.SpikeMaxAngleDegrees);
        if (withoutSpikes.Length < 4)
        {
            return Rejected(CaptureRejection.Empty);
        }

        var line = GeoOps.Factory.CreateLineString(withoutSpikes);
        var ring = DouglasPeuckerSimplifier.Simplify(line, settings.SimplifyToleranceMeters);

        // 2. Узлование и все замкнутые грани; узкие отростки снаружи отбрасываем.
        var faces = GeoOps.Polygonize(GeoOps.Node([ring]));
        var area = GeoOps.UnionAll(SelectEnclosedFaces(faces, settings));

        // 3. Минус закрытые зоны.
        if (masks is not null && !area.IsEmpty)
        {
            area = GeoOps.Difference(area, masks);
        }

        // 4. Фильтры площади и ширины.
        if (area.IsEmpty)
        {
            return Rejected(CaptureRejection.Empty);
        }

        if (area.Area > settings.MaxAreaSquareMeters)
        {
            return new CaptureShape(area, CaptureRejection.TooLarge);
        }

        if (area.Area < settings.MinAreaSquareMeters)
        {
            return new CaptureShape(area, CaptureRejection.TooSmall);
        }

        if (GeoOps.IsNarrowerThan(area, settings.MinHalfWidthMeters))
        {
            return new CaptureShape(area, CaptureRejection.TooNarrow);
        }

        return new CaptureShape(area, CaptureRejection.None);
    }

    private static CaptureShape Rejected(CaptureRejection reason) => new(GeoOps.EmptyPolygon, reason);

    /// <summary>
    /// Широкие грани входят всегда. Узкая грань входит, если достаточная доля её границы общая
    /// с уже вошедшими гранями; повторяем, пока что-то добавляется.
    /// </summary>
    private static List<Polygon> SelectEnclosedFaces(IReadOnlyList<Polygon> faces, CaptureShapeSettings settings)
    {
        var kept = new List<Polygon>();
        var thin = new List<Polygon>();
        foreach (var face in faces)
        {
            (GeoOps.IsNarrowerThan(face, settings.SpurHalfWidthMeters) ? thin : kept).Add(face);
        }

        if (kept.Count == 0)
        {
            return kept; // одни узкие грани — это чистое «туда-обратно»
        }

        bool added;
        do
        {
            added = false;
            for (var i = thin.Count - 1; i >= 0; i--)
            {
                var face = thin[i];
                var shared = kept
                    .Where(k => k.EnvelopeInternal.Intersects(face.EnvelopeInternal))
                    .Sum(k => GeoOps.SharedBoundaryLength(face, k));
                if (shared >= settings.ThinFaceMinSharedBoundary * face.Boundary.Length)
                {
                    kept.Add(face);
                    thin.RemoveAt(i);
                    added = true;
                }
            }
        }
        while (added);

        return kept;
    }

    /// <summary>
    /// Убирает точки, в которых путь разворачивается почти назад (угол меньше <paramref name="maxAngleDegrees"/>).
    /// Первая и последняя точки остаются: это начало петли.
    /// </summary>
    private static Coordinate[] RemoveSpikes(IReadOnlyList<Coordinate> coordinates, double maxAngleDegrees)
    {
        var points = new List<Coordinate>(coordinates);
        var maxAngle = maxAngleDegrees * Math.PI / 180;
        var i = 1;
        while (i < points.Count - 1)
        {
            // Совпадающие соседние точки — тоже «шип» с нулевым углом: убираем.
            var angle = points[i].Equals2D(points[i - 1]) || points[i].Equals2D(points[i + 1])
                ? 0
                : NetTopologySuite.Algorithm.AngleUtility.AngleBetween(points[i - 1], points[i], points[i + 1]);
            if (angle < maxAngle)
            {
                points.RemoveAt(i);
                i = Math.Max(1, i - 1); // после удаления соседняя точка могла стать новым шипом
            }
            else
            {
                i++;
            }
        }

        return points.ToArray();
    }
}
