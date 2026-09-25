#if DEBUG
    import Foundation
    import GameCore
    import GorodkiAPI
    import Networking

    /// Карта режима фикстур (`-GorodkiFixture player-map`, `-GorodkiScreen map-parcel` и др.): образцы сервера
    /// `territory.json` и `fog.json` (как их пишет сервер, через те же типы API и перевод, что у живых данных) и вокруг
    /// образца — земля и туман, похожие на настоящие: куски по тайлам UTM, как их режет сервер (кромки должны сойтись
    /// без швов), свои и чужие участки всех уровней, призрак, щит, осада, зоны «спорная» — живая и истёкшая (истёкшую
    /// карта скрывает). Ники — выдуманные, без персональных данных; «сейчас» зафиксировано — снимки одинаковые.
    enum MapFixture {
        /// «Сейчас» фикстуры: через 2 часа после визита куска 41 образца (`territory.json`, 21.09.2026 14:13 UTC).
        static let nowMs: Int64 = 1_790_000_000_000 + 2 * hour
        static let hour: Int64 = 3_600_000
        /// Игрок образцов (`me.json`, цвет 7 — Sky).
        static let me = "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b"
        /// Угол тайла 684:5775 — от него в метрах UTM разложена земля фикстуры; образец лежит в 115…215 м к востоку
        /// и 300…410 м к северу от угла.
        static let origin = PlanarPoint(east: 684_000, north: 5_775_000)

        /// Окно камеры — вся земля фикстуры: ~1 км по ширине экрана (земля — от −420 до 550 м по x).
        static let window = MapWindow(
            center: Utm34.unproject(PlanarPoint(east: origin.east + 65, north: origin.north + 280)),
            latitudeDelta: 0.009, longitudeDelta: 0.015)

        /// Касание листа участка (`map-parcel`): свой кусок 41 образца, в углу — живая зона «спорная».
        static let parcelTap = Coordinate(latitude: 52.0984, longitude: 23.6892)

        /// Ники чужих владельцев — как их отдал бы `GET /players/{id}`: ник с согласия или «Игрок #…».
        static let names: [String: String] = [
            rival(1): "Лиса-2718", rival(2): "Игрок #4290", rival(3): "Бегун-0815", rival(4): "Игрок #7342",
            rival(5): "Ёж-5120", rival(6): "Бегун-3306", rival(7): "Игрок #1188", rival(8): "Сова-9051",
        ]

        static func rival(_ number: Int) -> String {
            "0199a1b2-0000-7000-8000-00000000000\(number)"
        }

        private struct Plot {
            var owner: String
            var color: Int
            var level: Int
            var ghost = false
            var east: Double
            var north: Double
            var width: Double
            var height: Double
            var visitHoursAgo: Int64
            var shieldHours: Int64?
            var siegeHours: Int64?
        }

        /// Участки: прямоугольники со срезанными углами (радиус ≈ 7 м, как их срезает бегун — tokens.md, §3), метры
        /// от `origin`. Несколько пересекают линии тайлов x = 684 000 и y = 5 775 000.
        private static let plots: [Plot] = [
            Plot(owner: me, color: 7, level: 3, east: -180, north: 280, width: 150, height: 130, visitHoursAgo: 5),
            Plot(owner: me, color: 7, level: 1, east: 240, north: 300, width: 110, height: 160, visitHoursAgo: 100),
            Plot(owner: me, color: 7, level: 2, east: -90, north: -130, width: 160, height: 180, visitHoursAgo: 30),
            Plot(
                owner: rival(1), color: 0, level: 2, east: -60, north: 60, width: 190, height: 150, visitHoursAgo: 20),
            Plot(
                owner: rival(2), color: 1, level: 3, east: 160, north: 40, width: 170, height: 180, visitHoursAgo: 3,
                shieldHours: 9),
            Plot(
                owner: rival(3), color: 4, level: 1, east: -260, north: 40, width: 160, height: 170, visitHoursAgo: 60
            ),
            Plot(
                owner: rival(4), color: 9, level: 2, east: 60, north: 560, width: 200, height: 150, visitHoursAgo: 8,
                siegeHours: 16),
            Plot(
                owner: rival(5), color: 10, level: 1, east: -220, north: 470, width: 190, height: 200,
                visitHoursAgo: 90),
            Plot(
                owner: rival(6), color: 8, level: 3, east: 300, north: 560, width: 160, height: 170, visitHoursAgo: 2),
            Plot(
                owner: rival(7), color: 11, level: 0, ghost: true, east: 380, north: 60, width: 120, height: 140,
                visitHoursAgo: 170),
            Plot(
                owner: rival(1), color: 0, level: 1, east: 120, north: -200, width: 160, height: 150,
                visitHoursAgo: 75),
            Plot(
                owner: rival(8), color: 3, level: 2, east: -420, north: -170, width: 180, height: 200,
                visitHoursAgo: 26),
            Plot(
                owner: rival(2), color: 1, level: 1, east: 400, north: 300, width: 150, height: 200,
                visitHoursAgo: 110),
        ]

        /// Земля: тайлы образца `territory.json` и сгенерированные — одна дорога через типы API и `LandTile(_:)`.
        static func land() -> [LandTile] {
            var views: [TileKeyPair: TileViews] = [:]
            if let sample = Fixtures.sample("territory", as: Components.Schemas.TerritoryResponse.self) {
                for tile in sample.tiles {
                    views[TileKeyPair(x: Int(tile.x), y: Int(tile.y)), default: ([], [])].parcels += tile.parcels
                    views[TileKeyPair(x: Int(tile.x), y: Int(tile.y)), default: ([], [])].zones += tile.contestedZones
                }
            }
            var id: Int64 = 1_000
            for plot in plots {
                let ring = rounded(east: plot.east, north: plot.north, width: plot.width, height: plot.height)
                for (tile, piece) in MapStressScene.cutByTiles(ring) {
                    id += 1
                    let view = Components.Schemas.ParcelView(
                        id: id, ownerId: plot.owner, colorIndex: plot.color, level: plot.level, ghost: plot.ghost,
                        lastVisitAtMs: nowMs - plot.visitHoursAgo * hour,
                        shieldUntilMs: plot.shieldHours.map { nowMs + $0 * hour },
                        siegeUntilMs: plot.siegeHours.map { nowMs + $0 * hour }, exterior: flat(piece), holes: [])
                    views[TileKeyPair(x: tile.x, y: tile.y), default: ([], [])].parcels.append(view)
                }
            }
            // Живая зона на углу чужого участка и истёкшая 5 минут назад — её карта скрывает.
            let zones: [(untilMs: Int64, ring: [PlanarPoint])] = [
                (nowMs + 20 * hour, rounded(east: 250, north: 150, width: 80, height: 70)),
                (nowMs - 5 * 60_000, rounded(east: 10, north: 100, width: 90, height: 70)),
            ]
            for zone in zones {
                for (tile, piece) in MapStressScene.cutByTiles(zone.ring) {
                    views[TileKeyPair(x: tile.x, y: tile.y), default: ([], [])].zones.append(
                        Components.Schemas.ContestedZoneView(untilMs: zone.untilMs, exterior: flat(piece), holes: []))
                }
            }
            return views.keys.sorted { ($0.x, $0.y) < ($1.x, $1.y) }.compactMap { key in
                views[key].map { LandTile(key: LandTileKey(x: key.x, y: key.y), parcels: $0.parcels, zones: $0.zones) }
            }
        }

        /// Свой туман: образец `fog.json` (биты, как их упаковал сервер) и открытое вдоль своих петель и дороги к ним.
        static func fog() -> [FogTileKey: FogTileBits] {
            var layer = FogLayer()
            let own = plots.filter { $0.owner == me }
            for plot in own {
                let ring = rounded(
                    east: plot.east - 12, north: plot.north - 12, width: plot.width + 24, height: plot.height + 24)
                walk(ring + [ring[0]], into: &layer)
            }
            // Образец 41 и дорога к нему: пробежка от своих участков на юге через квартал.
            walk(rounded(east: 100, north: 290, width: 130, height: 130), into: &layer)
            walk(
                [(-40.0, -40.0), (-40, 250), (100, 250), (100, 450), (330, 470), (330, 280), (620, 280), (620, -300)]
                    .map {
                        PlanarPoint(east: origin.east + $0.0, north: origin.north + $0.1)
                    }, into: &layer)
            var tiles = layer.tiles
            if let sample = Fixtures.sample("fog", as: Components.Schemas.FogResponse.self) {
                for view in sample.tiles {
                    guard let words = try? FogTileCodec.words(fromCompressed: Data(view.bits.data)),
                        let bits = FogTileBits(words: words)
                    else { continue }
                    tiles[FogTileKey(x: Int(view.x), y: Int(view.y)), default: FogTileBits()].formUnion(bits)
                }
            }
            return tiles
        }

        /// Открыть туман вдоль ломаной (метры UTM) шагами по 20 м — как точки забега.
        private static func walk(_ points: [PlanarPoint], into layer: inout FogLayer) {
            for (a, b) in zip(points, points.dropFirst()) {
                let steps = max(1, Int((hypot(b.east - a.east, b.north - a.north) / 20).rounded(.up)))
                for step in 0..<steps {
                    let t0 = Double(step) / Double(steps)
                    let t1 = Double(step + 1) / Double(steps)
                    func at(_ t: Double) -> Coordinate {
                        Utm34.unproject(
                            PlanarPoint(east: a.east + (b.east - a.east) * t, north: a.north + (b.north - a.north) * t))
                    }
                    layer.reveal(from: at(t0), to: at(t1))
                }
            }
        }

        /// Прямоугольник со срезанными углами против часовой стрелки, метры от `origin` → метры UTM.
        private static func rounded(east: Double, north: Double, width: Double, height: Double) -> [PlanarPoint] {
            let radius = 7.0
            let corners: [(x: Double, y: Double, from: Double)] = [
                (east + width - radius, north + radius, -Double.pi / 2),
                (east + width - radius, north + height - radius, 0),
                (east + radius, north + height - radius, Double.pi / 2),
                (east + radius, north + radius, Double.pi),
            ]
            return corners.flatMap { corner in
                (0...3).map { step in
                    let angle = corner.from + Double(step) * Double.pi / 6
                    return PlanarPoint(
                        east: origin.east + corner.x + radius * cos(angle),
                        north: origin.north + corner.y + radius * sin(angle))
                }
            }
        }

        /// Кольцо в метрах UTM → `[широта, долгота, …]` с 7 знаками, замкнутое повтором первой точки — как у сервера.
        private static func flat(_ ring: [PlanarPoint]) -> [Double] {
            func round7(_ value: Double) -> Double { (value * 1e7).rounded() / 1e7 }
            return (ring + ring.prefix(1)).flatMap { point -> [Double] in
                let coordinate = Utm34.unproject(point)
                return [round7(coordinate.latitude), round7(coordinate.longitude)]
            }
        }

        private typealias TileViews = (
            parcels: [Components.Schemas.ParcelView], zones: [Components.Schemas.ContestedZoneView]
        )

        private struct TileKeyPair: Hashable {
            var x: Int
            var y: Int
        }
    }

    /// Ники фикстуры вместо `GET /players/{id}`.
    struct FixturePlayerNames: PlayerNames {
        func name(of playerId: String) async throws -> String {
            MapFixture.names[playerId] ?? "Игрок #1000"
        }
    }

    /// Разрешения фикстуры: ничего не спрошено, запросы — без системы (снимок подсказки не вызывает окна iOS).
    @MainActor
    final class FixtureRunPermissions: RunPermissions {
        var location = PermissionState.notDetermined
        var motion = PermissionState.notDetermined

        init() {}

        func requestLocation() async { location = .allowed }
        func requestMotion() async { motion = .allowed }
    }
#endif
