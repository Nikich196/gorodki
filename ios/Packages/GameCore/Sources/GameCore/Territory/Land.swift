/// Тайл земли — квадрат UTM 1×1 км, ключ `GET /territory` (⌊x/1000⌋, ⌊y/1000⌋) в метрах зоны 34N
/// (docs/architecture/territory-map.md).
public struct LandTileKey: Hashable, Sendable, Comparable {
    public var x: Int
    public var y: Int

    public init(x: Int, y: Int) {
        self.x = x
        self.y = y
    }

    public static func < (a: LandTileKey, b: LandTileKey) -> Bool { a.x != b.x ? a.x < b.x : a.y < b.y }

    /// Тайл, в котором лежит точка.
    public static func containing(_ coordinate: Coordinate) -> LandTileKey {
        let point = Utm34.project(coordinate)
        return LandTileKey(
            x: Int((point.east / Utm34.tileSizeMeters).rounded(.down)),
            y: Int((point.north / Utm34.tileSizeMeters).rounded(.down)))
    }
}

/// Отношение зрителя к куску земли — то, что можно понять из ответа `/territory` (PLAN.md, §6.3). Клана в ответе нет
/// (кланы — #64), поэтому «клан» не различается; «Ничейные земли» — отдельные цели, не куски.
public enum LandRelation: Hashable, Sendable {
    /// Своя земля.
    case mine
    /// Чужая живая земля.
    case rival
    /// Угасшая земля: 3 дня видна «призраком» (§3.3, `ghost` в ответе).
    case lost
}

/// Кусок земли внутри одного тайла — строка ответа `/territory` без лишнего: владелец и его цвет, уровень, времена
/// (мс Unix) и контур. Кусков одного участка столько, сколько тайлов он задевает; состояние у них общее.
public struct LandParcel: Hashable, Sendable {
    public var id: Int64
    public var ownerId: String
    /// Номер цвета владельца в палитре из 12.
    public var colorIndex: Int
    /// Уровень, как его посчитал сервер при чтении (угасание — ленивое): 1…3, у призрака 0.
    public var level: Int
    public var ghost: Bool
    public var lastVisitAtMs: Int64
    public var shieldUntilMs: Int64?
    public var siegeUntilMs: Int64?
    public var shape: ParcelShape
    /// Кромка куска — его кольца без отрезков по краям тайла (`LandBorders`): на карте нет швов между тайлами.
    public var borders: [[Coordinate]]

    public init(
        id: Int64, ownerId: String, colorIndex: Int, level: Int, ghost: Bool, lastVisitAtMs: Int64,
        shieldUntilMs: Int64?, siegeUntilMs: Int64?, shape: ParcelShape, tile: LandTileKey
    ) {
        self.id = id
        self.ownerId = ownerId
        self.colorIndex = colorIndex
        self.level = level
        self.ghost = ghost
        self.lastVisitAtMs = lastVisitAtMs
        self.shieldUntilMs = shieldUntilMs
        self.siegeUntilMs = siegeUntilMs
        self.shape = shape
        self.borders = LandBorders.lines(of: shape, in: tile)
    }

    /// Отношение к зрителю `viewer` (номер вошедшего игрока; `nil` — не вошёл). Номера — UUID, регистр не важен.
    public func relation(viewer: String?) -> LandRelation {
        if ghost {
            return .lost
        }
        if let viewer, viewer.lowercased() == ownerId.lowercased() {
            return .mine
        }
        return .rival
    }

    /// Уровень заливки 1…3 (неожиданное значение сервера — в пределы, экран не падает); у призрака — `nil`.
    public var fillLevel: Int? {
        ghost ? nil : min(max(level, 1), 3)
    }

    /// Щит ещё действует.
    public func shieldActive(atMs now: Int64) -> Bool {
        (shieldUntilMs ?? .min) > now
    }

    /// Осада ещё идёт: треснувший кусок нельзя укреплять (§3.3).
    public func siegeActive(atMs now: Int64) -> Bool {
        (siegeUntilMs ?? .min) > now
    }
}

/// Зона «спорная» (§3.3, большая петля): чужая земля, обведённая большой петлёй, — отдельный слой поверх кусков, без
/// игровой силы.
public struct ContestedZone: Hashable, Sendable {
    /// До какого момента зона видна, мс Unix. Сервер присылает только неистёкшие, но тайл живёт в кэше до часа,
    /// а версия тайла при истечении зоны не меняется — поэтому скрывает истёкшие сам телефон.
    public var untilMs: Int64
    public var shape: ParcelShape

    public init(untilMs: Int64, shape: ParcelShape) {
        self.untilMs = untilMs
        self.shape = shape
    }

    public func isActive(atMs now: Int64) -> Bool {
        now < untilMs
    }
}

/// Земля одного тайла.
public struct LandTile: Hashable, Sendable {
    public var key: LandTileKey
    public var parcels: [LandParcel]
    public var contestedZones: [ContestedZone]

    public init(key: LandTileKey, parcels: [LandParcel], contestedZones: [ContestedZone]) {
        self.key = key
        self.parcels = parcels
        self.contestedZones = contestedZones
    }
}

/// Земля, которую знает карта: тайлы по ключам.
public struct LandMap: Hashable, Sendable {
    public private(set) var tiles: [LandTileKey: LandTile] = [:]

