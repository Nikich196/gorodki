import Foundation

/// Точка на плоскости в метрах: `east` — на восток, `north` — на север.
public struct PlanarPoint: Hashable, Codable, Sendable {
    public var east: Double
    public var north: Double

    public init(east: Double, north: Double) {
        self.east = east
        self.north = north
    }
}

/// Локальная плоскость «восток — север» (ENU) вокруг выбранной точки.
///
/// Геометрию следа удобно считать в метрах на плоскости, а не в градусах.
/// Вблизи начала координат (единицы километров) Землю можно считать плоской.
/// Масштабы берём из эллипсоида WGS 84 в точке начала, поэтому на расстоянии 2 км
/// ошибка — сантиметры: для детектора петли и оценки площади на телефоне этого хватает.
public struct LocalTangentPlane: Sendable {
    public let origin: Coordinate

    /// Метров в радиане широты (радиус кривизны меридиана M).
    private let metersPerRadianNorth: Double
    /// Метров в радиане долготы на широте начала (N · cos φ).
    private let metersPerRadianEast: Double

    public init(origin: Coordinate) {
        self.origin = origin

        // Параметры эллипсоида WGS 84.
        let semiMajorAxis = 6_378_137.0
        let flattening = 1 / 298.257_223_563
        let eccentricitySquared = flattening * (2 - flattening)

        let sinLat = sin(origin.latitude.radians)
        let w = 1 - eccentricitySquared * sinLat * sinLat
        let primeVerticalRadius = semiMajorAxis / w.squareRoot()  // N
        let meridionalRadius = semiMajorAxis * (1 - eccentricitySquared) / (w * w.squareRoot())  // M

        metersPerRadianNorth = meridionalRadius
        metersPerRadianEast = primeVerticalRadius * cos(origin.latitude.radians)
    }

    /// Градусы → метры на плоскости.
    public func project(_ coordinate: Coordinate) -> PlanarPoint {
        PlanarPoint(
            east: (coordinate.longitude - origin.longitude).radians * metersPerRadianEast,
            north: (coordinate.latitude - origin.latitude).radians * metersPerRadianNorth
        )
    }

    /// Метры на плоскости → градусы. Точно обратно `project(_:)`.
    public func unproject(_ point: PlanarPoint) -> Coordinate {
        Coordinate(
            latitude: origin.latitude + (point.north / metersPerRadianNorth).degrees,
            longitude: origin.longitude + (point.east / metersPerRadianEast).degrees
        )
    }
}
