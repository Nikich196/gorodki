/// Участок на карте для касания: внешний контур и дыры в координатах (как в `GET /territory`, docs/architecture/
/// territory-map.md: кольца `[широта, долгота, …]`, первая точка повторяется в конце).
public struct ParcelShape: Sendable, Equatable {
    public var exterior: [Coordinate]
    public var holes: [[Coordinate]]

    public init(exterior: [Coordinate], holes: [[Coordinate]] = []) {
        self.exterior = exterior
        self.holes = holes
    }

    /// Из кольца ответа сервера: `[широта, долгота, широта, долгота, …]`. Нечётный хвост отбрасывается.
    public static func ring(_ flat: [Double]) -> [Coordinate] {
        stride(from: 0, to: flat.count - 1, by: 2).map { Coordinate(latitude: flat[$0], longitude: flat[$0 + 1]) }
    }
}

/// Какой участок под пальцем (PLAN.md §7: «касание через `UITapGestureRecognizer` + hit-test в GameCore»).
///
/// Участки на карте не перекрываются (инвариант движка участков), поэтому точка внутри контура и вне его дыр — ответ.
/// Если такого нет, палец мог попасть рядом с узким участком: тогда ближайший, чья граница не дальше допуска касания
/// (он зависит от масштаба карты — его даёт экран). Расчёт — на касательной плоскости вокруг точки касания.
public enum ParcelHitTest {
    /// - Parameters:
    ///   - tap: точка касания.
    ///   - tolerance: насколько далеко от границы участка ещё считается «попал», метры.
    ///   - parcels: участки видимых тайлов.
    /// - Returns: индекс участка в `parcels` или `nil`.
    public static func parcel(at tap: Coordinate, tolerance: Double, among parcels: [ParcelShape]) -> Int? {
        let plane = LocalTangentPlane(origin: tap)
        let origin = PlanarPoint(east: 0, north: 0)
        var nearest: (index: Int, distance: Double)?
        for (index, parcel) in parcels.enumerated() {
            let exterior = ring(parcel.exterior, on: plane)
            let holes = parcel.holes.map { ring($0, on: plane) }
            let inHole = holes.contains { $0.contains(origin) }
            if exterior.contains(origin), !inHole {
                return index
            }
            // Вне участка (или в его дыре) — расстояние до ближайшей границы.
            let distance = ([exterior] + holes).map { distanceToBoundary(of: $0) }.min() ?? .infinity
            if distance <= tolerance, distance < (nearest?.distance ?? .infinity) {
                nearest = (index, distance)
            }
        }
        return nearest?.index
    }

    private static func ring(_ coordinates: [Coordinate], on plane: LocalTangentPlane) -> PlanarRing {
        var points = coordinates.map(plane.project)
        if points.count > 1, points.first == points.last {
            points.removeLast()  // кольцо сервера замкнуто повтором первой точки
        }
        return PlanarRing(points)
    }

    /// Расстояние от начала координат (точки касания) до границы кольца, метры.
    private static func distanceToBoundary(of ring: PlanarRing) -> Double {
        let vertices = ring.vertices
        guard !vertices.isEmpty else { return .infinity }
        var best = Double.infinity
        for index in vertices.indices {
            let a = vertices[index]
            let b = vertices[(index + 1) % vertices.count]
            best = min(best, distanceToSegment(a, b))
        }
        return best
    }

    private static func distanceToSegment(_ a: PlanarPoint, _ b: PlanarPoint) -> Double {
        let dx = b.east - a.east
        let dy = b.north - a.north
        let lengthSquared = dx * dx + dy * dy
        let t = lengthSquared > 0 ? max(0, min(1, -(a.east * dx + a.north * dy) / lengthSquared)) : 0
        let x = a.east + t * dx
        let y = a.north + t * dy
        return (x * x + y * y).squareRoot()
    }
}
