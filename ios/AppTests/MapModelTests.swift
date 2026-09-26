import DesignSystem
import Foundation
import GameCore
import Synchronization
import Testing

@testable import Gorodki

/// Экран «Карта» (docs/architecture/ios-app.md, «Карта»): цвет по отношению, скрытие истёкших зон, касание,
/// лист участка, фикстура.
@Suite("Карта: земля, зоны, касание, лист участка")
@MainActor
struct MapModelTests {
    private static let me = "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b"
    private static let rival = "0199a1b2-0000-7000-8000-000000000001"
    private static let tile = LandTileKey(x: 684, y: 5775)
    private static let hour: Int64 = 3_600_000
    /// 25.09.2026 18:00 по Минску.
    private static let now: Int64 = 1_790_348_400_000

    /// Квадрат 50 × 50 м в метрах от угла тайла.
    private static func square(_ east: Double, _ north: Double, size: Double = 50) -> ParcelShape {
        let ring = [(0.0, 0.0), (size, 0), (size, size), (0, size), (0, 0)].map {
            Utm34.unproject(PlanarPoint(east: 684_000 + east + $0.0, north: 5_775_000 + north + $0.1))
        }
        return ParcelShape(exterior: ring)
    }

    private static func point(_ east: Double, _ north: Double) -> Coordinate {
        Utm34.unproject(PlanarPoint(east: 684_000 + east, north: 5_775_000 + north))
    }

    private static func parcel(
        id: Int64 = 1, owner: String = me, color: Int = 7, level: Int = 2, ghost: Bool = false,
        shieldUntil: Int64? = nil, siegeUntil: Int64? = nil, at east: Double = 100, _ north: Double = 100
    ) -> LandParcel {
        LandParcel(
            id: id, ownerId: owner, colorIndex: color, level: level, ghost: ghost, lastVisitAtMs: now - 2 * hour,
            shieldUntilMs: shieldUntil, siegeUntilMs: siegeUntil, shape: square(east, north), tile: tile)
    }

    private static func profile() -> ProfileModel {
        let profile = ProfileModel(signedIn: true)
        profile.playerId = me
        profile.displayName = "Бегун-1234"
        profile.colorIndex = 7
        return profile
    }

    final class Clock: Sendable {
        private let ms = Mutex<Int64>(1_790_348_400_000)  // MapModelTests.now
        var date: Date { Date(timeIntervalSince1970: Double(ms.withLock { $0 }) / 1_000) }
        func advance(_ by: Int64) { ms.withLock { $0 += by } }
    }

    private func model(
        names: (any PlayerNames)? = nil, clock: Clock = Clock()
    ) -> MapModel {
        MapModel(profile: Self.profile(), names: names, clock: { clock.date })
    }

    // MARK: - Цвет

    @Test("«Игроки»: своя — цвет владельца как есть, чужая — альфа ×0,55, уровень — насыщенность L1–L3")
    func playersColoring() {
        let model = model()
        let mine = model.style(of: Self.parcel(level: 3))
        #expect(mine == LandStyle(relation: .mine, color: .sky, level: .three))
        #expect(mine.fill(.day) == PlayerColor.sky.fill(.three, theme: .day))
        let rival = model.style(of: Self.parcel(owner: Self.rival, color: 0, level: 1))
        #expect(rival == LandStyle(relation: .rival, color: .red, level: .one))
        #expect(rival.fill(.night).alpha == PlayerColor.red.fill(.one, theme: .night).alpha * 0.55)
        #expect(rival.edge(.day) == PlayerColor.red.edge.day.withAlpha(0.8))
        #expect(model.style(of: Self.parcel(level: 1)).fill(.day) != mine.fill(.day), "L1 и L3 различимы")
    }

    @Test("Призрак — цвет призрака темы, чей бы ни был; «Отношения»: моё — мой цвет, соперник — Red (или Orange)")
    func ghostAndRelations() {
        let model = model()
        let ghost = model.style(of: Self.parcel(owner: Self.rival, color: 4, level: 0, ghost: true))
        #expect(ghost.relation == .lost && ghost.level == nil)
        #expect(ghost.fill(.day) == Palette.ghost.day.withAlpha(0.07))
        #expect(ghost.edge(.night) == Palette.ghost.night)

        model.coloring = .relations
        #expect(model.style(of: Self.parcel(color: 3)).color == .sky, "моё — мой цвет, а не номер куска")
        #expect(model.style(of: Self.parcel(owner: Self.rival, color: 4)).color == .red)
        model.profile.colorIndex = 0  // мой цвет — Red: соперники берут соседний
        #expect(model.style(of: Self.parcel(owner: Self.rival, color: 4)).color == .orange)
    }

