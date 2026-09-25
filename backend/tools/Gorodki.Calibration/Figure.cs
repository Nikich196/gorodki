using System.Globalization;
using System.Text;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Runs;
using NetTopologySuite.Geometries;

namespace Gorodki.Calibration;

/// <summary>
/// Картинка «след + контур» одной петли — SVG без внешних файлов. Координаты — метры UTM 34N, как в движке, от угла
/// рамки; север сверху.
/// </summary>
public static class Figure
{
    private const double MaxSide = 560;
    private const double MarginMeters = 8;

    public static string Svg(RunEvaluation run, LoopEvaluation loop)
    {
        var (start, end) = (loop.Loop.StartSeq, loop.Loop.EndSeq);
        var first = run.Utm[start];
        var radius = loop.Loop.RadiusMeters;
        var bounds = new Envelope(first.X - radius, first.X + radius, first.Y - radius, first.Y + radius);
        for (var i = start; i <= end; i++)
        {
            bounds.ExpandToInclude(run.Utm[i]);
        }

        if (loop.Contour is { } contour)
        {
            bounds.ExpandToInclude(contour.EnvelopeInternal);
        }

        bounds.ExpandBy(MarginMeters);
        var scale = MaxSide / Math.Max(bounds.Width, bounds.Height);
        string X(Coordinate c) => F((c.X - bounds.MinX) * scale);
        string Y(Coordinate c) => F((bounds.MaxY - c.Y) * scale);
        string P(Coordinate c) => $"{X(c)},{Y(c)}";

        var svg = new StringBuilder();
        var (width, height) = (F(bounds.Width * scale), F(bounds.Height * scale));
        svg.Append(CultureInfo.InvariantCulture, $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" width="{width}" height="{height}" role="img">""");
        svg.Append("""<rect width="100%" height="100%" fill="#fafafa"/>""");

        // Весь забег — фоном: куски между разрывами следа, без отброшенных судьёй точек.
        foreach (var part in Parts(run, 0, run.Utm.Count - 1))
        {
            svg.Append(CultureInfo.InvariantCulture, $"""<polyline points="{string.Join(' ', part.Select(P))}" fill="none" stroke="#c8c8c8" stroke-width="1"/>""");
        }

        if (loop.Contour is { } area)
        {
            var path = new StringBuilder();
            foreach (var polygon in GeoOps.Polygons(area))
            {
                foreach (var ring in new[] { polygon.ExteriorRing }.Concat(polygon.InteriorRings))
                {
                    path.Append('M').AppendJoin(" L", ring.Coordinates.Select(P)).Append(" Z ");
                }
            }

            svg.Append(CultureInfo.InvariantCulture, $"""<path d="{path}" fill="#2a9d8f" fill-opacity="0.25" fill-rule="evenodd" stroke="#2a9d8f" stroke-width="1.5"/>""");
        }

        foreach (var part in Parts(run, start, end))
        {
            svg.Append(CultureInfo.InvariantCulture, $"""<polyline points="{string.Join(' ', part.Select(P))}" fill="none" stroke="#1f5fbf" stroke-width="1.5"/>""");
            foreach (var point in part)
            {
                svg.Append(CultureInfo.InvariantCulture, $"""<circle cx="{X(point)}" cy="{Y(point)}" r="1.6" fill="#1f5fbf"/>""");
            }
        }

        for (var i = start; i <= end; i++)
        {
            if (run.Verdicts[i].Kind == VerdictKind.Ignored)
            {
                var (x, y) = ((run.Utm[i].X - bounds.MinX) * scale, (bounds.MaxY - run.Utm[i].Y) * scale);
                svg.Append(CultureInfo.InvariantCulture, $"""<path d="M{F(x - 3)},{F(y - 3)} L{F(x + 3)},{F(y + 3)} M{F(x - 3)},{F(y + 3)} L{F(x + 3)},{F(y - 3)}" stroke="#888" stroke-width="1.2"/>""");
            }
        }

        var last = run.Utm[end];
        svg.Append(CultureInfo.InvariantCulture, $"""<circle cx="{X(first)}" cy="{Y(first)}" r="{F(radius * scale)}" fill="none" stroke="#2e7d32" stroke-dasharray="5 4"/>""");
        svg.Append(CultureInfo.InvariantCulture, $"""<line x1="{X(first)}" y1="{Y(first)}" x2="{X(last)}" y2="{Y(last)}" stroke="#444" stroke-dasharray="2 2"/>""");
        if (loop.WidestPoint is { } center && loop.HalfWidthMeters is { } half)
        {
            svg.Append(CultureInfo.InvariantCulture, $"""<circle cx="{X(center)}" cy="{Y(center)}" r="{F(half * scale)}" fill="none" stroke="#e76f51" stroke-width="1.5" stroke-dasharray="1 3"/>""");
        }

        svg.Append(CultureInfo.InvariantCulture, $"""<circle cx="{X(first)}" cy="{Y(first)}" r="4.5" fill="#2e7d32"/>""");
        svg.Append(CultureInfo.InvariantCulture, $"""<circle cx="{X(last)}" cy="{Y(last)}" r="4.5" fill="#c62828"/>""");

        // Масштаб: круглое число метров, около четверти ширины.
        var meters = new[] { 1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000 }.LastOrDefault(m => m <= bounds.Width / 4, 1);
        var bar = meters * scale;
        var baseline = (bounds.Height * scale) - 10;
        svg.Append(CultureInfo.InvariantCulture, $"""<line x1="10" y1="{F(baseline)}" x2="{F(10 + bar)}" y2="{F(baseline)}" stroke="#222" stroke-width="2"/>""");
        svg.Append(CultureInfo.InvariantCulture, $"""<text x="10" y="{F(baseline - 5)}" font-size="12" font-family="sans-serif" fill="#222">{meters} м</text>""");
        svg.Append("</svg>");
        return svg.ToString();
    }

    /// <summary>Точки start…end без отброшенных судьёй, разрезанные на разрывах следа.</summary>
    private static List<List<Coordinate>> Parts(RunEvaluation run, int start, int end)
    {
        var parts = new List<List<Coordinate>> { new() };
        for (var i = start; i <= end; i++)
        {
            var kind = run.Verdicts[i].Kind;
            if (kind == VerdictKind.Ignored)
            {
                continue;
            }

            if (kind == VerdictKind.SegmentBroken && parts[^1].Count > 0)
            {
                parts.Add([]);
            }

            parts[^1].Add(run.Utm[i]);
        }

        return [.. parts.Where(p => p.Count > 0)];
    }

    private static string F(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
