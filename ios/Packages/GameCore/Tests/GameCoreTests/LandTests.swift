import Foundation
import Testing

@testable import GameCore

@Suite("Земля на карте: отношение, зоны «спорная», кромки без швов, окно карты")
struct LandTests {
    private static let tile = LandTileKey(x: 684, y: 5775)
    private static let me = "0199A1B2-C3D4-7E5F-8A9B-0C1D2E3F4A5B"

    /// Точка UTM 34N, метры от угла тайла `tile`, округлённая до 7 знаков — как её отдаёт сервер.
    private static func at(_ east: Double, _ north: Double, in tile: LandTileKey = tile) -> Coordinate {
        let coordinate = Utm34.unproject(
            PlanarPoint(east: Double(tile.x) * 1_000 + east, north: Double(tile.y) * 1_000 + north))
        func round7(_ value: Double) -> Double { (value * 1e7).rounded() / 1e7 }
        return Coordinate(latitude: round7(coordinate.latitude), longitude: round7(coordinate.longitude))
    }

    /// Прямоугольник против часовой стрелки, замкнутый повтором первой точки.
    private static func rect(_ east: Double, _ north: Double, _ width: Double, _ height: Double) -> [Coordinate] {
        [(0.0, 0.0), (width, 0), (width, height), (0, height), (0, 0)].map { at(east + $0.0, north + $0.1) }
    }

    private static func parcel(
        owner: String = "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b", level: Int = 2, ghost: Bool = false,
        shape: ParcelShape = ParcelShape(exterior: rect(100, 100, 50, 50)), tile: LandTileKey = tile
    ) -> LandParcel {
        LandParcel(
            id: 1, ownerId: owner, colorIndex: 7, level: level, ghost: ghost, lastVisitAtMs: 0,
            shieldUntilMs: 1_000, siegeUntilMs: nil, shape: shape, tile: tile)
    }

    @Test("Своя — по номеру игрока без учёта регистра; чужая; призрак — «потеряно», чей бы ни был; без входа — чужая")
    func relation() {
        #expect(Self.parcel().relation(viewer: Self.me) == .mine)
        #expect(Self.parcel(owner: "0299a1b2-0000-7e5f-8a9b-0c1d2e3f4a5b").relation(viewer: Self.me) == .rival)
        #expect(Self.parcel(level: 0, ghost: true).relation(viewer: Self.me) == .lost)
        #expect(Self.parcel().relation(viewer: nil) == .rival)
    }

    @Test("Уровень заливки — 1…3, неожиданное значение сервера — в пределы; у призрака заливки уровня нет")
    func fillLevel() {
        #expect(Self.parcel(level: 2).fillLevel == 2)
        #expect(Self.parcel(level: 7).fillLevel == 3)
        #expect(Self.parcel(level: 0).fillLevel == 1)
        #expect(Self.parcel(level: 0, ghost: true).fillLevel == nil)
    }

    @Test("Щит и осада действуют строго до своего момента")
    func shieldAndSiege() {
        var parcel = Self.parcel()
        #expect(parcel.shieldActive(atMs: 999))
        #expect(!parcel.shieldActive(atMs: 1_000))
        #expect(!parcel.siegeActive(atMs: 0))
        parcel.siegeUntilMs = 5
        #expect(parcel.siegeActive(atMs: 4) && !parcel.siegeActive(atMs: 5))
    }

    @Test("Зоны «спорная»: истёкшая по untilMs скрыта, хотя тайл тот же (версия при истечении не меняется)")
    func expiredZonesHidden() {
        let early = ContestedZone(untilMs: 1_000, shape: ParcelShape(exterior: Self.rect(0, 0, 50, 50)))
        let late = ContestedZone(untilMs: 5_000, shape: ParcelShape(exterior: Self.rect(200, 0, 50, 50)))
        let map = LandMap([LandTile(key: Self.tile, parcels: [], contestedZones: [early, late])])

        #expect(map.activeZones(atMs: 999) == [early, late])
        #expect(map.activeZones(atMs: 1_000) == [late])
        #expect(map.activeZones(atMs: 5_000).isEmpty)
        #expect(map.contestedZone(at: Self.at(25, 25), atMs: 999) == early)
        #expect(map.contestedZone(at: Self.at(25, 25), atMs: 1_000) == nil)
    }

    @Test("Кусок без сторон на краю тайла — одна замкнутая линия кромки")
    func wholePieceBorder() {
        let lines = LandBorders.lines(of: ParcelShape(exterior: Self.rect(100, 100, 50, 50)), in: Self.tile)
        #expect(lines.count == 1)
        #expect(lines[0].count == 5 && lines[0].first == lines[0].last)
    }

