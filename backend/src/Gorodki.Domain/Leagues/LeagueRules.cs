namespace Gorodki.Domain.Leagues;

/// <summary>Предел средней скорости за окно: если за последние <c>WindowSeconds</c> средняя скорость выше — это не бег (не велосипед).</summary>
public sealed record SpeedLimit(double WindowSeconds, double MaxKilometersPerHour);

/// <summary>«Разгон машины»: скорость выросла на <c>DeltaKilometersPerHour</c> за <c>WithinSeconds</c> и дошла до <c>ReachingKilometersPerHour</c>.</summary>
public sealed record CarLaunchRule(double DeltaKilometersPerHour, double WithinSeconds, double ReachingKilometersPerHour);

/// <summary>«Транспорт по датчикам»: доля времени automotive за окно и одновременно высокая скорость.</summary>
public sealed record VehicleShareRule(double WindowSeconds, double MinShare, double SpeedWindowSeconds, double MinKilometersPerHour);

/// <summary>
/// Пороги античита одной лиги (PLAN.md, §3.9). Зеркало <c>LeagueRules</c> из GameCore (Swift): те же имена и та же форма JSON,
/// чтобы телефон читал этот раздел конфига напрямую. Совпадение проверяют тесты обеих сторон по <c>contracts/game-config.v1.json</c>.
/// </summary>
public sealed record LeagueRules
{
    // Слой 1 — сама точка.

    /// <summary>Хуже этой точности точка для захвата не используется (для первой петли новичка — отдельный порог).</summary>
    public double MaxAccuracyMeters { get; init; } = 25;

    /// <summary>Точка старше этого — устарела.</summary>
    public double MaxFixAgeSeconds { get; init; } = 10;

    /// <summary>Скачок быстрее этого — «телепорт», след рвётся (180 км/ч, калибруется на полевом тесте).</summary>
    public double TeleportMetersPerSecond { get; init; } = 50;

    // Слой 2 — отрезки.

    public IReadOnlyList<SpeedLimit> SpeedLimits { get; init; } = [];

    /// <summary>Бег: «транспорт» по датчикам дольше этого — разрыв.</summary>
    public double? VehicleSeconds { get; init; }

    /// <summary>Бег: «велосипед» по датчикам дольше этого — разрыв и предложение перейти в лигу «Вело».</summary>
    public double? CyclingSeconds { get; init; }

    public VehicleShareRule? VehicleShare { get; init; }

    public CarLaunchRule? CarLaunch { get; init; }

    /// <summary>Бег: ноль шагов за это окно при скорости не ниже <see cref="NoStepsMinSpeed"/> — разрыв.</summary>
    public double? NoStepsWindowSeconds { get; init; }

    public double NoStepsMinSpeed { get; init; } = 1.0;

    /// <summary>Бег: допустимая длина шага <c>[от, до]</c>, метры (так Swift кодирует <c>ClosedRange</c>).</summary>
    public IReadOnlyList<double>? StrideMeters { get; init; }

    /// <summary>Окно, за которое считается длина шага.</summary>
    public double StrideWindowSeconds { get; init; } = 30;

    /// <summary>Лига «Бег»: ходьба и бег.</summary>
    public static LeagueRules Run { get; } = new()
    {
        SpeedLimits = [new(30, 25), new(300, 19)],
        VehicleSeconds = 20,
        CyclingSeconds = 30,
        NoStepsWindowSeconds = 20,
        StrideMeters = [0.3, 2.2],
    };

    /// <summary>Лига «Вело».</summary>
    public static LeagueRules Bike { get; } = new()
    {
        SpeedLimits = [new(5, 60), new(30, 48), new(60, 42), new(300, 36)],
        VehicleShare = new(WindowSeconds: 120, MinShare: 0.6, SpeedWindowSeconds: 60, MinKilometersPerHour: 25),
        CarLaunch = new(DeltaKilometersPerHour: 25, WithinSeconds: 6, ReachingKilometersPerHour: 35),
    };
}
