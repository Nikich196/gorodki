using System.Globalization;
using System.Text;
using Gorodki.Domain.Geo;
using NetTopologySuite.Geometries;

namespace Gorodki.OsmPipeline.Tests;

/// <summary>
/// Синтетический «город» у Бреста: рамка — 2×2 тайла UTM (684–685 × 5774–5775), город — квадрат внутри неё. Объекты OSM
/// строятся прямо в UTM на сетке 0,1 м (как их отдаёт <see cref="GeoJsonSeq"/>), без osmium и без данных OSM.
/// </summary>
internal static class Synthetic
{
    public const double X0 = 684_000;

    public const double Y0 = 5_774_000;

    public static readonly TileRange Tiles = new(684, 5774, 685, 5775);

    /// <summary>Город: квадрат 1,8 × 1,8 км с полями 100 м до края рамки.</summary>
    public static Polygon City() => Rectangle(X0 + 100, Y0 + 100, X0 + 1_900, Y0 + 1_900);

    private static long _nextId = 1_000;

    public static PipelineParams Params(Action<PipelineParamsBuilder>? change = null)
    {
        var builder = new PipelineParamsBuilder();
        change?.Invoke(builder);
        return builder.Build();
    }

    public static OsmInput Input(params OsmFeature[] features) => new() { Features = features, City = City(), CityOsmId = 1 };

    public static OsmFeature Way(string tags, params (double X, double Y)[] points) =>
        new("way", Interlocked.Increment(ref _nextId), Tags(tags), GeoOps.Factory.CreateLineString([.. points.Select(p => new Coordinate(X0 + p.X, Y0 + p.Y))]));

    public static OsmFeature Area(string tags, Geometry geometry, string type = "way") =>
        new(type, Interlocked.Increment(ref _nextId), Tags(tags), geometry);

    public static OsmFeature Node(string tags, double x, double y) =>
        new("node", Interlocked.Increment(ref _nextId), Tags(tags), GeoOps.Factory.CreatePoint(new Coordinate(X0 + x, Y0 + y)));

    /// <summary>Прямоугольник в метрах от угла рамки.</summary>
    public static Polygon Box(double x1, double y1, double x2, double y2) => Rectangle(X0 + x1, Y0 + y1, X0 + x2, Y0 + y2);

    public static Polygon Rectangle(double minX, double minY, double maxX, double maxY) => GeoOps.Factory.CreatePolygon(
    [
        new Coordinate(minX, minY), new Coordinate(maxX, minY), new Coordinate(maxX, maxY), new Coordinate(minX, maxY), new Coordinate(minX, minY),
    ]);

    /// <summary>Точка в метрах от угла рамки.</summary>
    public static Point At(double x, double y) => GeoOps.Factory.CreatePoint(new Coordinate(X0 + x, Y0 + y));

    /// <summary>Теги вида <c>"highway=footway;foot=no"</c>.</summary>
    public static IReadOnlyDictionary<string, string> Tags(string tags) =>
        tags.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Split('=', 2))
            .ToDictionary(kv => kv[0], kv => kv[1], StringComparer.Ordinal);

    /// <summary>Строка GeoJSONSeq, как её пишет <c>osmium export -a type,id</c>: координаты — градусы из UTM (метры от угла рамки).</summary>
    public static string GeoJsonLine(string type, long id, string tags, string geometryType, string coordinates) =>
        $"\u001e{{\"type\":\"Feature\",\"geometry\":{{\"type\":\"{geometryType}\",\"coordinates\":{coordinates}}},\"properties\":{{\"@type\":\"{type}\",\"@id\":{id}{string.Concat(Tags(tags).Select(kv => $",\"{kv.Key}\":\"{kv.Value}\""))}}}}}";

    public static string LonLat(params (double X, double Y)[] points)
    {
        var text = new StringBuilder("[");
        for (var i = 0; i < points.Length; i++)
        {
            var (latitude, longitude) = Utm34.Inverse(X0 + points[i].X, Y0 + points[i].Y);
            text.Append(CultureInfo.InvariantCulture, $"{(i > 0 ? "," : "")}[{longitude:R},{latitude:R}]");
        }

        return text.Append(']').ToString();
    }
}

/// <summary>Параметры для тестов: по умолчанию — как в osm-pipeline.json (решения 25.09), рамка — у синтетического города.</summary>
internal sealed class PipelineParamsBuilder
{
    public double? BorderStripMeters { get; set; }

    public Gorodki.Domain.Osm.PlayZone PlayZone { get; set; } = Gorodki.Domain.Osm.PlayZone.Anywhere;

    public List<string> Memorials { get; } = [];

    public List<long> SidewalkWayIds { get; } = [];

    public ArenaRecipe? Arena { get; set; }

    public PipelineParams Build()
    {
        var (south, west) = Utm34.Inverse(Synthetic.X0, Synthetic.Y0);
        var (north, east) = Utm34.Inverse(Synthetic.X0 + 2_000, Synthetic.Y0 + 2_000);
        return new PipelineParams
        {
            Frame = new Frame(west, south, east, north),
            CityRelationId = 1,
            CountryRelationId = 2,
            BorderStripMeters = BorderStripMeters,
            PlayZone = PlayZone,
            Memorials = new MemorialParams { Objects = Memorials },
            MajorRoads = new MajorRoadParams { SidewalkWayIds = SidewalkWayIds },
            Arena = Arena,
        };
    }
}