    @Test("Участок через край тайла: у каждого куска нет стороны по линии тайла — кромки сходятся без шва")
    func cutPiecesHaveNoSeam() {
        let east = LandTileKey(x: Self.tile.x + 1, y: Self.tile.y)
        // Левый кусок: 950…1000 м в тайле 684, правый: 0…50 м в тайле 685 — общая сторона на линии x = 685 000.
        let left = ParcelShape(exterior: Self.rect(950, 100, 50, 50))
        let rightInEast = ParcelShape(
            exterior: [(0.0, 0.0), (50, 0), (50, 50), (0, 50), (0, 0)].map { Self.at($0.0, 100 + $0.1, in: east) })

        let leftLines = LandBorders.lines(of: left, in: Self.tile)
        let rightLines = LandBorders.lines(of: rightInEast, in: east)
        // Три стороны одной ломаной, без стороны на краю: 4 точки, начало и конец — на линии тайла.
        #expect(leftLines.count == 1 && leftLines[0].count == 4)
        #expect(rightLines.count == 1 && rightLines[0].count == 4)
        for point in [leftLines[0].first, leftLines[0].last, rightLines[0].first, rightLines[0].last] {
            let projected = point.map(Utm34.project)
            #expect(abs((projected?.east ?? 0) - 685_000) < LandBorders.tileEdgeTolerance)
        }
    }

    @Test("Разрез посреди кольца: линия через начало кольца не разбивается надвое; дыра — своя линия")
    func cutInTheMiddleOfRing() {
        // Кольцо начинается с середины нижней стороны; разрез — правая сторона на линии x = 685 000.
        let ring = [(975.0, 100.0), (1000, 100), (1000, 150), (950, 150), (950, 100), (975, 100)].map {
            Self.at($0.0, $0.1)
        }
        let hole = [(960.0, 110.0), (970, 110), (970, 120), (960, 120), (960, 110)].map { Self.at($0.0, $0.1) }
        let lines = LandBorders.lines(of: ParcelShape(exterior: ring, holes: [hole]), in: Self.tile)
        #expect(lines.count == 2)
        #expect(lines[0].count == 5, "верх, лево, низ — одной ломаной от угла до угла на краю")
        #expect(lines[1].count == 5 && lines[1].first == lines[1].last)
    }

    @Test("Касание: кусок ищется и в соседнем тайле; мимо всех — никто")
    func hitAcrossTiles() {
        let east = LandTileKey(x: Self.tile.x + 1, y: Self.tile.y)
        let shape = ParcelShape(
            exterior: [(0.0, 0.0), (50, 0), (50, 50), (0, 50), (0, 0)].map { Self.at($0.0, 100 + $0.1, in: east) })
        let parcel = Self.parcel(shape: shape, tile: east)
        let map = LandMap([
            LandTile(key: Self.tile, parcels: [], contestedZones: []),
            LandTile(key: east, parcels: [parcel], contestedZones: []),
        ])
        #expect(map.parcel(at: Self.at(25, 125, in: east), tolerance: 5) == parcel)
        #expect(map.parcel(at: Self.at(-2, 125, in: east), tolerance: 5) == parcel, "палец в 2 м от края — он")
        #expect(map.parcel(at: Self.at(500, 500), tolerance: 5) == nil)
    }

    @Test("Окно карты: тайлы земли и тумана покрывают все углы; число — без перечисления")
    func window() {
        let center = Self.at(500, 500)
        let window = MapWindow(center: center, latitudeDelta: 0.02, longitudeDelta: 0.03)
        let land = Set(window.landTiles)
        #expect(land.count == window.landTileCount)
        for corner in [
            Coordinate(latitude: window.south, longitude: window.west),
            Coordinate(latitude: window.north, longitude: window.east),
            Coordinate(latitude: window.south, longitude: window.east),
            Coordinate(latitude: window.north, longitude: window.west),
        ] {
            #expect(land.contains(LandTileKey.containing(corner)))
            #expect(window.fogTiles.contains(FogGrid.cell(of: corner).tile))
        }
        #expect(window.fogTiles.count == window.fogTileCount)
        let world = MapWindow(south: -80, west: -170, north: 80, east: 170)
        #expect(world.fogTileCount > 10_000_000, "весь мир — число, а не список")
    }
}

@Suite("Моменты по-русски: лист участка")
struct MomentTextTests {
    private let minsk = TimeZone(identifier: "Europe/Minsk") ?? .current
    /// 25.09.2026 18:00 по Минску (UTC+3).
    private let now: Int64 = 1_790_348_400_000
    private let hour: Int64 = 3_600_000

    @Test("Прошедшее: сегодня, вчера, дата в этом году и в другом — «в ЧЧ:ММ»")
    func past() {
        #expect(MomentText.past(now - 2 * hour, now: now, timeZone: minsk) == "сегодня в 16:00")
        #expect(MomentText.past(now - 20 * hour, now: now, timeZone: minsk) == "вчера в 22:00")
        #expect(MomentText.past(now - 72 * hour, now: now, timeZone: minsk) == "22 сентября в 18:00")
        #expect(MomentText.past(now - 400 * 24 * hour, now: now, timeZone: minsk) == "21 августа 2025 в 18:00")
    }

    @Test("Срок: сегодня — только время, завтра и дальше — с днём")
    func until() {
        #expect(MomentText.until(now + 4 * hour, now: now, timeZone: minsk) == "до 22:00")
        #expect(MomentText.until(now + 8 * hour + 30 * 60_000, now: now, timeZone: minsk) == "до завтра, 02:30")
        #expect(MomentText.until(now + 68 * hour, now: now, timeZone: minsk) == "до 28 сентября, 14:00")
    }
}
