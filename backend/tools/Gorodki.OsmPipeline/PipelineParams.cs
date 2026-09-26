using System.Text.Json;
using System.Text.Json.Serialization;
using Gorodki.Domain.Osm;

namespace Gorodki.OsmPipeline;

/// <summary>
/// Параметры конвейера (<c>osm-pipeline.json</c> рядом с кодом). Числа и списки тегов — из решений 25.09
/// (docs/decisions/osm-questions.md) и PLAN.md, §3.3, §3.6, §3.10; чего в решениях нет, остаётся пустым или выключенным.
/// Параметры входят в отпечаток набора и в <c>metadata.json</c> — это «метод» для ODbL 4.6.
/// </summary>
public sealed record PipelineParams
{
    /// <summary>Рамка вырезки из выгрузки Беларуси, градусы: город с запасом, чтобы буферы у края видели соседей.</summary>
    public required Frame Frame { get; init; }

    /// <summary>Отношение границы города (<c>boundary=administrative</c>) — проверено на данных, не угадано.</summary>
    public required long CityRelationId { get; init; }

    /// <summary>Отношение границы страны (<c>admin_level=2</c>): линия госграницы для погранполосы.</summary>
    public required long CountryRelationId { get; init; }

    /// <summary>Административные районы для «% по районам» (вопрос 6.3: Ленинский и Московский).</summary>
    public IReadOnlyList<long> DistrictRelationIds { get; init; } = [];

    public PedestrianParams Pedestrian { get; init; } = new();

    public MajorRoadParams MajorRoads { get; init; } = new();

    public RailParams Rail { get; init; } = new();

    public AreaTagParams Areas { get; init; } = new();

    /// <summary>
    /// Погранполоса: ширина по обе стороны линии госграницы, метры. Из официальных правил пограничного режима, а не на
    /// глаз (вопрос 1.3); пока числа нет — <c>null</c>, и слоя погранполосы в наборе нет.
    /// </summary>
    public double? BorderStripMeters { get; init; }

    public MemorialParams Memorials { get; init; } = new();

    /// <summary>
    /// Где можно захватывать (вопрос 6.4): <c>anywhere</c> — маски «вне поля» нет; <c>city</c> — только Брест;
    /// <c>arena</c> — только Арена.
    /// </summary>
    [JsonConverter(typeof(PlayZoneConverter))]
    public PlayZone PlayZone { get; init; } = PlayZone.Anywhere;

    /// <summary>Отрезков на четверть окружности у буферов (в NTS по умолчанию 8).</summary>
    public int QuadrantSegments { get; init; } = 8;

    /// <summary>Упрощение масок, метры; 0 — без упрощения (проект: включать только по замеру).</summary>
    public double MaskSimplifyMeters { get; init; }

    /// <summary>Id отношений-площадей, которые можно не собрать (с причиной), — только вне города.</summary>
    public IReadOnlyList<KnownBrokenRelation> KnownBrokenRelations { get; init; } = [];

    /// <summary>
    /// Id замкнутых путей темы площадей, которые можно не собрать в многоугольник (с причиной), — как
    /// <see cref="KnownBrokenRelations"/>, но для путей.
    /// </summary>
    public IReadOnlyList<KnownBrokenWay> KnownBrokenWays { get; init; } = [];

    /// <summary>
    /// Разобранный непустой журнал ошибок темы площадей (<c>areas.errors.txt</c>): SHA-256 его содержимого и причина.
    /// Совпал — журнал идёт в примечания, иначе непустой журнал — нарушение. Привязка к содержимому, а не просто
    /// «разрешено»: новый журнал при пересборке снова требует разбора.
    /// </summary>
    public KnownErrorLog? KnownAreaErrors { get; init; }

    /// <summary>Арена и кварталы — «рецепт» (id зданий, улицы разреза, метки кварталов), а не геометрия.</summary>
    public ArenaRecipe? Arena { get; init; }

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static PipelineParams FromJson(string json) =>
        JsonSerializer.Deserialize<PipelineParams>(json, JsonOptions) ?? throw new JsonException("Пустые параметры конвейера.");

    /// <summary>Канонический вид — для отпечатка набора и <c>metadata.json</c>.</summary>
    public string ToCanonicalJson() => JsonSerializer.Serialize(this, JsonOptions);

    private sealed class PlayZoneConverter : JsonConverter<PlayZone>
    {
        public override PlayZone Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            OsmCodes.PlayZoneOf(reader.GetString() ?? "");

        public override void Write(Utf8JsonWriter writer, PlayZone value, JsonSerializerOptions options) =>
            writer.WriteStringValue(OsmCodes.Of(value));
    }
}

/// <summary>Рамка в градусах WGS 84.</summary>
public sealed record Frame(double West, double South, double East, double North)
{
    public string OsmiumBox => FormattableString.Invariant($"{West},{South},{East},{North}");
}

/// <summary>Пешеходные пути для «достижимого» (вопрос 2, вариант Б; <c>cycleway</c> и <c>service</c> — в списке).</summary>
public sealed record PedestrianParams
{
    /// <summary>Линии этих классов <c>highway</c> — пешеходные (если нет запрета, ниже).</summary>
    public IReadOnlyList<string> Highways { get; init; } =
    [
        "footway", "path", "pedestrian", "steps", "living_street", "residential", "service", "unclassified", "tertiary",
        "secondary", "primary", "track", "cycleway",
    ];

