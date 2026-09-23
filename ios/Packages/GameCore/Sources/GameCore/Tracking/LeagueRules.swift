/// Лига: у бега и велосипеда свои правила и своя карта (PLAN.md, D16).
public enum League: String, Codable, Sendable, CaseIterable {
    case run
    case bike
}

/// Предел средней скорости за окно: если за последние `windowSeconds` средняя скорость выше — это не бег (не велосипед).
public struct SpeedLimit: Hashable, Codable, Sendable {
    public var windowSeconds: Double
    public var maxKilometersPerHour: Double

    public init(windowSeconds: Double, maxKilometersPerHour: Double) {
        self.windowSeconds = windowSeconds
        self.maxKilometersPerHour = maxKilometersPerHour
    }
}

/// «Разгон машины»: скорость выросла на `deltaKilometersPerHour` за `withinSeconds` и дошла до `reachingKilometersPerHour`.
public struct CarLaunchRule: Hashable, Codable, Sendable {
    public var deltaKilometersPerHour: Double
    public var withinSeconds: Double
    public var reachingKilometersPerHour: Double
}

/// «Транспорт по датчикам»: доля времени automotive за окно и одновременно высокая скорость.
public struct VehicleShareRule: Hashable, Codable, Sendable {
    public var windowSeconds: Double
    public var minShare: Double
    public var speedWindowSeconds: Double
    public var minKilometersPerHour: Double
}

/// Пороги античита одной лиги (PLAN.md, §3.9). Хранятся в игровом конфиге, здесь — значения по умолчанию.
public struct LeagueRules: Hashable, Codable, Sendable {
    // Слой 1 — сама точка.
    /// Хуже этой точности точка для захвата не используется (для первой петли новичка — 35 м).
    public var maxAccuracyMeters: Double = 25
    /// Точка старше этого — устарела.
    public var maxFixAgeSeconds: Double = 10
    /// Скачок быстрее этого — «телепорт», след рвётся. Начальное значение (180 км/ч), калибруется на полевом тесте.
    public var teleportMetersPerSecond: Double = 50

    // Слой 2 — отрезки.
    public var speedLimits: [SpeedLimit]
    /// Бег: «транспорт» по датчикам дольше этого — разрыв.
    public var vehicleSeconds: Double?
    /// Бег: «велосипед» по датчикам дольше этого — разрыв и предложение перейти в лигу «Вело».
    public var cyclingSeconds: Double?
    public var vehicleShare: VehicleShareRule?
    public var carLaunch: CarLaunchRule?
    /// Бег: ноль шагов за это окно при скорости не ниже `noStepsMinSpeed` — разрыв.
    public var noStepsWindowSeconds: Double?
    public var noStepsMinSpeed: Double = 1.0
    /// Бег: длина шага вне этих пределов — разрыв.
    public var strideMeters: ClosedRange<Double>?
    /// Окно, за которое считается длина шага.
    public var strideWindowSeconds: Double = 30

    /// Лига «Бег»: ходьба и бег.
    public static let run = LeagueRules(
        speedLimits: [
            SpeedLimit(windowSeconds: 30, maxKilometersPerHour: 25),
            SpeedLimit(windowSeconds: 300, maxKilometersPerHour: 19),
        ],
        vehicleSeconds: 20,
        cyclingSeconds: 30,
        vehicleShare: nil,
        carLaunch: nil,
        noStepsWindowSeconds: 20,
        strideMeters: 0.3...2.2
    )

    /// Лига «Вело».
    public static let bike = LeagueRules(
        speedLimits: [
            SpeedLimit(windowSeconds: 5, maxKilometersPerHour: 60),
            SpeedLimit(windowSeconds: 30, maxKilometersPerHour: 48),
            SpeedLimit(windowSeconds: 60, maxKilometersPerHour: 42),
            SpeedLimit(windowSeconds: 300, maxKilometersPerHour: 36),
        ],
        vehicleSeconds: nil,
        cyclingSeconds: nil,
        vehicleShare: VehicleShareRule(
            windowSeconds: 120, minShare: 0.6, speedWindowSeconds: 60, minKilometersPerHour: 25),
        carLaunch: CarLaunchRule(deltaKilometersPerHour: 25, withinSeconds: 6, reachingKilometersPerHour: 35),
        noStepsWindowSeconds: nil,
        strideMeters: nil
    )

    public static func `default`(for league: League) -> LeagueRules {
        switch league {
        case .run: .run
        case .bike: .bike
        }
    }
}
