using System.Text.Json;
using Gorodki.Domain.Territory;

namespace Gorodki.Calibration;

/// <summary>
/// Выход половины телефона (<c>ios/Tools/LoopReplay</c>, <c>LoopReplayOutput</c>): точки забегов из «Моих данных»
/// и петли, которые нашёл детектор GameCore при каждом варианте чисел. Имена полей — как в Swift.
/// </summary>
/// <param name="Input">Имя файла «Моих данных».</param>
/// <param name="Newcomer">Судья с порогом точности новичка.</param>
public sealed record ReplayFile(string Input, bool Newcomer, IReadOnlyList<ReplayRun> Runs)
{
    /// <summary>Поле, которого нет в файле, — ошибка, а не молчаливый ноль: так видно, что половины разошлись.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
    };

    public static ReplayFile Read(string json) =>
        JsonSerializer.Deserialize<ReplayFile>(json, JsonOptions) ?? throw new JsonException("Пустой файл петель.");
}

/// <param name="Points">Точки подряд с номера 0 — только это начало следа сервер режет на кольца.</param>
/// <param name="PointsAfterGap">Сколько точек после первого пропуска номеров не разбиралось.</param>
/// <param name="ServerCaptures">Итоги заявок на сервере из выгрузки (без номеров точек).</param>
/// <param name="Variants">Петли по вариантам чисел детектора.</param>
public sealed record ReplayRun(
    string Id,
    string League,
    bool MotionAuthorized,
    IReadOnlyList<ReplayPoint> Points,
    IReadOnlyList<ReplayMotion> Motion,
    IReadOnlyList<ReplaySteps> Steps,
    int PointsAfterGap,
    JudgeCounts Judge,
    IReadOnlyList<ServerCapture> ServerCaptures,
    IReadOnlyList<DetectorRun> Variants);

/// <summary>Точка следа, как в «Моих данных»: время — мс Unix, точность — метры.</summary>
public sealed record ReplayPoint(int Seq, long T, double Lat, double Lon, double Acc, int Flags, double? Speed = null);

public sealed record ReplayMotion(long T, string Activity);

public sealed record ReplaySteps(long Start, long End, int? Steps = null);

/// <summary>Вердикты судьи телефона: принято, отброшено, разрывов.</summary>
public sealed record JudgeCounts(int Accepted, int Ignored, int Breaks);

public sealed record ServerCapture(string RunId, string Status, string? RejectCode = null, double? AreaSquareMeters = null);

/// <summary>Числа детектора (те же имена, что у <c>LoopDetectorSettings</c> в GameCore) и найденные петли.</summary>
public sealed record DetectorRun(LoopDetectorSettings Detector, IReadOnlyList<FoundLoop> Loops);

/// <summary>Заявка, которую отправил бы телефон.</summary>
/// <param name="Closure"><c>crossing</c> или <c>proximity</c>.</param>
/// <param name="EstimatedArea">Грубая площадь на телефоне, м².</param>
/// <param name="RadiusMeters">R для концов петли по формуле детектора, м.</param>
/// <param name="GapMeters">Расстояние между концами, м.</param>
public sealed record FoundLoop(
    int StartSeq,
    int EndSeq,
    string Closure,
    double EstimatedArea,
    double RadiusMeters,
    double GapMeters,
    double StartAccuracy,
    double EndAccuracy);