    /// <summary>Эти классы — пешеходные только с пешеходным доступом (тот же признак, что у «магистралей», вопрос 1.2).</summary>
    public IReadOnlyList<string> HighwaysWithAccess { get; init; } = ["trunk", "trunk_link"];

    /// <summary>Полоса вокруг оси пути: радиус открытия тумана (<c>ExplorationConfig.RevealRadiusMeters</c>).</summary>
    public double BufferMeters { get; init; } = 25;
}

/// <summary>«Магистрали» (вопрос 1.2, вариант А, 12 м).</summary>
public sealed record MajorRoadParams
{
    /// <summary>В маске всегда (кроме тоннелей).</summary>
    public IReadOnlyList<string> Always { get; init; } = ["motorway", "motorway_link"];

    /// <summary>В маске, если нет пешеходного доступа (нет тротуара — тега <c>sidewalk</c> нет или он no/none — и нет foot=yes/designated).</summary>
    public IReadOnlyList<string> WithoutPedestrianAccess { get; init; } = ["trunk", "trunk_link"];

    /// <summary>Полуширина маски от оси, метры (§3.11: «≤12 м от оси автодороги»).</summary>
    public double HalfWidthMeters { get; init; } = 12;

    /// <summary>Пути OSM, у которых тротуар есть, а тега нет, — по решению Никиты (это наши данные).</summary>
    public IReadOnlyList<long> SidewalkWayIds { get; init; } = [];
}

/// <summary>Ж/д (вопрос 1.1, вариант А, 10 м).</summary>
public sealed record RailParams
{
    public IReadOnlyList<string> Railways { get; init; } = ["rail", "light_rail", "narrow_gauge"];

    public double HalfWidthMeters { get; init; } = 10;
}

/// <summary>Площадные темы: тег <c>ключ=значение</c> или <c>ключ=*</c> (любое значение).</summary>
public sealed record AreaTagParams
{
    public IReadOnlyList<string> Water { get; init; } = ["natural=water", "waterway=riverbank", "landuse=reservoir", "landuse=basin"];

    public IReadOnlyList<string> RailAreas { get; init; } = ["landuse=railway"];

    public IReadOnlyList<string> Military { get; init; } = ["landuse=military", "military=*"];

    public IReadOnlyList<string> Cemetery { get; init; } = ["landuse=cemetery", "amenity=grave_yard"];

    /// <summary>Пешеходные площади: <c>highway=pedestrian</c> с <c>area=yes</c> или отношением и <c>place=square</c>.</summary>
    public IReadOnlyList<string> PedestrianAreas { get; init; } = ["highway=pedestrian", "place=square"];

    /// <summary>
    /// Ценность земли ×0,5 (§3.5: «поле, лес, промзона»; вопрос 6.8 — в v1). Стартовый список — теги из вопроса 6.8,
    /// окончательный утверждает Никита.
    /// </summary>
    public IReadOnlyList<string> LowValueLand { get; init; } = ["landuse=farmland", "landuse=forest", "landuse=industrial", "natural=wood"];
}

/// <summary>Мемориалы — по списку, утверждённому Никитой (вопрос 6.6). Пока списка нет — слой пуст.</summary>
public sealed record MemorialParams
{
    /// <summary>Объекты OSM вида <c>way/123</c>, <c>relation/45</c>, <c>node/67</c>.</summary>
    public IReadOnlyList<string> Objects { get; init; } = [];

    /// <summary>Точечный памятник — круг этого радиуса (§3.11: «мемориалы +50 м»).</summary>
    public double PointRadiusMeters { get; init; } = 50;
}

/// <summary>Отношение, которое можно не собрать, и почему.</summary>
public sealed record KnownBrokenRelation(long Id, string Reason);

/// <summary>Замкнутый путь, который можно не собрать, и почему.</summary>
public sealed record KnownBrokenWay(long Id, string Reason);

/// <summary>Журнал ошибок osmium, который разобран и принят: SHA-256 содержимого (строчные hex) и почему он не страшен.</summary>
public sealed record KnownErrorLog(string Sha256, string Reason);

/// <summary>
/// «Рецепт» Арены (вопрос 3): здания-якоря, радиус-ориентир, улицы разреза и метки кварталов. Геометрию строит конвейер.
/// Пока Никита не утвердил, <see cref="Proposal"/> = true — в наборе Арена и кварталы помечены как предложение.
/// </summary>
public sealed record ArenaRecipe
{
    public bool Proposal { get; init; } = true;

    /// <summary>Корпуса и общежития БрГТУ — объекты OSM вида <c>way/123</c>.</summary>
    public IReadOnlyList<string> Anchors { get; init; } = [];

    /// <summary>~1,5 км вокруг корпусов и общежитий (§3.6) — ориентир, граница идёт по улицам.</summary>
    public double RadiusMeters { get; init; } = 1500;

    /// <summary>Грань сети улиц входит в Арену, если хотя бы такая доля её площади — в пределах радиуса.</summary>
    public double MinShareInside { get; init; } = 0.5;

    /// <summary>Улицы разреза — значения тега <c>name</c> (все пути с этим именем).</summary>
    public IReadOnlyList<string> CutStreets { get; init; } = [];

    /// <summary>Дополнительные пути разреза по id (перемычки без имени).</summary>
    public IReadOnlyList<long> CutWayIds { get; init; } = [];

    /// <summary>Кварталы: название и точка внутри (наши данные, не производные OSM).</summary>
    public IReadOnlyList<QuarterLabel> Quarters { get; init; } = [];
}

public sealed record QuarterLabel(string Name, double Lat, double Lon);
