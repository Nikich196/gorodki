namespace Gorodki.Domain.Runs;

/// <summary>Признаки точки от системы геопозиции.</summary>
[Flags]
public enum PointFlags : byte
{
    None = 0,

    /// <summary>iOS сообщила, что координаты подставлены программой (<c>isSimulatedBySoftware</c>). Это подсказка для доверия, не приговор.</summary>
    Simulated = 1,

    /// <summary>Координаты пришли от внешнего устройства (<c>isProducedByAccessory</c>).</summary>
    ProducedByAccessory = 2,
}

/// <summary>Вид движения по данным CoreMotion. Числа — часть формата хранения, их не менять.</summary>
public enum MotionActivity : byte
{
    Unknown = 0,
    Stationary = 1,
    Walking = 2,
    Running = 3,
    Cycling = 4,
    Automotive = 5,
}

/// <summary>
/// Точка GPS-следа в том виде, в каком её хранит сервер: координаты с шагом 1e-7° (около 1 см), точность в дециметрах,
/// скорость в см/с. Всё приводится к этим шагам сразу при создании, поэтому точка после записи и чтения — та же самая,
/// а повторная отправка тех же измерений даёт тот же хэш куска.
/// Округление середины — «от нуля», как <c>rounded()</c> в Swift: телефон приводит точку к тем же шагам
/// (<c>quantizedForStorage</c> в GameCore) и судит те же числа, что потом сервер.
/// </summary>
public readonly record struct TrackPoint
{
    /// <summary>Скорость неизвестна.</summary>
    public const ushort NoSpeed = ushort.MaxValue;

    private TrackPoint(int seq, long timeMs, int latitudeE7, int longitudeE7, ushort accuracyDm, ushort speedCmPerSecond, PointFlags flags)
    {
        Seq = seq;
        TimeMs = timeMs;
        LatitudeE7 = latitudeE7;
        LongitudeE7 = longitudeE7;
        AccuracyDecimeters = accuracyDm;
        SpeedCentimetersPerSecond = speedCmPerSecond;
        Flags = flags;
    }

    /// <summary>Номер точки в забеге: по нему заявка петли ссылается на часть следа.</summary>
    public int Seq { get; }

    /// <summary>Время точки по часам телефона, миллисекунды Unix.</summary>
    public long TimeMs { get; }

    public int LatitudeE7 { get; }

    public int LongitudeE7 { get; }

    public ushort AccuracyDecimeters { get; }

    public ushort SpeedCentimetersPerSecond { get; }

    public PointFlags Flags { get; }

    public double Latitude => LatitudeE7 / 1e7;

    public double Longitude => LongitudeE7 / 1e7;

    public double AccuracyMeters => AccuracyDecimeters / 10.0;

    public double? SpeedMetersPerSecond => SpeedCentimetersPerSecond == NoSpeed ? null : SpeedCentimetersPerSecond / 100.0;

    /// <summary>Точка из измерений телефона. Значения приводятся к шагам хранения; проверка допустимости — отдельно.</summary>
    public static TrackPoint FromMeasurements(
        int seq, long timeMs, double latitude, double longitude, double accuracyMeters, double? speedMetersPerSecond, PointFlags flags)
    {
        // NaN проходит любые сравнения «меньше/больше», поэтому конечность — отдельной проверкой.
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || !double.IsFinite(accuracyMeters))
        {
            throw new ArgumentException("Координаты и точность должны быть конечными числами.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(latitude, -90);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(latitude, 90);
        ArgumentOutOfRangeException.ThrowIfLessThan(longitude, -180);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(longitude, 180);
        ArgumentOutOfRangeException.ThrowIfNegative(accuracyMeters);

        return new TrackPoint(
            seq,
            timeMs,
            (int)Math.Round(latitude * 1e7, MidpointRounding.AwayFromZero),
            (int)Math.Round(longitude * 1e7, MidpointRounding.AwayFromZero),
            Saturate(accuracyMeters * 10),
            speedMetersPerSecond is { } speed && speed >= 0 ? Saturate(speed * 100) : NoSpeed,
            flags);
    }

    /// <summary>Точка из уже квантованных значений — только для чтения из хранилища.</summary>
    internal static TrackPoint FromStored(
        int seq, long timeMs, int latitudeE7, int longitudeE7, ushort accuracyDm, ushort speedCmPerSecond, PointFlags flags) =>
        new(seq, timeMs, latitudeE7, longitudeE7, accuracyDm, speedCmPerSecond, flags);

    /// <summary>Округление в диапазон 0…65534 (65535 занято под «скорость неизвестна»).</summary>
    private static ushort Saturate(double value) => (ushort)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, NoSpeed - 1);
}

/// <summary>С этого момента CoreMotion считает, что владелец движется так-то (до следующей записи).</summary>
public readonly record struct MotionSample(long TimeMs, MotionActivity Activity);

/// <summary>Шаги за интервал по шагомеру. <c>Steps = null</c> — шагомер ничего не знает (это не ноль шагов).</summary>
public readonly record struct StepSample(long StartMs, long EndMs, int? Steps);

/// <summary>
/// Кусок забега: подряд идущие точки с номерами <c>FirstSeq…</c> и данные датчиков, которые телефон успел получить к отправке.
/// </summary>
/// <param name="SensorsCompleteThroughMs">
/// До этого момента (часы телефона, мс Unix) телефон уже передал все записи движения и шагомера. CoreMotion и шагомер
/// отдают данные с задержкой, поэтому записи датчиков не обязаны попадать во время точек этого куска: запоздавшие
/// приходят со следующим куском. Проверка петли ждёт, пока эта отметка дойдёт до её конца.
/// </param>
public sealed record TrackChunk(
    int FirstSeq,
    long SensorsCompleteThroughMs,
    IReadOnlyList<TrackPoint> Points,
    IReadOnlyList<MotionSample> Motion,
    IReadOnlyList<StepSample> Steps)
{
    public int LastSeq => FirstSeq + Points.Count - 1;

    /// <summary>Совпадают ли два куска по содержимому (у записей со списками обычное <c>==</c> сравнивает ссылки на списки).</summary>
    public bool SameContentAs(TrackChunk other) =>
        FirstSeq == other.FirstSeq
        && SensorsCompleteThroughMs == other.SensorsCompleteThroughMs
        && Points.SequenceEqual(other.Points)
        && Motion.SequenceEqual(other.Motion)
        && Steps.SequenceEqual(other.Steps);
}
