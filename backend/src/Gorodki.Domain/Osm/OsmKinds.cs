namespace Gorodki.Domain.Osm;

/// <summary>
/// Вид маски — земли, которая не захватывается (PLAN.md, §3.3; docs/architecture/osm-pipeline.md, «Маски»). Каждый вид
/// отдельно: причину отказа можно объяснить игроку, а виды можно по-разному применять в других правилах. Числа хранятся в
/// базе (<c>masks.kind</c>) — не менять.
/// </summary>
public enum MaskKind : short
{
    /// <summary>Вода: <c>natural=water</c>, <c>waterway=riverbank</c>, <c>landuse=reservoir|basin</c>.</summary>
    Water = 1,

    /// <summary>Железная дорога: буфер путей ±<c>RailHalfWidth</c> и площади <c>landuse=railway</c>.</summary>
    Rail = 2,

    /// <summary>«Магистрали»: <c>motorway</c> всегда, <c>trunk</c> без пешеходного доступа, ±<c>MajorRoadHalfWidth</c>.</summary>
    MajorRoad = 3,

    /// <summary>Военные объекты: <c>landuse=military</c>, <c>military=*</c> и ручные добавления.</summary>
    Military = 4,

    /// <summary>Кладбища: <c>landuse=cemetery</c>, <c>amenity=grave_yard</c>.</summary>
    Cemetery = 5,

    /// <summary>Погранполоса: пока нет ширины из официальных правил, слоя нет (вопрос 1.3).</summary>
    Border = 6,

    /// <summary>Мемориалы — по списку Никиты: площадь по контуру, точечный памятник — круг 50 м.</summary>
    Memorial = 7,

    /// <summary>Вне игрового поля — только если захват ограничен городом или Ареной (вопрос 6.4).</summary>
    Outside = 8,
}

/// <summary>Район для «% по районам» (PLAN.md, §3.10) и для Арены (§3.6). Числа хранятся в базе (<c>districts.kind</c>).</summary>
public enum DistrictKind : short
{
    /// <summary>Весь город — «% Бреста».</summary>
    City = 1,

    /// <summary>Административный район (Ленинский, Московский).</summary>
    District = 2,

    /// <summary>Арена БрГТУ.</summary>
    Arena = 3,

    /// <summary>Квартал Арены.</summary>
    Quarter = 4,
}

/// <summary>Ценность земли для очков (§3.5: «поле, лес, промзона 0,5»). Число хранится в базе (<c>land_zones.kind</c>).</summary>
public enum LandKind : short
{
    /// <summary>Поле, лес, промзона: ×0,5.</summary>
    LowValue = 1,
}

/// <summary>Где можно захватывать (вопрос 6.4): от этого зависит маска «вне игрового поля».</summary>
public enum PlayZone : short
{
    /// <summary>Везде, как сейчас: маски «вне поля» нет.</summary>
    Anywhere = 0,

    /// <summary>Только в городе: всё вне границы Бреста — «вне поля».</summary>
    City = 1,

    /// <summary>Только в Арене.</summary>
    Arena = 2,
}

/// <summary>Имена видов в файлах набора и в параметрах конвейера.</summary>
public static class OsmCodes
{
    public static string Of(MaskKind kind) => kind switch
    {
        MaskKind.Water => "water",
        MaskKind.Rail => "rail",
        MaskKind.MajorRoad => "major_road",
        MaskKind.Military => "military",
        MaskKind.Cemetery => "cemetery",
        MaskKind.Border => "border",
        MaskKind.Memorial => "memorial",
        MaskKind.Outside => "outside",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный вид маски."),
    };

    public static MaskKind MaskKindOf(string code) =>
        Enum.GetValues<MaskKind>().FirstOrDefault(k => Of(k) == code) is var kind && Of(kind) == code
            ? kind
            : throw new FormatException($"Неизвестный вид маски: {code}.");

    public static string Of(DistrictKind kind) => kind switch
    {
        DistrictKind.City => "city",
        DistrictKind.District => "district",
        DistrictKind.Arena => "arena",
        DistrictKind.Quarter => "quarter",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный вид района."),
    };

    public static DistrictKind DistrictKindOf(string code) =>
        Enum.GetValues<DistrictKind>().FirstOrDefault(k => Of(k) == code) is var kind && Of(kind) == code
            ? kind
            : throw new FormatException($"Неизвестный вид района: {code}.");

    public static string Of(PlayZone zone) => zone switch
    {
        PlayZone.Anywhere => "anywhere",
        PlayZone.City => "city",
        PlayZone.Arena => "arena",
        _ => throw new ArgumentOutOfRangeException(nameof(zone), zone, "Неизвестная зона игры."),
    };

    public static PlayZone PlayZoneOf(string code) =>
        Enum.GetValues<PlayZone>().FirstOrDefault(k => Of(k) == code) is var zone && Of(zone) == code
            ? zone
            : throw new FormatException($"Неизвестная зона игры: {code}.");
}
