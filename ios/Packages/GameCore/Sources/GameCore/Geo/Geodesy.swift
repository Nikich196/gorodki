import Foundation

/// Расстояния на поверхности Земли.
public enum Geodesy {
    /// Средний радиус Земли по IUGG, метры.
    public static let meanEarthRadius = 6_371_008.8

    /// Расстояние по дуге большого круга (формула гаверсинусов), метры.
    ///
    /// Точность — доли процента: Земля не шар. Для отрезков GPS-следа (метры и десятки метров)
    /// этого более чем достаточно. Точную площадь участков считает сервер в проекции UTM.
    public static func distance(from a: Coordinate, to b: Coordinate) -> Double {
        let lat1 = a.latitude.radians
        let lat2 = b.latitude.radians
        let deltaLat = (b.latitude - a.latitude).radians
        let deltaLon = (b.longitude - a.longitude).radians

        let h =
            sin(deltaLat / 2) * sin(deltaLat / 2)
            + cos(lat1) * cos(lat2) * sin(deltaLon / 2) * sin(deltaLon / 2)
        return 2 * meanEarthRadius * asin(min(1, h.squareRoot()))
    }
}

extension Double {
    /// Градусы → радианы.
    var radians: Double { self * .pi / 180 }
    /// Радианы → градусы.
    var degrees: Double { self * 180 / .pi }
}