    @Test("Слой карты не зависит от окраски — «Отношения» перекрашивают готовые слои; кромка — без уровня")
    func groupsSurviveColoring() {
        let model = model()
        let rival = Self.parcel(owner: Self.rival, color: 16, level: 2)
        let group = model.group(of: rival)
        #expect(group == LandGroup(relation: .rival, colorIndex: 4, level: 2), "номер цвета — по модулю 12")
        #expect(group.edge.level == nil)
        #expect(model.style(of: group).color == .forest)
        model.coloring = .relations
        #expect(model.group(of: rival) == group)
        #expect(model.style(of: group).color == .red)
        #expect(model.group(of: Self.parcel(owner: Self.rival, level: 0, ghost: true)).colorIndex == 0)
    }

    // MARK: - Зоны

    @Test("Истёкшая зона скрывается по таймеру, хотя тайл не менялся; на «Исследовании» зон нет")
    func expiredZonesHide() {
        let clock = Clock()
        let model = model(clock: clock)
        let zone = ContestedZone(untilMs: Self.now + 30 * 60_000, shape: Self.square(100, 100))
        model.apply(land: [LandTile(key: Self.tile, parcels: [Self.parcel()], contestedZones: [zone])])
        #expect(model.visibleZones == [zone])

        clock.advance(29 * 60_000)
        model.tick()
        #expect(model.visibleZones == [zone])
        clock.advance(60_000)
        model.tick()
        #expect(model.visibleZones.isEmpty, "untilMs наступил — зоны нет, версия тайла та же")

        clock.advance(-60_000)
        model.tick()
        model.layer = .explore
        #expect(model.visibleZones.isEmpty)
    }

    // MARK: - Касание и лист

    @Test("Касание: свой кусок — лист сразу с моим ником; мимо — лист закрыт; на «Исследовании» — не выбирается")
    func tapOwn() {
        let model = model()
        model.apply(land: [LandTile(key: Self.tile, parcels: [Self.parcel()], contestedZones: [])])
        model.select(at: Self.point(125, 125), tolerance: 5)
        #expect(model.selection?.owner == .name("Бегун-1234"))
        #expect(model.sheet?.subtitle == "Твоя земля")
        model.select(at: Self.point(400, 400), tolerance: 5)
        #expect(model.selection == nil)
        model.layer = .explore
        model.select(at: Self.point(125, 125), tolerance: 5)
        #expect(model.selection == nil)
    }

    private struct Names: PlayerNames {
        func name(of playerId: String) async throws -> String {
            guard playerId == "0199a1b2-0000-7000-8000-000000000001" else { throw CancellationError() }
            return "Игрок #4290"
        }
    }

    @Test("Чужой кусок: ник приходит с сервера («Игрок #…»), сервер не ответил — «Владелец не загрузился»")
    func tapRival() async {
        let model = model(names: Names())
        model.apply(
            land: [
                LandTile(
                    key: Self.tile,
                    parcels: [
                        Self.parcel(id: 1, owner: Self.rival, at: 100, 100),
                        Self.parcel(id: 2, owner: "0199a1b2-0000-7000-8000-000000000009", at: 300, 100),
                    ], contestedZones: [])
            ])
        model.select(at: Self.point(125, 125), tolerance: 5)
        #expect(model.selection?.owner == .loading)
        await waitUntil { model.selection?.owner != .loading }
        #expect(model.selection?.owner == .name("Игрок #4290"))
        #expect(model.sheet?.title == "Игрок #4290")

        model.select(at: Self.point(325, 125), tolerance: 5)
        await waitUntil { model.selection?.owner != .loading }
        #expect(model.sheet?.title == "Владелец не загрузился")
        #expect(model.sheet?.subtitle == "Земля соперника")
    }

