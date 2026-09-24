import Foundation

/// Градусы WGS 84 ↔ метры UTM зоны 34N (EPSG:32634) — та же проекция, что у сервера (`Gorodki.Domain.Geo.Utm34`):
/// в ней хранятся участки, а тайлы карты — квадраты 1×1 км с ключом (⌊x/1000⌋, ⌊y/1000⌋) (PLAN.md, §7.3).
/// На телефоне нужна, чтобы видеть тайлы так же, как сервер: какие тайлы видны на карте и где проходят их края.
///
/// Формулы Крюгера в форме Карни (C. F. F. Karney, «Transverse Mercator with an accuracy of a few nanometers», 2011)
/// с рядами до n³, как на сервере: до 6° от осевого меридиана ошибка меньше миллиметра. Эталоны в тестах — от PROJ.
public enum Utm34 {
    /// Код системы координат EPSG.
    public static let srid = 32_634
    /// Сторона тайла карты, метры.
    public static let tileSizeMeters = 1_000.0

    private static let semiMajorAxis = 6_378_137.0  // a, WGS 84
    private static let flattening = 1 / 298.257_223_563  // f, WGS 84
    private static let scaleFactor = 0.9996  // k0 для всех зон UTM
    private static let falseEasting = 500_000.0  // сдвиг на восток, чтобы координаты были положительными
    private static let centralMeridian = 21.0.radians  // осевой меридиан зоны 34

    // Третий сплющённый параметр n и производные от него величины.
    private static let n = flattening / (2 - flattening)
    private static let eccentricity = 2 * n.squareRoot() / (1 + n)
    private static let rectifyingRadius = semiMajorAxis / (1 + n) * (1 + n * n / 4 + n * n * n * n / 64)

    private static let alpha = [
        n / 2 - 2 * n * n / 3 + 5 * n * n * n / 16,
        13 * n * n / 48 - 3 * n * n * n / 5,
        61 * n * n * n / 240,
    ]

    private static let beta = [
        n / 2 - 2 * n * n / 3 + 37 * n * n * n / 96,
        n * n / 48 + n * n * n / 15,
        17 * n * n * n / 480,
    ]

    private static let delta = [
        2 * n - 2 * n * n / 3 - 2 * n * n * n,
        7 * n * n / 3 - 8 * n * n * n / 5,
        56 * n * n * n / 15,
    ]

    /// Широта и долгота → восток и север, метры (`east` — easting, `north` — northing).
    public static func project(_ coordinate: Coordinate) -> PlanarPoint {
        let phi = coordinate.latitude.radians
        let deltaLambda = coordinate.longitude.radians - centralMeridian

        let sinPhi = sin(phi)
        // Конформная широта через t = tg χ.
        let t = sinh(atanh(sinPhi) - eccentricity * atanh(eccentricity * sinPhi))
        let xiPrime = atan2(t, cos(deltaLambda))
        let etaPrime = atanh(sin(deltaLambda) / (1 + t * t).squareRoot())

        var xi = xiPrime
        var eta = etaPrime
        for (index, a) in alpha.enumerated() {
            let j = Double(index + 1)
            xi += a * sin(2 * j * xiPrime) * cosh(2 * j * etaPrime)
            eta += a * cos(2 * j * xiPrime) * sinh(2 * j * etaPrime)
        }

        return PlanarPoint(
            east: falseEasting + scaleFactor * rectifyingRadius * eta,
            north: scaleFactor * rectifyingRadius * xi)
    }

    /// Восток и север, метры → широта и долгота.
    public static func unproject(_ point: PlanarPoint) -> Coordinate {
        let xi = point.north / (scaleFactor * rectifyingRadius)
        let eta = (point.east - falseEasting) / (scaleFactor * rectifyingRadius)

        var xiPrime = xi
        var etaPrime = eta
        for (index, b) in beta.enumerated() {
            let j = Double(index + 1)
            xiPrime -= b * sin(2 * j * xi) * cosh(2 * j * eta)
            etaPrime -= b * cos(2 * j * xi) * sinh(2 * j * eta)
        }

        let chi = asin(sin(xiPrime) / cosh(etaPrime))
        var phi = chi
        for (index, d) in delta.enumerated() {
            phi += d * sin(2 * Double(index + 1) * chi)
        }

        let lambda = centralMeridian + atan2(sinh(etaPrime), cos(xiPrime))
        return Coordinate(latitude: phi.degrees, longitude: lambda.degrees)
    }
}
