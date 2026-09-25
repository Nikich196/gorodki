import Foundation
import GameCore

/// Синтетические «Мои данные» для тестов и пробного запуска: три забега вокруг условной точки в Бресте, никаких
/// настоящих маршрутов. Ходьба 1,4 м/с, точка в секунду, точность 5 м; ошибка GPS медленно «плывёт» (AR(1)) и не
/// выходит за заданный предел по каждой оси. Зерно фиксированное: следы одинаковы на любой платформе.
public enum SyntheticRuns {
    public static let origin = Coordinate(latitude: 52.0976, longitude: 23.6880)

    /// Квадрат ~60 × 60 м с шумом до ±5 м, шире 2·R_min — петля, которую сервер засчитает. Детектор замыкает её
    /// сближением примерно за R до начала, поэтому петля меньше 3 600 м² — около 3 300.
    public static func square(seed: UInt64 = 1) -> MyData.Run {
        var track = SyntheticTrack(noiseLimit: 5, seed: seed)
        track.walk([(0, 0), (60, 0), (60, 60), (0, 60), (0, 0), (-40, -40)])
        return track.run(id: "00000001-0000-4000-8000-000000000001")
    }

    /// Полоса 12 × 300 м: туда по одной стороне, обратно по другой, шум до ±1 м.
    public static func strip(seed: UInt64 = 2) -> MyData.Run {
        var track = SyntheticTrack(noiseLimit: 1, seed: seed)
        track.walk([(0, 0), (300, 0), (300, 12), (0, 12), (-40, 52)])
        return track.run(id: "00000002-0000-4000-8000-000000000002")
    }

    /// Незамкнутая дуга: три четверти круга радиусом 50 м, концы в ≈ 71 м друг от друга (больше R_макс = 50 м).
    public static func arc(seed: UInt64 = 3) -> MyData.Run {
        var track = SyntheticTrack(noiseLimit: 1, seed: seed)
        let vertices = stride(from: 0.0, through: 270, by: 5).map { degrees -> (Double, Double) in
            let angle = degrees * .pi / 180
            return (50 * cos(angle), 50 * sin(angle))
        }
        track.walk(vertices)
        return track.run(id: "00000003-0000-4000-8000-000000000003")
    }

    public static func myData() -> MyData {
        MyData(runs: [square(), strip(), arc()], captures: [])
    }
}

/// Синтетический след: обход вершин (метры «восток — север» от `SyntheticRuns.origin`) с ошибкой GPS.
struct SyntheticTrack {
    private let plane = LocalTangentPlane(origin: SyntheticRuns.origin)
    private var generator: SplitMix64
    private var drift = (east: 0.0, north: 0.0)
    private let noiseLimit: Double
    private var timeMs: Int64 = 1_790_000_000_000
    private(set) var points: [MyData.Point] = []

    init(noiseLimit: Double, seed: UInt64) {
        self.noiseLimit = noiseLimit
        generator = SplitMix64(seed: seed)
    }

    /// Обход с постоянной скоростью, точка раз в секунду.
    mutating func walk(_ vertices: [(Double, Double)], speed: Double = 1.4) {
        for (from, to) in zip(vertices, vertices.dropFirst()) {
            let length = ((to.0 - from.0) * (to.0 - from.0) + (to.1 - from.1) * (to.1 - from.1)).squareRoot()
            let steps = max(1, Int((length / speed).rounded(.up)))
            for step in 0..<steps {
                let t = Double(step) / Double(steps)
                fix(east: from.0 + (to.0 - from.0) * t, north: from.1 + (to.1 - from.1) * t)
            }
        }
        if let last = vertices.last {
            fix(east: last.0, north: last.1)
        }
    }

    func run(id: String) -> MyData.Run {
        MyData.Run(id: id, league: .run, startedAtMs: points.first?.t ?? timeMs, points: points)
    }

    private mutating func fix(east: Double, north: Double) {
        // AR(1): ρ = 0,9; предел — по каждой оси.
        let sigma = noiseLimit / 4
        drift = (
            clamp(0.9 * drift.east + sigma * generator.gaussian()),
            clamp(0.9 * drift.north + sigma * generator.gaussian())
        )
        let coordinate = plane.unproject(PlanarPoint(east: east + drift.east, north: north + drift.north))
        points.append(
            MyData.Point(
                seq: points.count, t: timeMs, lat: (coordinate.latitude * 1e7).rounded() / 1e7,
                lon: (coordinate.longitude * 1e7).rounded() / 1e7, acc: 5))
        timeMs += 1_000
    }

    private func clamp(_ value: Double) -> Double {
        min(max(value, -noiseLimit), noiseLimit)
    }
}

/// SplitMix64 (Vigna): простой генератор с зерном, одинаковый на всех платформах.
struct SplitMix64: RandomNumberGenerator {
    private var state: UInt64

    init(seed: UInt64) {
        state = seed
    }

    mutating func next() -> UInt64 {
        state &+= 0x9E37_79B9_7F4A_7C15
        var z = state
        z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
        z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
        return z ^ (z >> 31)
    }

    /// Нормальное распределение N(0, 1) (Бокс — Мюллер).
    mutating func gaussian() -> Double {
        let u1 = max(Double.random(in: 0..<1, using: &self), 1e-300)
        let u2 = Double.random(in: 0..<1, using: &self)
        return (-2 * log(u1)).squareRoot() * cos(2 * .pi * u2)
    }
}
