/// Точка на Земле в градусах WGS 84 — то же, что даёт GPS.
///
/// Свой тип вместо `CLLocationCoordinate2D`: GameCore не зависит от CoreLocation
/// и поэтому тестируется на любой платформе.
public struct Coordinate: Hashable, Codable, Sendable {
    /// Широта: от −90 (юг) до 90 (север).
    public var latitude: Double
    /// Долгота: от −180 (запад) до 180 (восток).
    public var longitude: Double

    public init(latitude: Double, longitude: Double) {
        self.latitude = latitude
        self.longitude = longitude
    }

    /// Координаты в допустимых пределах и не NaN.
    public var isValid: Bool {
        (-90.0...90.0).contains(latitude) && (-180.0...180.0).contains(longitude)
    }
}
