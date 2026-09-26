using System.Text;
using System.Text.Json;
using Gorodki.Domain.Geo;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;

namespace Gorodki.OsmPipeline;

/// <summary>
/// Объект OSM из <c>osmium export</c>: тип и id (<c>-a type,id</c>), теги и геометрия уже в UTM 34N на сетке 0,1 м.
/// Линия — <see cref="LineString"/>, площадь — (мульти)многоугольник, точка — <see cref="Point"/>.
/// </summary>
public sealed record OsmFeature(string Type, long Id, IReadOnlyDictionary<string, string> Tags, Geometry Geometry)
{
    /// <summary>Ссылка вида <c>way/123</c> — так объекты перечислены в параметрах.</summary>
    public string Ref => $"{Type}/{Id}";

    public string? Tag(string key) => Tags.TryGetValue(key, out var value) ? value : null;

    public bool IsLine => Geometry is LineString;

    public bool IsArea => Geometry is Polygon or MultiPolygon;

    /// <summary>Совпадает ли с правилом <c>ключ=значение</c> или <c>ключ=*</c>.</summary>
    public bool Matches(string rule)
    {
        var (key, value) = Split(rule);
        return Tags.TryGetValue(key, out var actual) && (value == "*" || actual == value);
    }

    public bool MatchesAny(IEnumerable<string> rules) => rules.Any(Matches);

    private static (string Key, string Value) Split(string rule)
    {
        var at = rule.IndexOf('=', StringComparison.Ordinal);
        return at <= 0 ? throw new FormatException($"Правило тега — «ключ=значение»: {rule}") : (rule[..at], rule[(at + 1)..]);
    }
}

/// <summary>Чтение GeoJSONSeq из <c>osmium export -f geojsonseq -a type,id</c>: перевод в UTM 34N и перенос на сетку.</summary>
public static class GeoJsonSeq
{
    private static readonly GeometryFactory Floating = new(new PrecisionModel(), Utm34.Srid);

    /// <param name="wanted">Какие объекты разбирать (тип, id); остальные пропускаются до перевода геометрии.</param>
    public static IEnumerable<OsmFeature> ReadFile(string path, Func<string, long, bool>? wanted = null) =>
        Read(File.ReadLines(path, Encoding.UTF8), wanted);

    public static IEnumerable<OsmFeature> Read(IEnumerable<string> lines, Func<string, long, bool>? wanted = null)
    {
        foreach (var raw in lines)
        {
            // osmium по умолчанию ставит перед записью разделитель RS (0x1E, RFC 8142) — снимаем, если есть.
            var line = raw.TrimStart('\u001e').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (Parse(line, wanted) is { } feature)
            {
                yield return feature;
            }
        }
    }

    public static OsmFeature? Parse(string json, Func<string, long, bool>? wanted = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var properties = root.GetProperty("properties");
        var tags = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string? type = null;
        long id = 0;
        foreach (var property in properties.EnumerateObject())
        {
            switch (property.Name)
            {
                case "@type":
                    type = property.Value.GetString();
                    break;
                case "@id":
                    id = property.Value.GetInt64();
                    break;
                default:
                    tags[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText();
                    break;
            }
        }

        if (type is null)
        {
            throw new FormatException("У объекта нет @type: экспорт должен идти с «-a type,id».");
        }

        if (wanted is not null && !wanted(type, id))
        {
            return null;
        }

        var geometry = ToUtm(root.GetProperty("geometry"));
        return geometry is null || geometry.IsEmpty ? null : new OsmFeature(type, id, tags, geometry);
    }

    private static Geometry? ToUtm(JsonElement geometry)
    {
        var coordinates = geometry.GetProperty("coordinates");
        return geometry.GetProperty("type").GetString() switch
        {
            "Point" => GeoOps.Factory.CreatePoint(Project(coordinates)),
            "LineString" => Line(coordinates),
            "MultiLineString" => null, // osmium даёт такие только для отношений-маршрутов — их темы не берут
            "Polygon" => Area(Floating.CreatePolygon(Shell(coordinates[0]), Holes(coordinates))),
            "MultiPolygon" => Area(Floating.CreateMultiPolygon(
                coordinates.EnumerateArray().Select(p => Floating.CreatePolygon(Shell(p[0]), Holes(p))).ToArray())),
            var other => throw new FormatException($"Неизвестный тип геометрии: {other}"),
        };
    }

    private static Coordinate Project(JsonElement lonLat)
    {
        var (easting, northing) = Utm34.Forward(lonLat[1].GetDouble(), lonLat[0].GetDouble());
        return GeoOps.Snap(new Coordinate(easting, northing));
    }

    /// <summary>Линия на сетке без повторов соседних точек; меньше двух разных точек — не линия.</summary>
    private static LineString? Line(JsonElement points)
    {
        var list = new List<Coordinate>();
        foreach (var point in points.EnumerateArray())
        {
            var c = Project(point);
            if (list.Count == 0 || !list[^1].Equals2D(c))
            {
                list.Add(c);
            }
        }

        return list.Count < 2 ? null : GeoOps.Factory.CreateLineString(list.ToArray());
    }

    private static LinearRing Shell(JsonElement ring) => Ring(ring);

    private static LinearRing[] Holes(JsonElement polygon) =>
        polygon.EnumerateArray().Skip(1).Select(Ring).Where(r => !r.IsEmpty).ToArray();

    private static LinearRing Ring(JsonElement ring)
    {
        var list = new List<Coordinate>();
        foreach (var point in ring.EnumerateArray())
        {
            var (easting, northing) = Utm34.Forward(point[1].GetDouble(), point[0].GetDouble());
            list.Add(new Coordinate(easting, northing));
        }

        if (list.Count > 0 && !list[0].Equals2D(list[^1]))
        {
            list.Add(list[0].Copy());
        }

        return list.Count < 4 ? Floating.CreateLinearRing() : Floating.CreateLinearRing(list.ToArray());
    }

    /// <summary>Многоугольник из UTM без сетки → правильный многоугольник на сетке (или null, если от него ничего не осталось).</summary>
    private static Geometry? Area(Geometry floating)
    {
        var valid = floating.IsValid ? floating : GeometryFixer.Fix(floating);
        var snapped = GeoOps.Polygonal(GeoOps.Factory.CreateGeometry(GeoOps.Snap(valid)));
        return snapped.IsEmpty ? null : snapped;
    }
}
