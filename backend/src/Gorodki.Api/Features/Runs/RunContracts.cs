using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Config;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Runs;

namespace Gorodki.Api.Features.Runs;

// Всё время в API забегов — миллисекунды Unix по часам телефона (целые числа): так нет расхождений в точности дат
// между .NET и Swift, а сдвиг часов телефона сервер считает сам по полю sentAtMs.

/// <summary>Старт забега. Телефон создаёт идентификатор сам, поэтому забег можно начать без сети и отправить позже.</summary>
/// <param name="Id">UUID забега, созданный телефоном. Повтор того же запроса безопасен.</param>
/// <param name="ConfigVersion">Версия конфига, полученная при «Старте» (<c>GET /config</c>).</param>
/// <param name="StartedAtMs">Начало забега по часам телефона.</param>
/// <param name="SentAtMs">Момент отправки запроса по часам телефона — по нему сервер узнаёт сдвиг часов.</param>
/// <param name="DeviceId">Идентификатор установки из Keychain.</param>
/// <param name="AppVersion">Версия приложения, например <c>0.3.1 (42)</c>.</param>
/// <param name="MotionAuthorized">Разрешён ли доступ к «Движению».</param>
public sealed record StartRunRequest(
    Guid Id,
    League League,
    RunSource Source,
    int ConfigVersion,
    long StartedAtMs,
    long SentAtMs,
    Guid DeviceId,
    string AppVersion,
    bool MotionAuthorized);

/// <summary>Точка следа.</summary>
/// <param name="Seq">Номер точки в забеге: подряд, с нуля.</param>
/// <param name="T">Время точки, мс.</param>
/// <param name="Acc">Горизонтальная точность, м (точки с отрицательной точностью телефон отбрасывает до нумерации).</param>
/// <param name="Speed">Скорость, м/с; <c>null</c>, если неизвестна.</param>
/// <param name="Flags">Биты: 1 — координаты подставлены программой, 2 — от внешнего устройства.</param>
public sealed record TrackPointDto(int Seq, long T, double Lat, double Lon, double Acc, double? Speed, int Flags);

/// <summary>С момента <c>T</c> CoreMotion считает, что владелец движется так-то.</summary>
public sealed record MotionSampleDto(long T, MotionActivity Activity);

/// <summary>Шаги за интервал; <c>Steps = null</c> — шагомер не знает (это не ноль).</summary>
public sealed record StepSampleDto(long Start, long End, int? Steps);

/// <summary>Кусок забега. Границы куска телефон фиксирует при первой попытке отправки и повторяет тот же кусок без изменений.</summary>
/// <param name="SentAtMs">Момент отправки по часам телефона.</param>
/// <param name="SensorsCompleteThroughMs">До этого момента телефон уже отправил все данные движения и шагомера.</param>
public sealed record UploadChunkRequest(
    long SentAtMs,
    long SensorsCompleteThroughMs,
    IReadOnlyList<TrackPointDto>? Points,
    IReadOnlyList<MotionSampleDto>? Motion,
    IReadOnlyList<StepSampleDto>? Steps);

/// <summary>Завершение забега.</summary>
/// <param name="LastSeq">Номер последней точки (−1, если точек не было): по нему видно, каких кусков ещё нет.</param>
public sealed record FinishRunRequest(long EndedAtMs, int LastSeq, long SentAtMs);

/// <summary>Кусок принят.</summary>
/// <param name="Duplicate">Такой же кусок уже был — повтор ничего не изменил.</param>
public sealed record ChunkReceipt(int FirstSeq, int LastSeq, bool Duplicate);

/// <summary>Забег, каким его видит сервер.</summary>
/// <param name="Received">Какие точки уже есть (соседние куски склеены).</param>
/// <param name="Missing">Каких точек не хватает до <c>LastSeq</c> — их нужно дослать (пусто, пока забег не завершён).</param>
/// <param name="Newcomer">
/// Первый забег новичка: весь забег судится с порогом точности <c>capture.newcomerMaxAccuracyMeters</c> из конфига (35 м)
/// вместо порога лиги — так же должен судить и телефон.
/// </param>
public sealed record RunResponse(
    Guid Id,
    League League,
    RunSource Source,
    int ConfigVersion,
    long StartedAtMs,
    long? EndedAtMs,
    RunStatus Status,
    int? LastSeq,
    int ProcessedSeq,
    IReadOnlyList<SeqRange> Received,
    IReadOnlyList<SeqRange> Missing,
    bool Newcomer);

/// <summary>
/// Лимиты приёма забегов: защищают бесплатную базу (500 МБ; при переполнении она только для чтения — встанет вся игра)
/// от скрипта, который шлёт мусор (PLAN.md, §3.9, слой 4).
/// </summary>
public static class RunLimits
{
    public const string RateLimitPolicy = "runs";

    public const int MaxPointsPerChunk = 1_200;

    public const int MaxSamplesPerChunk = 1_200;

    /// <summary>Чаще двух точек в секунду GPS телефона не пишет.</summary>
    public const int MaxPointsPerSecond = 2;

    public const int MaxChunksPerRun = 400;

    /// <summary>Байт кусков на забег: 4 часа по точке в секунду — около 0,4 МБ вместе с датчиками.</summary>
    public const int MaxBytesPerRun = 1_000_000;

    public const long MaxBytesPerUserPerDay = 5_000_000;

    public const int MaxRunsPerDay = 10;

    public const long MaxRequestBytes = 1_000_000;

    /// <summary>Куски принимаются неделю после того, как сервер узнал о забеге (офлайн-выгрузка).</summary>
    public static readonly TimeSpan UploadWindow = TimeSpan.FromDays(7);

    /// <summary>Сильнее часы телефона сбиты быть не могут — просим включить автоматическое время.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromHours(12);

    /// <summary>Старой версией конфига можно начать забег ещё неделю после её замены (телефон мог быть без сети).</summary>
    public static readonly TimeSpan ConfigGrace = TimeSpan.FromDays(7);

    public static int MaxSeq(GameConfig config) => (int)(config.Capture.MaxRunHours * 3600 * MaxPointsPerSecond);
}
