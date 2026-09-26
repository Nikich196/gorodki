/// Окно карты в градусах — какие тайлы земли (UTM 1×1 км) и тумана (веб-меркатор z14) в нём видны: их карта и просит
/// у кэшей (`TerritoryCache.refresh(visible:)`, `FogCache.refresh(visible:)`).
public struct MapWindow: Hashable, Sendable {
    public var south: Double
    public var west: Double
    public var north: Double
    public var east: Double

    public init(south: Double, west: Double, north: Double, east: Double) {
        self.south = min(south, north)
        self.north = max(south, north)
        self.west = min(west, east)
        self.east = max(west, east)
    }

    /// Окно вокруг центра: `latitudeDelta` и `longitudeDelta` — полная высота и ширина, градусы.
    public init(center: Coordinate, latitudeDelta: Double, longitudeDelta: Double) {
        self.init(
            south: center.latitude - latitudeDelta / 2, west: center.longitude - longitudeDelta / 2,
            north: center.latitude + latitudeDelta / 2, east: center.longitude + longitudeDelta / 2)
    }

    private var corners: [Coordinate] {
        [
            Coordinate(latitude: south, longitude: west), Coordinate(latitude: south, longitude: east),
            Coordinate(latitude: north, longitude: west), Coordinate(latitude: north, longitude: east),
        ]
    }

    /// Сколько тайлов земли видно — без их перечисления: у далёкого окна (весь мир) их миллионы.
    public var landTileCount: Int {
        let (columns, rows) = landRanges
        return columns.count * rows.count
    }

    /// Тайлы земли в окне. Окно в градусах в UTM — чуть повёрнутый четырёхугольник, поэтому берётся рамка его углов:
    /// лишний тайл у края не страшен, пропущенный — был бы дырой на карте.
    public var landTiles: [LandTileKey] {
        let (columns, rows) = landRanges
        return columns.flatMap { x in rows.map { LandTileKey(x: x, y: $0) } }
    }

    private var landRanges: (ClosedRange<Int>, ClosedRange<Int>) {
        let keys = corners.map(LandTileKey.containing)
        let xs = keys.map(\.x)
        let ys = keys.map(\.y)
        return ((xs.min() ?? 0)...(xs.max() ?? 0), (ys.min() ?? 0)...(ys.max() ?? 0))
    }

    /// Сколько тайлов тумана видно.
    public var fogTileCount: Int {
        let (columns, rows) = fogRanges
        return columns.count * rows.count
    }

    /// Тайлы тумана в окне: в веб-меркаторе окно в градусах — прямоугольник.
    public var fogTiles: [FogTileKey] {
        let (columns, rows) = fogRanges
        return columns.flatMap { x in rows.map { FogTileKey(x: x, y: $0) } }
    }

    private var fogRanges: (ClosedRange<Int>, ClosedRange<Int>) {
        let northWest = FogGrid.cell(of: Coordinate(latitude: north, longitude: west)).tile
        let southEast = FogGrid.cell(of: Coordinate(latitude: south, longitude: east)).tile
        return (northWest.x...southEast.x, northWest.y...southEast.y)
    }
}
