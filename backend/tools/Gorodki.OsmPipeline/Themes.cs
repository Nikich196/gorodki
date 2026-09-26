using Gorodki.Domain.Osm;

namespace Gorodki.OsmPipeline;

/// <summary>
/// Какие объекты OSM во что идут (docs/architecture/osm-pipeline.md, «Маски», «Общие правила сборки»; решения 25.09).
/// Чистые функции тегов — проверяются тестами без данных OSM.
/// </summary>
/// <remarks>
/// Типы геометрии по темам: пути, ж/д и дороги — только линии; площади — только из тем-площадей. osmium выдаёт замкнутую
/// линию и линией, и многоугольником, и без этого правила замкнутая дорожка вокруг пруда стала бы кругом, а пруд попал бы
/// в знаменатель процента.
/// </remarks>
public sealed class Themes(PipelineParams parameters)
{
    private readonly HashSet<long> _sidewalkWays = [.. parameters.MajorRoads.SidewalkWayIds];

    /// <summary>
    /// Пешеходный доступ у дороги класса <c>trunk</c> (вопрос 1.2, А): есть тротуар (тег <c>sidewalk</c> есть и не no/none,
    /// в том числе <c>separate</c>) или <c>foot=yes|designated</c>; или путь в списке «тротуар есть, а тега нет».
    /// </summary>
    public bool HasPedestrianAccess(OsmFeature way) =>
        _sidewalkWays.Contains(way.Id)
        || way.Tag("sidewalk") is { } sidewalk && sidewalk is not ("no" or "none")
        || way.Tag("foot") is "yes" or "designated";

    /// <summary>Запреты для пешеходных путей (вопрос 2, Б): <c>foot=no</c>; <c>access=no|private</c> без разрешения пешеходам.</summary>
    public static bool IsClosedToPedestrians(OsmFeature way) =>
        way.Tag("foot") == "no"
        || way.Tag("access") is "no" or "private" && way.Tag("foot") is not ("yes" or "designated" or "permissive");

    /// <summary>Тоннель — любое значение <c>tunnel</c>, кроме <c>no</c>: земля над ним обычная.</summary>
    public static bool IsTunnel(OsmFeature way) => way.Tag("tunnel") is { } tunnel && tunnel != "no";

    /// <summary>Линия пешеходного пути для «достижимого». Мосты и подземные переходы входят.</summary>
    public bool IsPedestrianLine(OsmFeature feature)
    {
        if (!feature.IsLine || feature.Tag("highway") is not { } highway || IsClosedToPedestrians(feature))
        {
            return false;
        }

        return parameters.Pedestrian.Highways.Contains(highway)
            || parameters.Pedestrian.HighwaysWithAccess.Contains(highway) && HasPedestrianAccess(feature);
    }

    /// <summary>Пешеходная площадь: <c>highway=pedestrian</c> с <c>area=yes</c> или отношением; <c>place=square</c>.</summary>
    public bool IsPedestrianArea(OsmFeature feature)
    {
        if (!feature.IsArea || IsClosedToPedestrians(feature))
        {
            return false;
        }

        if (feature.Tag("highway") == "pedestrian")
        {
            return parameters.Areas.PedestrianAreas.Contains("highway=pedestrian")
                && (feature.Tag("area") == "yes" || feature.Type == "relation");
        }

        return feature.MatchesAny(parameters.Areas.PedestrianAreas.Where(rule => !rule.StartsWith("highway=", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Ось «магистрали» для маски: <c>motorway</c> всегда; <c>trunk</c> — если он не пешеходный путь. Одно правило с двух
    /// сторон: что в маске, того нет в пешеходных путях, и наоборот (вопрос 1.2). Тоннели не берутся.
    /// </summary>
    public bool IsMajorRoad(OsmFeature feature)
    {
        if (!feature.IsLine || feature.Tag("highway") is not { } highway || IsTunnel(feature))
        {
            return false;
        }

        return parameters.MajorRoads.Always.Contains(highway)
            || parameters.MajorRoads.WithoutPedestrianAccess.Contains(highway) && !IsPedestrianLine(feature);
    }

    /// <summary>Ось ж/д для маски: <c>rail</c>, <c>light_rail</c>, <c>narrow_gauge</c> не в тоннеле.</summary>
    public bool IsRailLine(OsmFeature feature) =>
        feature.IsLine && feature.Tag("railway") is { } railway && parameters.Rail.Railways.Contains(railway) && !IsTunnel(feature);

    /// <summary>Площадь маски, взятая как есть (вода, площади ж/д, военные объекты, кладбища), или null.</summary>
    public MaskKind? AreaMaskKind(OsmFeature feature)
    {
        if (!feature.IsArea)
        {
            return null;
        }

        if (feature.MatchesAny(parameters.Areas.Water))
        {
            return MaskKind.Water;
        }

        if (feature.MatchesAny(parameters.Areas.RailAreas))
        {
            return MaskKind.Rail;
        }

        if (feature.MatchesAny(parameters.Areas.Military))
        {
            return MaskKind.Military;
        }

        return feature.MatchesAny(parameters.Areas.Cemetery) ? MaskKind.Cemetery : null;
    }

    /// <summary>Земля с ценностью ×0,5 (§3.5).</summary>
    public bool IsLowValueLand(OsmFeature feature) => feature.IsArea && feature.MatchesAny(parameters.Areas.LowValueLand);
}
