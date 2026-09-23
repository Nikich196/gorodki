import Foundation

/// Клетка тумана — пиксель веб-меркатора на уровне 22 (PLAN.md, §3.10, «сетка G22»).
/// На широте Бреста это ≈5,87 м. 256 × 256 таких клеток образуют тайл уровня 14.
public struct FogCell: Hashable, Codable, Sendable {
    public var x: Int
    public var y: Int

    public init(x: Int, y: Int) {
        self.x = x
        self.y = y
    }

    /// Тайл уровня 14, в котором лежит клетка.
    public var tile: FogTileKey { FogTileKey(x: x >> 8, y: y >> 8) }

    /// Номер бита внутри тайла: строка × 256 + столбец.
    public var bitIndex: Int { (y & 255) * 256 + (x & 255) }
}

/// Тайл веб-меркатора уровня 14 (~1,5 × 1,5 км в Бресте): единица хранения тумана на сервере.
public struct FogTileKey: Hashable, Codable, Sendable, Comparable {
    public var x: Int
    public var y: Int

    public init(x: Int, y: Int) {
        self.x = x
        self.y = y
    }

    public static func < (a: FogTileKey, b: FogTileKey) -> Bool { a.x != b.x ? a.x < b.x : a.y < b.y }
}

/// Математика сетки тумана: веб-меркатор, уровень 22.
public enum FogGrid {
    public static let zoom = 22
    public static let cellsPerTileSide = 256
    private static let worldCells = Double(1 << zoom)
    /// Длина экватора в веб-меркаторе (радиус 6 378 137 м), метры.
    private static let equatorMeters = 40_075_016.685_578_49

    /// Дробные координаты пикселя уровня 22 (x — на восток, y — на юг).
    public static func pixel(of coordinate: Coordinate) -> (x: Double, y: Double) {
        let latitude = min(max(coordinate.latitude, -85.051_128_78), 85.051_128_78).radians
        let x = (coordinate.longitude + 180) / 360 * worldCells
        let y = (1 - log(tan(latitude) + 1 / cos(latitude)) / .pi) / 2 * worldCells
        return (x, y)
    }

    public static func cell(of coordinate: Coordinate) -> FogCell {
        let (x, y) = pixel(of: coordinate)
        return FogCell(x: Int(x.rounded(.down)), y: Int(y.rounded(.down)))
    }

    /// Центр клетки в градусах.
    public static func center(of cell: FogCell) -> Coordinate {
        let x = (Double(cell.x) + 0.5) / worldCells
        let y = (Double(cell.y) + 0.5) / worldCells
        let latitude = atan(sinh(.pi * (1 - 2 * y))).degrees
        return Coordinate(latitude: latitude, longitude: x * 360 - 180)
    }

    /// Сторона клетки на данной широте, метры (меркатор сохраняет форму: клетка — квадрат).
    public static func cellSizeMeters(atLatitude latitude: Double) -> Double {
        equatorMeters / worldCells * cos(latitude.radians)
    }
}