    @Test("Лист: уровень, визит, щит и осада — только действующие, спорная — если палец в живой зоне; по-русски")
    func sheetRows() throws {
        let minsk = try #require(TimeZone(identifier: "Europe/Minsk"))
        let parcel = Self.parcel(
            owner: Self.rival, level: 3, shieldUntil: Self.now + 4 * Self.hour, siegeUntil: Self.now + 20 * Self.hour)
        let zone = ContestedZone(untilMs: Self.now + 44 * Self.hour, shape: Self.square(100, 100))
        let content = ParcelSheetContent(
            ParcelSelection(parcel: parcel, zone: zone, owner: .name("Лиса-2718")), viewer: Self.me, nowMs: Self.now,
            timeZone: minsk)
        #expect(content.title == "Лиса-2718")
        #expect(
            content.rows.map { [$0.title, $0.value] } == [
                ["Уровень", "3 из 3"],
                ["Последний визит", "сегодня в 16:00"],
                ["Щит", "до 22:00"],
                ["Осада", "до завтра, 14:00"],
                ["Спорная", "до 27 сентября, 14:00"],
            ])

        let expired = ParcelSheetContent(
            ParcelSelection(
                parcel: Self.parcel(level: 0, ghost: true, shieldUntil: Self.now - 1), zone: nil, owner: .unknown),
            viewer: Self.me, nowMs: Self.now, timeZone: minsk)
        #expect(expired.subtitle == "Угасшая земля — видна призраком 3 дня")
        #expect(expired.rows.map(\.title) == ["Последний визит"], "у призрака нет уровня, истёкший щит не показан")
    }

    // MARK: - Данные

    private actor Source: MapDataSource {
        private(set) var landCalls: [(visible: Set<LandTileKey>, known: Set<LandTileKey>)] = []
        private(set) var fogCalls = 0

        func land(visible: Set<LandTileKey>, known: Set<LandTileKey>) async throws -> [LandTile] {
            landCalls.append((visible, known))
            return visible.subtracting(known).map {
                LandTile(key: $0, parcels: [], contestedZones: [])
            }
        }

        func fog(visible: Set<FogTileKey>, known: Set<FogTileKey>) async throws -> [FogTileKey: FogTileBits] {
            fogCalls += 1
            return Dictionary(uniqueKeysWithValues: visible.subtracting(known).map { ($0, FogTileBits()) })
        }
    }

    @Test("Окно карты: земля — видимые тайлы с тем, что уже известно; туман — только на «Исследовании»; весь мир — нет")
    func loadVisibleTiles() async {
        let source = Source()
        let model = MapModel(profile: Self.profile(), data: source)
        let window = MapWindow(center: Self.point(500, 500), latitudeDelta: 0.01, longitudeDelta: 0.015)
        model.show(window)
        await model.load()
        #expect(Set(model.land.tiles.keys) == Set(window.landTiles))
        #expect(await source.fogCalls == 0)
        model.layer = .explore
        await model.load()
        #expect(Set(model.fog.keys) == Set(window.fogTiles))
        #expect(await source.landCalls.last?.known == Set(window.landTiles))

        let far = Source()
        let world = MapModel(profile: Self.profile(), data: far)
        world.layer = .explore
        world.show(MapWindow(south: -60, west: -170, north: 70, east: 170))
        await world.load()
        #expect(await far.landCalls.isEmpty, "окно больше города не грузится")
        #expect(await far.fogCalls == 0)
    }

    // MARK: - Фикстура

    @Test("Фикстура карты: образец и земля вокруг, кромки без сторон по краю тайла, истёкшая зона скрыта, лист")
    func fixture() {
        let profile = Fixtures.profile("player")
        let model = Fixtures.map(.mapParcel, fixture: nil, profile: profile)
        let parcels = model.land.parcels
        #expect(parcels.contains { $0.id == 41 }, "кусок 41 образца territory.json")
        #expect(parcels.count > 15)
        #expect(Set(model.land.tiles.keys).count >= 3, "земля фикстуры задевает несколько тайлов")
        #expect(parcels.contains { $0.relation(viewer: profile.playerId) == .lost })
        #expect(model.land.activeZones(atMs: MapFixture.nowMs).count == 2, "образец и живая; истёкшая — скрыта")
        #expect(model.visibleZones.count == 2)
        #expect(!model.fog.isEmpty)
        #expect(model.selection?.parcel.id == 41)
        #expect(model.sheet?.title == "Бегун-1234")
        #expect(model.sheet?.rows.map(\.title).contains("Спорная") == true)

        #expect(Fixtures.map(.mapExplore, fixture: nil, profile: profile).layer == .explore)
        #expect(Fixtures.map(.map, fixture: "player", profile: profile).land.tiles.isEmpty)
    }

    private func waitUntil(_ condition: @MainActor () -> Bool) async {
        for _ in 0..<200 where !condition() {
            try? await Task.sleep(for: .milliseconds(10))
        }
    }
}
