import Foundation
import GameCore

/// Где стоит стенд S4: вокруг какой точки участки и туман и куда смотрит камера.
enum MapStressPreset: String, CaseIterable, Identifiable, Sendable {
    /// Центр Бреста — прежний стенд.
    case city
    /// «Арена БрГТУ» (PLAN.md §3.6: «радиус ~1,5 км вокруг корпусов и общежитий») — для S4-вид: читаются ли дорожки
    /// Арены на карте Apple, в том числе под туманом. Кварталов Арены стенд не знает: их границы решает Никита, для
    /// нагрузки на отрисовку хватает области.
    case arena

    var id: Self { self }

    var title: String {
        switch self {
        case .city: "Центр Бреста"
        case .arena: "Арена БрГТУ"
        }
    }

    var center: Coordinate {
        switch self {
        case .city:
            Coordinate(latitude: 52.0976, longitude: 23.6880)
        case .arena:
            // Кампус БрГТУ (ул. Московская, 267): центр области OpenStreetMap way 411586971 (amenity=university,
            // «Брэсцкі дзяржаўны тэхнічны ўніверсітэт», рамка 52,0929–52,0985° с. ш. × 23,7552–23,7612° в. д.)
            // по Nominatim на 24.09.2026. Корпуса 1–5 с адресом «Московская, 267» — внутри этой рамки.
            Coordinate(latitude: 52.095_776_5, longitude: 23.758_558_4)
        }
    }

    /// Радиус области с участками, метры; камера показывает её целиком.
    var radiusMeters: Double {
        switch self {
        case .city: 2_000  // размах прежнего стенда (квадрат ±2 км)
        case .arena: 1_500  // PLAN.md §3.6
        }
    }
}

/// Синтетическая карта для стенда S4 (PLAN.md §10, «Пробная сборка») и проверки S13 «снимок границ без швов».
///
/// - **5 000 участков** — число из критерия S4. Вершин — в среднем 82, как у настоящих кусков после 330 захватов
///   на «Арене» (замер движка, docs/adr/0003-territory-engine.md). Участки не перекрываются, как настоящая земля:
///   каждый — эллипс в своей ячейке сетки. Плотнее настоящей карты: все 5 000 видны разом в окне Арены — худший случай
///   для отрисовки. Эллипс выпуклый, поэтому край тайла режет его ровно на один кусок в каждом тайле.
/// - **Куски по тайлам UTM 1×1 км**, как их отдаёт сервер (`GET /territory`: одна строка — кусок внутри тайла). Если
///   отрисовщик не сливает куски (D4: один `MKMultiPolygon` на пару «владелец — уровень»), на краях тайлов видны швы.
///   Чтобы шов было где искать, на ближайшем к центру углу тайлов — **мишень**: круглый участок, разрезанный на четыре.
///   Её смотрят без тумана (docs/guides/probe-walk.md): на «Арене» через неё проходят «Т» и круг таяния стенда.
/// - **Контуры целых участков** — для слоя линий границ (D4: «границы — отдельным слоем линий»; сервер строит их
///   по контуру, объединённому поверх тайлов): на краях тайлов линий нет.
/// - **Туман на 300 тайлах z14** (критерий S4): в каждом из 300 ближайших к центру тайлов — короткая прогулка, у центра —
///   густые прогулки и буква «Т» перекладиной на север (по ней видно, не перевёрнута ли маска).
///
/// Всё из генератора с постоянным зерном: карта одна и та же при каждом запуске.
struct MapStressScene: Sendable {
    /// Кусок участка внутри одного тайла — как строка ответа сервера.
    struct Piece: Sendable {
        /// Номер участка в `outlines`.
        var parcel: Int
        var tile: Tile
        var ring: [Coordinate]
    }

    /// Целый участок: группа заливки и контур до резки по тайлам.
    struct Outline: Sendable {
        /// Группа заливки: цвет игрока и уровень (`group % colors` — цвет, `group / colors` — уровень от нуля).
        var group: Int
        var ring: [Coordinate]
    }

    /// Тайл карты: (⌊x/1000⌋, ⌊y/1000⌋) в метрах UTM 34N, как ключ сервера.
    struct Tile: Hashable, Sendable {
        var x: Int
        var y: Int
    }

