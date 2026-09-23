/// Точность, с которой сервер хранит точки (contracts/README.md, `TrackPoint` в `Gorodki.Domain/Runs`).
///
/// Телефон приводит каждое измерение к этим шагам **до** проверки судьёй и до отправки: тогда сервер,
/// повторяя проверки, получает ровно те же числа и те же вердикты. Округление середины — «от нуля»,
/// как `rounded()` в Swift (на сервере — `MidpointRounding.AwayFromZero`).
public enum StoragePrecision {
    /// 65535 на сервере означает «скорость неизвестна», поэтому потолок — 65534 шага.
    static let maxSteps = 65_534.0

    /// Время — целые миллисекунды.
    public static func time(_ seconds: Double) -> Double {
        (seconds * 1000).rounded() / 1000
    }

    /// Время в миллисекундах Unix — так оно уходит на сервер.
    public static func milliseconds(_ seconds: Double) -> Int64 {
        Int64((seconds * 1000).rounded())
    }
}

extension TrackPoint {
    /// Точка в точности хранения: время — мс, координаты — 1e-7° (около 1 см), точность — 0,1 м, скорость — 0,01 м/с.
    /// Отрицательная скорость (iOS так сообщает «неизвестно») становится `nil`.
    public func quantizedForStorage() -> TrackPoint {
        TrackPoint(
            seq: seq,
            coordinate: Coordinate(
                latitude: (coordinate.latitude * 1e7).rounded() / 1e7,
                longitude: (coordinate.longitude * 1e7).rounded() / 1e7),
            timestamp: StoragePrecision.time(timestamp),
            horizontalAccuracy: min(max((horizontalAccuracy * 10).rounded(), 0), StoragePrecision.maxSteps) / 10,
            speed: speed.flatMap { $0 >= 0 ? min(($0 * 100).rounded(), StoragePrecision.maxSteps) / 100 : nil }
        )
    }
}

extension MotionSample {
    public func quantizedForStorage() -> MotionSample {
        MotionSample(timestamp: StoragePrecision.time(timestamp), activity: activity)
    }
}

extension PedometerSample {
    public func quantizedForStorage() -> PedometerSample {
        PedometerSample(start: StoragePrecision.time(start), end: StoragePrecision.time(end), steps: steps)
    }
}
