/// Одна точка GPS-следа.
public struct TrackPoint: Hashable, Codable, Sendable {
    /// Порядковый номер точки в забеге: по нему сервер понимает, какую часть следа заявляет петля.
    public var seq: Int
    public var coordinate: Coordinate
    /// Время точки, секунды Unix.
    public var timestamp: Double
    /// Горизонтальная точность, метры (радиус, в котором с большой вероятностью находится телефон).
    public var horizontalAccuracy: Double
    /// Скорость от GPS, м/с, если телефон её сообщил.
    public var speed: Double?

    public init(seq: Int, coordinate: Coordinate, timestamp: Double, horizontalAccuracy: Double, speed: Double? = nil) {
        self.seq = seq
        self.coordinate = coordinate
        self.timestamp = timestamp
        self.horizontalAccuracy = horizontalAccuracy
        self.speed = speed
    }
}

/// Вид движения по данным датчиков телефона (CoreMotion).
public enum MotionActivity: String, Codable, Sendable {
    case stationary, walking, running, cycling, automotive, unknown
}

/// С этого момента телефон считает, что владелец движется так-то (до следующей записи).
public struct MotionSample: Hashable, Codable, Sendable {
    public var timestamp: Double
    public var activity: MotionActivity

    public init(timestamp: Double, activity: MotionActivity) {
        self.timestamp = timestamp
        self.activity = activity
    }
}

/// Шаги за интервал по данным шагомера.
public struct PedometerSample: Hashable, Codable, Sendable {
    public var start: Double
    public var end: Double
    /// `nil` — шагомер ничего не знает (это «неизвестно», а не ноль шагов).
    public var steps: Int?

    public init(start: Double, end: Double, steps: Int?) {
        self.start = start
        self.end = end
        self.steps = steps
    }
}