    static let parcelCount = 5_000
    /// Среднее число вершин участка — замер движка (docs/adr/0003-territory-engine.md).
    static let averageVertices = 82
    static let fogTileCount = 300
    /// Уровней участка у стенда (насыщенность заливки, PLAN.md §6.3).
    static let levels = 3
    /// Радиус мишени, метры: в окне Арены (3 км) — кружок заметного размера. Число стенда, а не игры.
    static let seamTargetRadiusMeters = 150.0

    let preset: MapStressPreset
    let outlines: [Outline]
    let pieces: [Piece]
    let fog: FogLayer
    /// Центр мишени — угол четырёх тайлов.
    let seamTarget: Coordinate

    /// Тайлы, в которых есть куски.
    var tileCount: Int { Set(pieces.map(\.tile)).count }

    /// - Parameter colors: сколько цветов игроков (у приложения — 12, PLAN.md §6.5); групп заливки — `colors × levels`.
    static func make(preset: MapStressPreset, colors: Int) -> MapStressScene {
        var generator = SeededGenerator(seed: 2026)
        let center = Utm34.project(preset.center)
        let corner = PlanarPoint(
            east: (center.east / Utm34.tileSizeMeters).rounded() * Utm34.tileSizeMeters,
            north: (center.north / Utm34.tileSizeMeters).rounded() * Utm34.tileSizeMeters)

        let target = ellipse(
            center: corner, a: seamTargetRadiusMeters, b: seamTargetRadiusMeters, rotation: 0,
            vertices: averageVertices, generator: &generator)
        var parcels: [(group: Int, ring: [PlanarPoint])] = [(0, target)]
        let groups = colors * levels
        for cell in cells(center: center, radius: preset.radiusMeters, avoiding: corner) {
            let group = Int.random(in: 0..<groups, using: &generator)
            parcels.append((group, parcel(in: cell, generator: &generator)))
        }

        var pieces: [Piece] = []
        for (index, parcel) in parcels.enumerated() {
            for (tile, ring) in cutByTiles(parcel.ring) {
                pieces.append(Piece(parcel: index, tile: tile, ring: ring.map(Utm34.unproject)))
            }
        }

        return MapStressScene(
            preset: preset,
            outlines: parcels.map { Outline(group: $0.group, ring: $0.ring.map(Utm34.unproject)) },
            pieces: pieces,
            fog: fog(around: preset.center, generator: &generator),
            seamTarget: Utm34.unproject(corner))
    }

    // MARK: - Участки

    /// Квадратная ячейка сетки: центр и сторона, метры UTM.
    struct Cell {
        var center: PlanarPoint
        var size: Double
    }

    /// Ячейки для `parcelCount − 1` участков (один — мишень): ближайшие к центру в пределах радиуса, кроме тех, что
    /// задели бы мишень. Сторона — с запасом, чтобы ячеек в круге хватило и за вычетом мишени.
    static func cells(center: PlanarPoint, radius: Double, avoiding corner: PlanarPoint) -> [Cell] {
        let size = (Double.pi * radius * radius / Double(parcelCount)).squareRoot() * 0.95
        let reach = Int((radius / size).rounded(.up))
        var cells: [(distance: Double, cell: Cell)] = []
        for i in -reach...reach {
            for j in -reach...reach {
                let point = PlanarPoint(east: center.east + Double(i) * size, north: center.north + Double(j) * size)
                let distance = hypot(point.east - center.east, point.north - center.north)
                // Ячейка целиком дальше от угла, чем край мишени: половина диагонали — 0,71 стороны.
                let clearOfTarget =
                    hypot(point.east - corner.east, point.north - corner.north) > seamTargetRadiusMeters + size * 0.75
                if distance <= radius, clearOfTarget {
                    cells.append((distance, Cell(center: point, size: size)))
                }
            }
        }
        // Равные расстояния — по координатам: порядок не зависит от сортировки.
        cells.sort {
            ($0.distance, $0.cell.center.east, $0.cell.center.north)
                < ($1.distance, $1.cell.center.east, $1.cell.center.north)
        }
        return cells.prefix(parcelCount - 1).map(\.cell)
    }

