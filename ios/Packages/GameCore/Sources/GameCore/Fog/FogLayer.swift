/// Битовая карта тумана одного тайла: 256 × 256 клеток, бит 1 — клетка открыта.
/// Такие же 8 КБ (до сжатия) хранит сервер в `fog_tiles.bits` (PLAN.md, §7.3).
public struct FogTileBits: Hashable, Codable, Sendable {
    public static let wordCount = 256 * 256 / 64

    public private(set) var words: [UInt64]

    public init() {
        words = Array(repeating: 0, count: Self.wordCount)
    }

    public init?(words: [UInt64]) {
        guard words.count == Self.wordCount else { return nil }
        self.words = words
    }

    public func isSet(_ bit: Int) -> Bool { words[bit >> 6] & (1 << UInt64(bit & 63)) != 0 }

    public mutating func set(_ bit: Int) { words[bit >> 6] |= 1 << UInt64(bit & 63) }

    /// Сколько клеток открыто.
    public var count: Int { words.reduce(0) { $0 + $1.nonzeroBitCount } }

    /// Сколько клеток открыто здесь и не было открыто в `old`: popcount(new & ~old).
    public func newCount(comparedTo old: FogTileBits) -> Int {
        zip(words, old.words).reduce(0) { $0 + ($1.0 & ~$1.1).nonzeroBitCount }
    }

    public mutating func formUnion(_ other: FogTileBits) {
        for index in words.indices {
            words[index] |= other.words[index]
        }
    }
}

/// Слой «Исследования» одного игрока: открытые клетки по тайлам (PLAN.md, §3.10).
///
/// Валидное движение открывает круг радиусом 25 м вокруг каждой точки пути; между точками путь
/// «закрашивается» сплошной полосой, если разрыв не больше порога (100 м пешком, 200 м на велосипеде) —
/// длинный разрыв значит, что GPS пропадал, и закрашивать неизвестный путь нельзя.
public struct FogLayer: Hashable, Codable, Sendable {
    public private(set) var tiles: [FogTileKey: FogTileBits] = [:]

    public init() {}

    public var isEmpty: Bool { tiles.isEmpty }

    /// Открывает круг радиусом `radius` метров вокруг точки.
    public mutating func reveal(around coordinate: Coordinate, radius: Double = 25) {
        let size = FogGrid.cellSizeMeters(atLatitude: coordinate.latitude)
        let (px, py) = FogGrid.pixel(of: coordinate)
        let reach = Int((radius / size).rounded(.up)) + 1
        let cx = Int(px.rounded(.down))
        let cy = Int(py.rounded(.down))
        for dy in -reach...reach {
            for dx in -reach...reach {
                let x = cx + dx
                let y = cy + dy
                // Клетка открыта, если её центр в круге.
                let ex = (Double(x) + 0.5 - px) * size
                let ey = (Double(y) + 0.5 - py) * size
                if ex * ex + ey * ey <= radius * radius {
                    set(FogCell(x: x, y: y))
                }
            }
        }
    }

    /// Открывает полосу вдоль пути между двумя точками (или только концы, если разрыв длиннее `maxGap`).
    public mutating func reveal(from a: Coordinate, to b: Coordinate, radius: Double = 25, maxGap: Double = 100) {
        let length = Geodesy.distance(from: a, to: b)
        guard length <= maxGap else {
            reveal(around: a, radius: radius)
            reveal(around: b, radius: radius)
            return
        }

        // Шаг — половина клетки, чтобы круги сливались в сплошную полосу без «зубцов».
        let step = FogGrid.cellSizeMeters(atLatitude: a.latitude) / 2
        let count = max(1, Int((length / step).rounded(.up)))
        for i in 0...count {
            let t = Double(i) / Double(count)
            let point = Coordinate(
                latitude: a.latitude + (b.latitude - a.latitude) * t,
                longitude: a.longitude + (b.longitude - a.longitude) * t
            )
            reveal(around: point, radius: radius)
        }
    }

    public func isRevealed(_ cell: FogCell) -> Bool { tiles[cell.tile]?.isSet(cell.bitIndex) ?? false }

    /// Всего открытых клеток.
    public var cellCount: Int { tiles.values.reduce(0) { $0 + $1.count } }

    /// Открытая площадь, м²: клетки каждого тайла × площадь клетки на широте его центра.
    public var areaSquareMeters: Double {
        tiles.reduce(0) { total, entry in
            let latitude = FogGrid.center(of: FogCell(x: (entry.key.x << 8) + 128, y: (entry.key.y << 8) + 128))
                .latitude
            let size = FogGrid.cellSizeMeters(atLatitude: latitude)
            return total + Double(entry.value.count) * size * size
        }
    }

    /// Сколько клеток открыто здесь и не было открыто в `old` («+N га сегодня»).
    public func newCellCount(comparedTo old: FogLayer) -> Int {
        tiles.reduce(0) { total, entry in
            total + entry.value.newCount(comparedTo: old.tiles[entry.key] ?? FogTileBits())
        }
    }

    public mutating func formUnion(_ other: FogLayer) {
        for (key, bits) in other.tiles {
            tiles[key, default: FogTileBits()].formUnion(bits)
        }
    }

    private mutating func set(_ cell: FogCell) {
        tiles[cell.tile, default: FogTileBits()].set(cell.bitIndex)
    }
}
