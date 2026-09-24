import GorodkiAPI

/// Лига — как в API: `run` или `bike`.
public typealias League = Components.Schemas.League

/// Тайл карты земли — клетка UTM 1×1 км, как в `GET /territory` (docs/architecture/territory-map.md).
public struct TileKey: Hashable, Sendable, Comparable {
    public var x: Int
    public var y: Int

    public init(x: Int, y: Int) {
        self.x = x
        self.y = y
    }

    public static func < (a: TileKey, b: TileKey) -> Bool { a.x != b.x ? a.x < b.x : a.y < b.y }
}
