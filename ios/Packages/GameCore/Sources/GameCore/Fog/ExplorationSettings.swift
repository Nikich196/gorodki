/// Числа «Исследования» (PLAN.md, §3.10 и §7.2). Хранятся в игровом конфиге (раздел `exploration`).
public struct ExplorationSettings: Hashable, Codable, Sendable {
    /// Валидное движение открывает карту в этом радиусе вокруг пути, метры.
    public var revealRadiusMeters: Double = 25
    /// Между точками дальше этого путь не «закрашивается»: GPS пропадал, и где шёл игрок — неизвестно.
    public var maxGapMeters = PerLeague(run: 100.0, bike: 200.0)
    /// Во сколько раз «Радар» увеличивает радиус.
    public var radarMultiplier: Double = 2

    public init() {}
}

/// Значение, своё для каждой лиги.
public struct PerLeague<Value: Hashable & Codable & Sendable>: Hashable, Codable, Sendable {
    public var run: Value
    public var bike: Value

    public init(run: Value, bike: Value) {
        self.run = run
        self.bike = bike
    }

    public func value(for league: League) -> Value {
        switch league {
        case .run: run
        case .bike: bike
        }
    }
}
