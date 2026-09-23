@testable import GameCore

/// Синтетические следы для тестов: движение по плоскости «восток — север» вокруг точки в Бресте.
/// Никаких реальных маршрутов и домов.
struct TrackSimulator {
    static let origin = Coordinate(latitude: 52.0976, longitude: 23.6880)
    let plane = LocalTangentPlane(origin: TrackSimulator.origin)
    var time: Double = 1_790_000_000
    var seq = 0

    /// Точка в метрах от начала.
    func coordinate(east: Double, north: Double) -> Coordinate {
        plane.unproject(PlanarPoint(east: east, north: north))
    }

    /// Движение по прямой с постоянной скоростью, точка раз в секунду.
    mutating func straight(
        from start: (east: Double, north: Double),
        heading: (east: Double, north: Double) = (1, 0),
        speed: Double,
        seconds: Int,
        accuracy: Double = 5,
        reportSpeed: Bool = false
    ) -> [TrackPoint] {
        (0..<seconds).map { i in
            let d = speed * Double(i)
            return next(
                east: start.east + heading.east * d, north: start.north + heading.north * d, accuracy: accuracy,
                speed: reportSpeed ? speed : nil)
        }
    }

    /// Обход многоугольника по вершинам с точками каждые `step` метров.
    mutating func walk(_ vertices: [(Double, Double)], step: Double = 3, accuracy: Double = 5, closed: Bool = true)
        -> [TrackPoint]
    {
        var result: [TrackPoint] = []
        let count = closed ? vertices.count : vertices.count - 1
        for i in 0..<count {
            let (x1, y1) = vertices[i]
            let (x2, y2) = vertices[(i + 1) % vertices.count]
            let length = ((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)).squareRoot()
            let segments = max(1, Int((length / step).rounded(.up)))
            for s in 0..<segments {
                let t = Double(s) / Double(segments)
                result.append(next(east: x1 + (x2 - x1) * t, north: y1 + (y2 - y1) * t, accuracy: accuracy))
            }
        }
        if closed, let first = vertices.first {
            result.append(next(east: first.0, north: first.1, accuracy: accuracy))
        }
        return result
    }

    mutating func next(east: Double, north: Double, accuracy: Double = 5, speed: Double? = nil) -> TrackPoint {
        defer {
            time += 1
            seq += 1
        }
        return TrackPoint(
            seq: seq,
            coordinate: coordinate(east: east, north: north),
            timestamp: time,
            horizontalAccuracy: accuracy,
            speed: speed
        )
    }
}
