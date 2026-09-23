using Gorodki.Domain.Geo;
using Gorodki.Domain.Territory;
using NetTopologySuite.Geometries;

namespace Gorodki.Domain.Runs;

/// <summary>Кольцо петли в UTM 34N — вход шага A (<see cref="CaptureShapeBuilder"/>) — или причина отказа.</summary>
/// <param name="Ring">Точки кольца по порядку; хорду от последней точки к первой замыкает построитель контура.</param>
/// <param name="ClosingTolerance">Насколько конец может не дойти до начала, метры.</param>
/// <param name="RejectCode">Стабильный код отказа для приложения или null.</param>
public sealed record LoopRing(IReadOnlyList<Coordinate> Ring, double ClosingTolerance, string? RejectCode)
{
    /// <summary>Допуск на расхождение расчётов телефона (плоскость «восток — север») и сервера (UTM, гаверсинус).</summary>
    public const double PhoneTolerance = 1.01;

    public bool IsAccepted => RejectCode is null;

    /// <summary>
    /// Проверяет заявку петли по вердиктам судьи и строит кольцо (PLAN.md, §3.2). Точки — весь след по номерам с нуля.
    /// </summary>
    public static LoopRing Build(
        IReadOnlyList<TrackPoint> points,
        IReadOnlyList<JudgeVerdict> verdicts,
        int startSeq,
        int endSeq,
        bool closedByCrossing,
        LoopDetectorSettings settings)
    {
        if (endSeq >= points.Count || startSeq < 0 || endSeq - startSeq < 3)
        {
            return Rejected("claim_invalid");
        }

        // Петля не может охватывать два отрезка (D16): разрыв внутри — отказ. Разрыв ровно на первой точке допустим:
        // после разрыва телефон начинает новый отрезок с этой точки.
        for (var seq = startSeq + 1; seq <= endSeq; seq++)
        {
            if (verdicts[seq].Kind == VerdictKind.SegmentBroken)
            {
                return Rejected($"segment_broken:{verdicts[seq].ToString()["broken:".Length..]}");
            }
        }

        var trail = new List<TrackPoint>();
        for (var seq = startSeq; seq <= endSeq; seq++)
        {
            var kind = verdicts[seq].Kind;
            if (kind == VerdictKind.Accepted || (seq == startSeq && kind == VerdictKind.SegmentBroken))
            {
                trail.Add(points[seq]);
            }
        }

        if (trail.Count < 4)
        {
            return Rejected("too_few_points");
        }

        var path = 0.0;
        for (var i = 1; i < trail.Count; i++)
        {
            path += Geodesy.Distance(trail[i - 1].Latitude, trail[i - 1].Longitude, trail[i].Latitude, trail[i].Longitude);
        }

        if (path * PhoneTolerance < settings.MinPathMeters)
        {
            return Rejected("too_short");
        }

        var utm = trail.Select(p =>
        {
            var (easting, northing) = Utm34.Forward(p.Latitude, p.Longitude);
            return new Coordinate(easting, northing);
        }).ToList();

        // Замыкание по пересечению: телефон увидел, что отрезок (начало, следующая) пересёк отрезок (предпоследняя, конец).
        // Концы при этом могут быть дальше R друг от друга (велосипед, пропуски точек) — кольцо строится через точку пересечения.
        if (closedByCrossing && Intersection(utm[0], utm[1], utm[^2], utm[^1]) is { } crossing)
        {
            var ring = new List<Coordinate> { crossing };
            ring.AddRange(utm.Skip(1).Take(utm.Count - 2));
            ring.Add(crossing.Copy());
            return new LoopRing(ring, 0.01, null);
        }

        var radius = Math.Clamp(
            settings.RadiusFactor * Math.Sqrt((trail[0].AccuracyMeters * trail[0].AccuracyMeters)
                + (trail[^1].AccuracyMeters * trail[^1].AccuracyMeters)),
            settings.MinRadiusMeters,
            settings.MaxRadiusMeters) * PhoneTolerance;
        return utm[0].Distance(utm[^1]) > radius ? Rejected("not_closed") : new LoopRing(utm, radius, null);
    }

    /// <summary>Код отказа шага A для приложения.</summary>
    public static string Code(CaptureRejection rejection) => rejection switch
    {
        CaptureRejection.TooFewPoints => "too_few_points",
        CaptureRejection.NotClosed => "not_closed",
        CaptureRejection.Empty => "empty",
        CaptureRejection.TooSmall => "too_small",
        CaptureRejection.TooNarrow => "too_narrow",
        CaptureRejection.TooLarge => "too_large",
        _ => "rejected",
    };

    private static LoopRing Rejected(string code) => new([], 0, code);

    /// <summary>Точка пересечения отрезков AB и CD или null.</summary>
    private static Coordinate? Intersection(Coordinate a, Coordinate b, Coordinate c, Coordinate d)
    {
        var r = new Coordinate(b.X - a.X, b.Y - a.Y);
        var s = new Coordinate(d.X - c.X, d.Y - c.Y);
        var denominator = (r.X * s.Y) - (r.Y * s.X);
        if (Math.Abs(denominator) < 1e-12)
        {
            return null;
        }

        var t = (((c.X - a.X) * s.Y) - ((c.Y - a.Y) * s.X)) / denominator;
        var u = (((c.X - a.X) * r.Y) - ((c.Y - a.Y) * r.X)) / denominator;
        return t is >= 0 and <= 1 && u is >= 0 and <= 1 ? new Coordinate(a.X + (t * r.X), a.Y + (t * r.Y)) : null;
    }
}
