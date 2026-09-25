using Gorodki.Domain.Config;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Runs;
using Gorodki.Domain.Territory;
using NetTopologySuite.Algorithm.Construct;
using NetTopologySuite.Geometries;

namespace Gorodki.Calibration;

/// <summary>Пара чисел шага A, с которой перепроверяется каждая петля.</summary>
public sealed record ShapeVariant(double MinAreaSquareMeters, double MinHalfWidthMeters)
{
    /// <summary>Числа из кода (версия 1 конфига): A_min 2 500 м², R_min 9 м.</summary>
    public static ShapeVariant Current { get; } = new(
        GameConfig.Default.Capture.Shape.MinAreaSquareMeters, GameConfig.Default.Capture.Shape.MinHalfWidthMeters);
}

/// <summary>Петля, проверенная кодом сервера.</summary>
/// <param name="VariantIndex">Номер варианта чисел детектора.</param>
/// <param name="RingRejectCode">Отказ до контура (<c>LoopRing</c>: <c>not_closed</c>, <c>too_short</c>…) или null.</param>
/// <param name="Contour">Контур P шага A (UTM 34N) — до фильтров площади и ширины; null, если его нет.</param>
/// <param name="HalfWidthMeters">Радиус наибольшего вписанного круга контура — «полуширина», м.</param>
/// <param name="Outcomes">Исход для каждого варианта шага A: <c>applied</c> или код отказа, как у сервера.</param>
public sealed record LoopEvaluation(
    int VariantIndex,
    FoundLoop Loop,
    string? RingRejectCode,
    Geometry? Contour,
    double? HalfWidthMeters,
    Coordinate? WidestPoint,
    IReadOnlyList<string> Outcomes)
{
    public double? AreaSquareMeters => Contour?.Area;
}

/// <summary>Забег после перепроверки: точки в UTM 34N, вердикты судьи сервера и петли.</summary>
public sealed record RunEvaluation(
    ReplayRun Run,
    IReadOnlyList<Coordinate> Utm,
    IReadOnlyList<JudgeVerdict> Verdicts,
    IReadOnlyList<LoopEvaluation> Loops);

/// <summary>
/// Половина сервера: каждую найденную петлю проверяет тот же код, что <c>CaptureProcessor</c> — судья отрезков
/// (<see cref="TrackJudging"/>), кольцо петли (<see cref="LoopRing"/>) и шаг A (<see cref="CaptureShapeBuilder"/>) без
/// масок (их ещё нет и на сервере). Числа шага A, кроме A_min и R_min, — из кода (версия 1 конфига).
/// </summary>
/// <remarks>
/// Не проверяется то, что зависит не от следа: разрешение «Движение» (только предупреждение), лимиты, «старше 3 часов»,
/// защита от мультиаккаунтов и что станет с землёй на карте. «applied» здесь — «контур прошёл шаг A».
/// </remarks>
public static class Calibration
{
    public const string Applied = "applied";

    public static IReadOnlyList<RunEvaluation> Evaluate(ReplayFile file, IReadOnlyList<ShapeVariant> shapes) =>
        [.. file.Runs.Select(run => Evaluate(run, file.Newcomer, shapes))];

    public static RunEvaluation Evaluate(ReplayRun run, bool newcomer, IReadOnlyList<ShapeVariant> shapes)
    {
        var points = run.Points
            .Select(p => TrackPoint.FromMeasurements(p.Seq, p.T, p.Lat, p.Lon, p.Acc, p.Speed, (PointFlags)p.Flags))
            .ToList();
        for (var i = 0; i < points.Count; i++)
        {
            if (points[i].Seq != i)
            {
                throw new InvalidDataException($"Забег {run.Id}: точки должны идти подряд с номера 0, а на месте {i} — {points[i].Seq}.");
            }
        }

        var league = Enum.Parse<League>(run.League, ignoreCase: true);
        var verdicts = TrackJudging.JudgeAll(
            GameConfig.Default.JudgeRulesFor(league, newcomer),
            points,
            run.Motion.Select(m => new MotionSample(m.T, Enum.Parse<MotionActivity>(m.Activity, ignoreCase: true))),
            run.Steps.Select(s => new StepSample(s.Start, s.End, s.Steps)));
        var utm = points.Select(p =>
        {
            var (easting, northing) = Utm34.Forward(p.Latitude, p.Longitude);
            return new Coordinate(easting, northing);
        }).ToList();

        var loops = new List<LoopEvaluation>();
        for (var variant = 0; variant < run.Variants.Count; variant++)
        {
            foreach (var loop in run.Variants[variant].Loops)
            {
                loops.Add(EvaluateLoop(points, verdicts, variant, run.Variants[variant].Detector, loop, shapes));
            }
        }

        return new RunEvaluation(run, utm, verdicts, loops);
    }

    private static LoopEvaluation EvaluateLoop(
        IReadOnlyList<TrackPoint> points,
        IReadOnlyList<JudgeVerdict> verdicts,
        int variant,
        LoopDetectorSettings detector,
        FoundLoop loop,
        IReadOnlyList<ShapeVariant> shapes)
    {
        var ring = LoopRing.Build(points, verdicts, loop.StartSeq, loop.EndSeq, loop.Closure == "crossing", detector);
        if (!ring.IsAccepted)
        {
            return new LoopEvaluation(variant, loop, ring.RejectCode, null, null, null, [.. shapes.Select(_ => ring.RejectCode!)]);
        }

        var outcomes = new List<string>();
        Geometry? contour = null;
        foreach (var shape in shapes)
        {
            var settings = GameConfig.Default.Capture.Shape with
            {
                MinAreaSquareMeters = shape.MinAreaSquareMeters,
                MinHalfWidthMeters = shape.MinHalfWidthMeters,
            };
            var built = CaptureShapeBuilder.Build(ring.Ring, ring.ClosingTolerance, masks: null, settings);
            outcomes.Add(built.IsAccepted ? Applied : LoopRing.Code(built.Rejection));
            if (!built.Area.IsEmpty)
            {
                contour = built.Area; // A_min и R_min только фильтруют: контур у всех вариантов один
            }
        }

        if (contour is null)
        {
            return new LoopEvaluation(variant, loop, null, null, null, null, outcomes);
        }

        var circle = new MaximumInscribedCircle(contour, 0.05);
        return new LoopEvaluation(variant, loop, null, contour, circle.GetRadiusLine().Length, circle.GetCenter().Coordinate, outcomes);
    }
}
