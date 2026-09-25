using Gorodki.Domain.Geo;
using Gorodki.Domain.Territory;

namespace Gorodki.Calibration.Tests;

/// <summary>
/// Синтетические следы в метрах UTM 34N от условной точки в Бресте — никаких настоящих маршрутов. Ходьба 1,4 м/с,
/// точка в секунду, точность 5 м; ошибка GPS «плывёт» (AR(1), ρ = 0,9) и не выходит за предел по каждой оси.
/// </summary>
internal static class Synthetic
{
    private const double OriginX = 684_000;
    private const double OriginY = 5_775_000;
    private const long StartMs = 1_790_000_000_000;

    public static List<ReplayPoint> Walk(IReadOnlyList<(double X, double Y)> vertices, double noiseLimit, int seed = 1)
    {
        var random = new Random(seed);
        var (driftX, driftY) = (0.0, 0.0);
        var points = new List<ReplayPoint>();

        void Fix(double x, double y)
        {
            double Clamp(double v) => Math.Clamp(v, -noiseLimit, noiseLimit);
            double Gaussian() => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
            (driftX, driftY) = (Clamp((0.9 * driftX) + (noiseLimit / 4 * Gaussian())), Clamp((0.9 * driftY) + (noiseLimit / 4 * Gaussian())));
            var (lat, lon) = Utm34.Inverse(OriginX + x + driftX, OriginY + y + driftY);
            points.Add(new ReplayPoint(points.Count, StartMs + (points.Count * 1_000L), Math.Round(lat, 7), Math.Round(lon, 7), 5, 0));
        }

        for (var i = 0; i + 1 < vertices.Count; i++)
        {
            var (from, to) = (vertices[i], vertices[i + 1]);
            var steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2)) / 1.4));
            for (var s = 0; s < steps; s++)
            {
                var t = (double)s / steps;
                Fix(from.X + ((to.X - from.X) * t), from.Y + ((to.Y - from.Y) * t));
            }
        }

        Fix(vertices[^1].X, vertices[^1].Y);
        return points;
    }

    /// <summary>Номер точки не раньше <paramref name="from"/>, ближайшей к месту (x, y) без шума.</summary>
    public static int Nearest(IReadOnlyList<ReplayPoint> points, double x, double y, int from = 0) =>
        Enumerable.Range(from, points.Count - from).MinBy(i =>
        {
            var (easting, northing) = Utm34.Forward(points[i].Lat, points[i].Lon);
            return Math.Pow(easting - OriginX - x, 2) + Math.Pow(northing - OriginY - y, 2);
        });

    public static FoundLoop Loop(int start, int end, string closure = "proximity") => new(start, end, closure, 0, 20, 0, 5, 5);

    public static ReplayRun Run(IReadOnlyList<ReplayPoint> points, params FoundLoop[] loops) =>
        new("00000000-0000-4000-8000-000000000001", "run", true, points, [], [], 0, new JudgeCounts(points.Count, 0, 0), [],
            [new DetectorRun(new LoopDetectorSettings(), loops)]);

    public static IReadOnlyList<(double X, double Y)> Square { get; } = [(0, 0), (60, 0), (60, 60), (0, 60), (0, 0), (-40, -40)];

    public static IReadOnlyList<(double X, double Y)> Strip { get; } = [(0, 0), (300, 0), (300, 12), (0, 12), (-40, 52)];

    /// <summary>Три четверти круга радиусом 50 м: концы в ≈ 71 м друг от друга.</summary>
    public static IReadOnlyList<(double X, double Y)> Arc { get; } =
        [.. Enumerable.Range(0, 55).Select(i => (50 * Math.Cos(i * 5 * Math.PI / 180), 50 * Math.Sin(i * 5 * Math.PI / 180)))];
}