    public init(_ tiles: [LandTile] = []) {
        for tile in tiles {
            apply(tile)
        }
    }

    /// Новая версия тайла заменяет прежнюю целиком.
    public mutating func apply(_ tile: LandTile) {
        tiles[tile.key] = tile
    }

    /// Все куски — в порядке тайлов: одинаковые данные дают одинаковую карту.
    public var parcels: [LandParcel] {
        tiles.keys.sorted().flatMap { tiles[$0]?.parcels ?? [] }
    }

    /// Зоны «спорная», которые ещё не истекли к `now`.
    public func activeZones(atMs now: Int64) -> [ContestedZone] {
        tiles.keys.sorted().flatMap { tiles[$0]?.contestedZones ?? [] }.filter { $0.isActive(atMs: now) }
    }

    /// Кусок под пальцем (`ParcelHitTest`). Ищется в тайле касания и восьми соседних: допуск касания — метры, а тайл —
    /// километр, дальше искать незачем.
    public func parcel(at tap: Coordinate, tolerance: Double) -> LandParcel? {
        let candidates = nearbyTiles(of: tap).flatMap { $0.parcels }
        return ParcelHitTest.parcel(at: tap, tolerance: tolerance, among: candidates.map(\.shape))
            .map { candidates[$0] }
    }

    /// Неистёкшая зона «спорная», в которой лежит точка (без допуска: зона — пометка поверх куска, а не цель касания).
    public func contestedZone(at tap: Coordinate, atMs now: Int64) -> ContestedZone? {
        let zones = nearbyTiles(of: tap).flatMap { $0.contestedZones }.filter { $0.isActive(atMs: now) }
        return ParcelHitTest.parcel(at: tap, tolerance: 0, among: zones.map(\.shape)).map { zones[$0] }
    }

    private func nearbyTiles(of tap: Coordinate) -> [LandTile] {
        let center = LandTileKey.containing(tap)
        var result: [LandTile] = []
        for dx in -1...1 {
            for dy in -1...1 {
                if let tile = tiles[LandTileKey(x: center.x + dx, y: center.y + dy)] {
                    result.append(tile)
                }
            }
        }
        return result
    }
}

/// Кромки земли по кускам — простейший честный вариант, пока сервер не отдаёт линии границ, объединённые через тайлы
/// (задача C14): сервер режет землю по тайлам UTM, и у куска бывают стороны на линии тайла — это разрез, а не граница
/// участка. Кромка куска — его кольца без таких сторон, поэтому соседние куски одного участка сходятся без шва.
///
/// Чего так не сделать: кромка между двумя разными участками одного владельца и уровня рисуется дважды (по разу у
/// каждого) — не видно; а граница, которая сама идёт ровно по линии тайла (в пределах `tileEdgeTolerance`),
/// пропадает — у следа GPS так почти не бывает.
public enum LandBorders {
    /// Точка лежит на линии тайла, если до неё не дальше стольких метров: координаты ответа — 7 знаков (~1 см).
    public static let tileEdgeTolerance = 0.05

    /// Линии кромки куска тайла `tile`: внешнее кольцо и дыры, разорванные там, где сторона лежит на краю тайла.
    /// Кольцо без таких сторон — одна замкнутая линия (первая точка повторяется в конце).
    public static func lines(of shape: ParcelShape, in tile: LandTileKey) -> [[Coordinate]] {
        ([shape.exterior] + shape.holes).flatMap { lines(ofRing: $0, in: tile) }
    }

    static func lines(ofRing ring: [Coordinate], in tile: LandTileKey) -> [[Coordinate]] {
        var points = ring
        if points.count > 1, points.first == points.last {
            points.removeLast()  // кольцо сервера замкнуто повтором первой точки
        }
        guard points.count >= 2 else { return [] }
        let projected = points.map(Utm34.project)
        let count = points.count
        let cut = (0..<count).map { onTileEdge(projected[$0], projected[($0 + 1) % count], tile) }
        guard let firstCut = cut.firstIndex(of: true) else {
            return [points + [points[0]]]
        }
        // Обход — со стороны сразу после разреза: линия, проходящая через начало кольца, не разбивается надвое.
        var lines: [[Coordinate]] = []
        var current: [Coordinate] = []
        for step in 1...count {
            let side = (firstCut + step) % count
            if cut[side] {
                if current.count >= 2 {
                    lines.append(current)
                }
                current = []
            } else {
                if current.isEmpty {
                    current.append(points[side])
                }
                current.append(points[(side + 1) % count])
            }
        }
        if current.count >= 2 {
            lines.append(current)
        }
        return lines
    }

    /// Сторона лежит на одной из четырёх линий тайла.
    private static func onTileEdge(_ a: PlanarPoint, _ b: PlanarPoint, _ tile: LandTileKey) -> Bool {
        let size = Utm34.tileSizeMeters
        let eastLines = [Double(tile.x) * size, Double(tile.x + 1) * size]
        let northLines = [Double(tile.y) * size, Double(tile.y + 1) * size]
        func near(_ value: Double, _ line: Double) -> Bool { abs(value - line) <= tileEdgeTolerance }
        return eastLines.contains { near(a.east, $0) && near(b.east, $0) }
            || northLines.contains { near(a.north, $0) && near(b.north, $0) }
    }
}