    /// Эллипс внутри ячейки: полуоси 0,30–0,45 стороны, сдвиг не выводит его за ячейку — соседи не перекрываются.
    private static func parcel(in cell: Cell, generator: inout SeededGenerator) -> [PlanarPoint] {
        let a = cell.size * Double.random(in: 0.30...0.45, using: &generator)
        let b = cell.size * Double.random(in: 0.30...0.45, using: &generator)
        let slack = cell.size / 2 - max(a, b)
        let center = PlanarPoint(
            east: cell.center.east + Double.random(in: -slack...slack, using: &generator),
            north: cell.center.north + Double.random(in: -slack...slack, using: &generator))
        return ellipse(
            center: center, a: a, b: b, rotation: Double.random(in: 0..<Double.pi, using: &generator),
            vertices: Int.random(in: (averageVertices / 2)...(averageVertices * 3 / 2), using: &generator),
            generator: &generator)
    }

    /// Точки эллипса при случайных углах по возрастанию: выпуклый контур против часовой стрелки с неровным шагом
    /// вершин, как у следа GPS.
    private static func ellipse(
        center: PlanarPoint, a: Double, b: Double, rotation: Double, vertices: Int, generator: inout SeededGenerator
    ) -> [PlanarPoint] {
        let angles = (0..<vertices).map { _ in Double.random(in: 0..<(2 * Double.pi), using: &generator) }.sorted()
        return angles.map { angle in
            let x = a * cos(angle)
            let y = b * sin(angle)
            return PlanarPoint(
                east: center.east + x * cos(rotation) - y * sin(rotation),
                north: center.north + x * sin(rotation) + y * cos(rotation))
        }
    }

    // MARK: - Резка по тайлам

    /// Куски выпуклого контура по тайлам UTM 1×1 км — как у сервера: у соседних кусков общий край по линии тайла.
    static func cutByTiles(_ ring: [PlanarPoint]) -> [(Tile, [PlanarPoint])] {
        let size = Utm34.tileSizeMeters
        let east = ring.map(\.east)
        let north = ring.map(\.north)
        guard let minEast = east.min(), let maxEast = east.max(), let minNorth = north.min(), let maxNorth = north.max()
        else { return [] }
        let columns = Int((minEast / size).rounded(.down))...Int((maxEast / size).rounded(.down))
        let rows = Int((minNorth / size).rounded(.down))...Int((maxNorth / size).rounded(.down))
        guard columns.count > 1 || rows.count > 1 else {
            return [(Tile(x: columns.lowerBound, y: rows.lowerBound), ring)]
        }
        var pieces: [(Tile, [PlanarPoint])] = []
        for x in columns {
            for y in rows {
                let piece = clip(
                    ring, east: (Double(x) * size)...(Double(x + 1) * size),
                    north: (Double(y) * size)...(Double(y + 1) * size))
                // Касание края тайла точкой или отрезком — не кусок; тонкий осколок — кусок, как у сервера до слияния.
                // Площадь — от угла тайла: в абсолютных метрах UTM (миллионы) формула шнурования теряет точность.
                let local = piece.map {
                    PlanarPoint(east: $0.east - Double(x) * size, north: $0.north - Double(y) * size)
                }
                if piece.count >= 3, PlanarRing(local).area > 1e-6 {
                    pieces.append((Tile(x: x, y: y), piece))
                }
            }
        }
        return pieces
    }

    /// Часть выпуклого контура внутри прямоугольника (алгоритм Сазерленда — Ходжмена). Точка на краю получает координату
    /// края ровно, поэтому у кусков по обе стороны края тайла общие вершины совпадают.
    static func clip(_ ring: [PlanarPoint], east: ClosedRange<Double>, north: ClosedRange<Double>) -> [PlanarPoint] {
        enum Side: CaseIterable { case minEast, maxEast, minNorth, maxNorth }

        func inside(_ point: PlanarPoint, _ side: Side) -> Bool {
            switch side {
            case .minEast: point.east >= east.lowerBound
            case .maxEast: point.east <= east.upperBound
            case .minNorth: point.north >= north.lowerBound
            case .maxNorth: point.north <= north.upperBound
            }
        }

        func crossing(_ from: PlanarPoint, _ to: PlanarPoint, _ side: Side) -> PlanarPoint {
            switch side {
            case .minEast, .maxEast:
                let line = side == .minEast ? east.lowerBound : east.upperBound
                let t = (line - from.east) / (to.east - from.east)
                return PlanarPoint(east: line, north: from.north + t * (to.north - from.north))
            case .minNorth, .maxNorth:
                let line = side == .minNorth ? north.lowerBound : north.upperBound
                let t = (line - from.north) / (to.north - from.north)
                return PlanarPoint(east: from.east + t * (to.east - from.east), north: line)
            }
        }

        var output = ring
        for side in Side.allCases {
            guard let last = output.last else { return [] }
            let input = output
            output = []
            var previous = last
            for current in input {
                if inside(current, side) {
                    if !inside(previous, side) {
                        output.append(crossing(previous, current, side))
                    }
                    output.append(current)
                } else if inside(previous, side) {
                    output.append(crossing(previous, current, side))
                }
                previous = current
            }
        }
        return output
    }

