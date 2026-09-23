using Gorodki.Domain.Geo;
using Gorodki.Domain.Runs;
using NetTopologySuite.Geometries;

namespace Gorodki.Domain.Territory;

/// <summary>Сколько засчитанного пути прошло внутри куска и когда игрок был в нём последний раз.</summary>
/// <param name="Meters">Метров пути внутри куска.</param>
/// <param name="LastTimeMs">Время последней точки пути, задевшего кусок (по часам телефона, мс).</param>
public sealed record PieceVisit(double Meters, long LastTimeMs);

/// <summary>
/// Визиты (PLAN.md, §3.3): «визит — ≥50 м следа внутри участка или повторный захват; не считаются первые и последние
/// 200 м забега и приватные зоны». Здесь — сколько засчитанного пути (<see cref="JudgedPath"/>) прошло внутри каждого куска;
/// само правило визита — <see cref="CaptureRules.Visit"/>.
/// </summary>
public static class Visits
{
    /// <summary>Участок пути в UTM 34N и время его конца.</summary>
    public readonly record struct Step(Coordinate From, Coordinate To, long TimeMs);

    /// <summary>
    /// Засчитанный путь без первых и последних <paramref name="trimMeters"/> (по длине пути): начало и конец забега чаще всего —
    /// у дома, и визит по ним выдал бы, где человек живёт.
    /// </summary>
    public static IReadOnlyList<Step> TrimmedPath(IEnumerable<(TrackPoint From, TrackPoint To)> segments, double trimMeters)
    {
        var steps = segments
            .Select(s =>
            {
                var (fromX, fromY) = Utm34.Forward(s.From.Latitude, s.From.Longitude);
                var (toX, toY) = Utm34.Forward(s.To.Latitude, s.To.Longitude);
                return new Step(new Coordinate(fromX, fromY), new Coordinate(toX, toY), s.To.TimeMs);
            })
            .ToList();
        var total = steps.Sum(s => s.From.Distance(s.To));
        if (total <= 2 * trimMeters)
        {
            return []; // весь забег — в зоне «у дома»
        }

        var result = new List<Step>();
        var walked = 0.0;
        foreach (var step in steps)
        {
            var length = step.From.Distance(step.To);
            var start = Math.Max(walked, trimMeters);
            var end = Math.Min(walked + length, total - trimMeters);
            if (end > start && length > 0)
            {
                result.Add(step with
                {
                    From = Along(step, (start - walked) / length),
                    To = Along(step, (end - walked) / length),
                });
            }

            walked += length;
        }

        return result;
    }

    /// <summary>Сколько пути прошло внутри каждого куска (по номеру в списке); куски без пути не входят.</summary>
    public static IReadOnlyDictionary<int, PieceVisit> Inside(IReadOnlyList<Step> path, IReadOnlyList<Polygon> pieces)
    {
        var result = new Dictionary<int, PieceVisit>();
        if (path.Count == 0)
        {
            return result;
        }

        var lines = path.Select(s => (Step: s, Line: GeoOps.Factory.CreateLineString([s.From, s.To]))).ToList();
        for (var i = 0; i < pieces.Count; i++)
        {
            var piece = pieces[i];
            var meters = 0.0;
            long lastTime = 0;
            foreach (var (step, line) in lines)
            {
                if (!piece.EnvelopeInternal.Intersects(line.EnvelopeInternal))
                {
                    continue;
                }

                var inside = GeoOps.LengthInside(line, piece);
                if (inside > 0)
                {
                    meters += inside;
                    lastTime = Math.Max(lastTime, step.TimeMs);
                }
            }

            if (meters > 0)
            {
                result[i] = new PieceVisit(meters, lastTime);
            }
        }

        return result;
    }

    private static Coordinate Along(Step step, double fraction) =>
        new(step.From.X + ((step.To.X - step.From.X) * fraction), step.From.Y + ((step.To.Y - step.From.Y) * fraction));
}