    // MARK: - Туман

    /// Короткая прогулка в каждом из `fogTileCount` ближайших к центру тайлов z14, густые прогулки у центра и «Т».
    private static func fog(around center: Coordinate, generator: inout SeededGenerator) -> FogLayer {
        var fog = FogLayer()
        let home = FogGrid.cell(of: center).tile
        let reach = Int((Double(fogTileCount).squareRoot() / 2).rounded(.up)) + 1
        var tiles: [FogTileKey] = []
        for dx in -reach...reach {
            for dy in -reach...reach {
                tiles.append(FogTileKey(x: home.x + dx, y: home.y + dy))
            }
        }
        func distance(_ tile: FogTileKey) -> Int {
            (tile.x - home.x) * (tile.x - home.x) + (tile.y - home.y) * (tile.y - home.y)
        }
        tiles.sort { (distance($0), $0) < (distance($1), $1) }
        for tile in tiles.prefix(fogTileCount) {
            let start = FogGrid.center(
                of: FogCell(
                    x: tile.x * FogGrid.cellsPerTileSide
                        + Int.random(in: 0..<FogGrid.cellsPerTileSide, using: &generator),
                    y: tile.y * FogGrid.cellsPerTileSide
                        + Int.random(in: 0..<FogGrid.cellsPerTileSide, using: &generator)))
            walk(
                from: LocalTangentPlane(origin: start), at: PlanarPoint(east: 0, north: 0), steps: 60, into: &fog,
                generator: &generator)
        }

        let plane = LocalTangentPlane(origin: center)
        for _ in 0..<40 {
            let start = PlanarPoint(
                east: Double.random(in: -3_000...3_000, using: &generator),
                north: Double.random(in: -3_000...3_000, using: &generator))
            walk(from: plane, at: start, steps: 200, into: &fog, generator: &generator)
        }
        revealNorthMarker(plane: plane, into: &fog)
        return fog
    }

    /// Прогулка шагами по 8 м с плавными поворотами.
    private static func walk(
        from plane: LocalTangentPlane, at start: PlanarPoint, steps: Int, into fog: inout FogLayer,
        generator: inout SeededGenerator
    ) {
        var x = start.east
        var y = start.north
        var heading = Double.random(in: 0..<(2 * Double.pi), using: &generator)
        var previous = plane.unproject(start)
        for _ in 0..<steps {
            heading += Double.random(in: -0.4...0.4, using: &generator)
            x += 8 * cos(heading)
            y += 8 * sin(heading)
            let next = plane.unproject(PlanarPoint(east: x, north: y))
            fog.reveal(from: previous, to: next)
            previous = next
        }
    }

    /// Буква «Т» перекладиной на север: по ней на телефоне сразу видно, не перевёрнута ли маска.
    private static func revealNorthMarker(plane: LocalTangentPlane, into fog: inout FogLayer) {
        func line(_ a: (Double, Double), _ b: (Double, Double)) {
            fog.reveal(
                from: plane.unproject(PlanarPoint(east: a.0, north: a.1)),
                to: plane.unproject(PlanarPoint(east: b.0, north: b.1)),
                radius: 30, maxGap: 1_000)
        }
        line((0, -400), (0, 400))
        line((-300, 400), (300, 400))
    }
}

/// Предсказуемый генератор случайных чисел: одна и та же «карта» при каждом запуске.
struct SeededGenerator: RandomNumberGenerator {
    private var state: UInt64

    init(seed: UInt64) {
        state = seed &+ 0x9E37_79B9_7F4A_7C15
    }

    mutating func next() -> UInt64 {
        // SplitMix64.
        state &+= 0x9E37_79B9_7F4A_7C15
        var z = state
        z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
        z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
        return z ^ (z >> 31)
    }
}
