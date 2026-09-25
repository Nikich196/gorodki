import DesignSystem
import Foundation
import GameCore
import Observation

/// Слой карты (PLAN.md, §5; docs/design/tokens.md, §8): «Захват» — земли без тумана, «Исследование» — туман,
/// земли скрыты.
enum MapLayer: String, CaseIterable, Identifiable, Sendable {
    case capture, explore

    var id: Self { self }

    var title: String {
        switch self {
        case .capture: "Захват"
        case .explore: "Исследование"
        }
    }
}

/// Режим окраски земли (PLAN.md, §6.3): «Игроки» — цвет владельца, «Отношения» — моё своим цветом, соперники красным.
/// «Кланы» появятся вместе с кланами на сервере (#64): в ответе `/territory` клана нет.
enum LandColoring: String, CaseIterable, Identifiable, Sendable {
    case players, relations

    var id: Self { self }

    var title: String {
        switch self {
        case .players: "Игроки"
        case .relations: "Отношения"
        }
    }
}

/// Как нарисовать кусок земли: отношение — узор и толщина кромки, цвет, уровень — насыщенность заливки
/// (docs/design/tokens.md, §3). Одинаковые стили карта сливает в один слой MapKit (PLAN.md, D4).
struct LandStyle: Hashable, Sendable {
    var relation: TerritoryRelation
    var color: PlayerColor
    /// `nil` — у призрака: заливка — цвет призрака, уровня нет.
    var level: TerritoryLevel?

    /// Заливка с альфой уровня и отношения.
    func fill(_ theme: Theme) -> RGBA {
        relation.fill(levelFill: color.fill(level ?? .one, theme: theme), theme: theme)
    }

    /// Цвет кромки.
    func edge(_ theme: Theme) -> RGBA {
        relation.edgeColor(ownerEdge: color.edge[theme], theme: theme)
    }

    /// Стиль кромки: уровень на неё не влияет, поэтому кромки куски разных уровней одного владельца — один слой.
    var edgeStyle: LandStyle {
        LandStyle(relation: relation, color: color, level: nil)
    }

    /// Стиль куска для зрителя `viewer` цвета `player`.
    static func of(_ parcel: LandParcel, viewer: String?, player: PlayerColor, coloring: LandColoring) -> LandStyle {
        let relation: TerritoryRelation =
            switch parcel.relation(viewer: viewer) {
            case .mine: .mine
            case .rival: .rival
            case .lost: .lost
            }
        let level = parcel.fillLevel.flatMap(TerritoryLevel.init(rawValue:))
        if relation == .lost {
            // У призрака свои цвета темы — цвет владельца не виден; один стиль на всех: один слой на карте.
            return LandStyle(relation: .lost, color: .red, level: nil)
        }
        let color: PlayerColor
        switch coloring {
        case .players:
            color = PlayerColor(index: parcel.colorIndex)
        case .relations:
            // «Отношения»: моё — мой цвет, соперники — Red; если мой цвет и есть Red, соперники — соседний в палитре.
            color = relation == .mine ? player : (player == .red ? .orange : .red)
        }
        return LandStyle(relation: relation, color: color, level: level)
    }
}

/// Кусок, выбранный касанием, — содержимое листа участка.
struct ParcelSelection: Identifiable, Equatable {
    /// Как зовут владельца: свой ник известен сразу, чужой приходит с сервера (`GET /players/{id}`).
    enum Owner: Equatable {
        case loading
        case name(String)
        /// Сервер не ответил: ника нет — пишем только отношение.
        case unknown
    }

    var parcel: LandParcel
    /// Неистёкшая зона «спорная» под пальцем.
    var zone: ContestedZone?
    var owner: Owner

    var id: Int64 { parcel.id }
}

/// Строки листа участка — только то, что есть в ответе `/territory` (и ник владельца из `/players/{id}`).
struct ParcelSheetContent: Equatable {
    struct Row: Equatable, Identifiable {
        var title: String
        var value: String
        var id: String { title }
    }

    var title: String
    var subtitle: String
    var rows: [Row]

    init(_ selection: ParcelSelection, viewer: String?, nowMs: Int64, timeZone: TimeZone = .current) {
        let parcel = selection.parcel
        let relation = parcel.relation(viewer: viewer)
        switch selection.owner {
        case .loading: title = "Загружаю владельца…"
        case .name(let name): title = name
        case .unknown: title = relation == .mine ? "Ты" : "Владелец не загрузился"
        }
        switch relation {
        case .mine: subtitle = "Твоя земля"
        case .rival: subtitle = "Земля соперника"
        case .lost: subtitle = "Угасшая земля — видна призраком 3 дня"
        }
        var rows: [Row] = []
        if let level = parcel.fillLevel {
            rows.append(Row(title: "Уровень", value: "\(level) из 3"))
        }
        rows.append(
            Row(
                title: "Последний визит",
                value: MomentText.past(parcel.lastVisitAtMs, now: nowMs, timeZone: timeZone)))
        if let until = parcel.shieldUntilMs, parcel.shieldActive(atMs: nowMs) {
            rows.append(Row(title: "Щит", value: MomentText.until(until, now: nowMs, timeZone: timeZone)))
        }
        if let until = parcel.siegeUntilMs, parcel.siegeActive(atMs: nowMs) {
            rows.append(
                Row(
                    title: "Осада — укреплять нельзя",
                    value: MomentText.until(until, now: nowMs, timeZone: timeZone)))
        }
        if let zone = selection.zone, zone.isActive(atMs: nowMs) {
            rows.append(
                Row(
                    title: "Спорная — её обвела большая петля",
                    value: MomentText.until(zone.untilMs, now: nowMs, timeZone: timeZone)))
        }
        self.rows = rows
    }
}

/// Шаг подсказки перед системным разрешением (PLAN.md, §6.6: «в момент надобности», одна кнопка «Продолжить»,
/// «Всегда» не просим; геопозиция — при «Старте», движение — сразу после).
enum PermissionPrimer: String, Identifiable, Sendable {
    case location, motion

    var id: Self { self }
}

/// Разрешение глазами экрана.
enum PermissionState: Sendable {
    case notDetermined, allowed, denied
}

/// Системные разрешения забега — за протоколом: в режиме фикстур и в тестах настоящих запросов нет.
@MainActor
protocol RunPermissions: AnyObject {
    var location: PermissionState { get }
    var motion: PermissionState { get }
    func requestLocation() async
    func requestMotion() async
}

/// Данные карты: земля и туман видимых тайлов. Живые — кэши `TerritoryCache` и `FogCache` (`MapSources.swift`),
/// в режиме фикстур — образцы.
protocol MapDataSource: Sendable {
    /// Земля: тайлы, пришедшие заново (с сервера или с диска), и видимые, которых карта ещё не знает (`known`).
    func land(visible: Set<LandTileKey>, known: Set<LandTileKey>) async throws -> [LandTile]
    /// Свой туман тех же правил: новые и ещё не известные карте тайлы.
    func fog(visible: Set<FogTileKey>, known: Set<FogTileKey>) async throws -> [FogTileKey: FogTileBits]
}

/// Ник владельца по номеру (`GET /players/{id}`): с его согласия ник, иначе «Игрок #1234» — так отдаёт сервер.
protocol PlayerNames: Sendable {
    func name(of playerId: String) async throws -> String
}

/// Экран «Карта»: слой, окраска, земля и туман видимых тайлов, выбранный участок, «Старт» с подсказками разрешений.
/// Простые значения — их задают кэши или образцы в режиме фикстур; рисует `GameMapView`.
@MainActor
@Observable
final class MapModel {
    /// Не просить больше стольких тайлов земли за раз: окно дальше города (весь Брест — ~180 тайлов) не грузится.
    static let maxLandTiles = 200
    /// Тайлов тумана z14 (~1,5 км) — меньше: 60 закрывают город с запасом.
    static let maxFogTiles = 60
    /// Раз в столько секунд карта скрывает истёкшие зоны и спрашивает кэши (они сами решают, что перезапросить).
    static let tickInterval: TimeInterval = 60

    var layer: MapLayer = .capture {
        didSet {
            if layer != oldValue {
                selection = nil
                scheduleLoad()
            }
        }
    }
    var coloring: LandColoring = .players
    private(set) var land = LandMap()
    /// Растёт при каждом изменении земли — по нему карта перестраивает слои.
    private(set) var landRevision = 0
    private(set) var fog: [FogTileKey: FogTileBits] = [:]
    private(set) var fogRevision = 0
    /// «Сейчас», мс Unix: по нему скрываются истёкшие зоны «спорная». Обновляется раз в минуту (`tick`).
    private(set) var nowMs: Int64
    var selection: ParcelSelection?
    var primer: PermissionPrimer?
    var startNoticeShown = false
    /// Окно, которое карта показывает при открытии.
    let initialWindow: MapWindow
    let profile: ProfileModel

    @ObservationIgnored private let data: (any MapDataSource)?
    @ObservationIgnored private let names: (any PlayerNames)?
    @ObservationIgnored private let permissions: any RunPermissions
    @ObservationIgnored private let clock: @Sendable () -> Date
    @ObservationIgnored private var window: MapWindow?
    @ObservationIgnored private var loading: Task<Void, Never>?

    /// Центр Бреста — окно по умолчанию.
    static let brest = MapWindow(
        center: Coordinate(latitude: 52.0976, longitude: 23.7341), latitudeDelta: 0.06, longitudeDelta: 0.06)

    init(
        profile: ProfileModel, data: (any MapDataSource)? = nil, names: (any PlayerNames)? = nil,
        permissions: any RunPermissions = NoRunPermissions(), initialWindow: MapWindow = MapModel.brest,
        clock: @escaping @Sendable () -> Date = { Date() }
    ) {
        self.profile = profile
        self.data = data
        self.names = names
        self.permissions = permissions
        self.initialWindow = initialWindow
        self.clock = clock
        self.nowMs = Self.milliseconds(clock())
    }

    private static func milliseconds(_ date: Date) -> Int64 {
        Int64((date.timeIntervalSince1970 * 1_000).rounded())
    }

    // MARK: - Кто смотрит

    var viewerId: String? { profile.playerId }
    var player: PlayerColor { profile.playerColor }

    // MARK: - Что рисовать

    /// Стиль куска для этого зрителя и режима окраски.
    func style(of parcel: LandParcel) -> LandStyle {
        LandStyle.of(parcel, viewer: viewerId, player: player, coloring: coloring)
    }

    /// Неистёкшие зоны «спорная» — только на «Захвате»: на «Исследовании» земли скрыты.
    var visibleZones: [ContestedZone] {
        layer == .capture ? land.activeZones(atMs: nowMs) : []
    }

    // MARK: - Данные

    /// Карта сдвинулась (конец жеста): подгрузить видимые тайлы.
    func show(_ window: MapWindow) {
        self.window = window
        scheduleLoad()
    }

    /// Раз в минуту: скрыть истёкшие зоны и спросить кэши (запасной опрос и подсказки реального времени).
    func tick() {
        nowMs = Self.milliseconds(clock())
        if let selection, let zone = selection.zone, !zone.isActive(atMs: nowMs) {
            self.selection?.zone = nil
        }
        scheduleLoad()
    }

    private func scheduleLoad() {
        guard data != nil, window != nil else { return }
        loading?.cancel()
        loading = Task { await load() }
    }

    /// Подгрузить тайлы окна: земля — всегда (её нужно и листу), туман — только на «Исследовании». Ошибки сети
    /// не показываются: на карте остаётся то, что уже есть (с диска или прошлого ответа), следующий сдвиг или минута
    /// спросят снова.
    func load() async {
        guard let data, let window else { return }
        if window.landTileCount <= Self.maxLandTiles {
            let visible = Set(window.landTiles)
            if let tiles = try? await data.land(visible: visible, known: Set(land.tiles.keys)), !tiles.isEmpty,
                !Task.isCancelled
            {
                apply(land: tiles)
            }
        }
        if layer == .explore, window.fogTileCount <= Self.maxFogTiles {
            let visible = Set(window.fogTiles)
            if let tiles = try? await data.fog(visible: visible, known: Set(fog.keys)), !tiles.isEmpty,
                !Task.isCancelled
            {
                apply(fog: tiles)
            }
        }
    }

    func apply(land tiles: [LandTile]) {
        for tile in tiles {
            land.apply(tile)
        }
        landRevision += 1
    }

    func apply(fog tiles: [FogTileKey: FogTileBits]) {
        fog.merge(tiles) { _, new in new }
        fogRevision += 1
    }

    // MARK: - Касание

    /// Касание карты: кусок под пальцем (`ParcelHitTest`) — лист участка, мимо — лист закрывается.
    /// - Parameter tolerance: допуск касания, метры (карта считает его по масштабу).
    func select(at tap: Coordinate, tolerance: Double) {
        guard layer == .capture, let parcel = land.parcel(at: tap, tolerance: tolerance) else {
            selection = nil
            return
        }
        let zone = land.contestedZone(at: tap, atMs: nowMs)
        if parcel.relation(viewer: viewerId) == .mine, let name = profile.displayName {
            selection = ParcelSelection(parcel: parcel, zone: zone, owner: .name(name))
            return
        }
        selection = ParcelSelection(parcel: parcel, zone: zone, owner: names == nil ? .unknown : .loading)
        guard let names else { return }
        let ownerId = parcel.ownerId
        let parcelId = parcel.id
        Task {
            let name = try? await names.name(of: ownerId)
            guard selection?.id == parcelId else { return }  // пока шёл запрос, выбрали другой
            selection?.owner = name.map(ParcelSelection.Owner.name) ?? .unknown
        }
    }

    var sheet: ParcelSheetContent? {
        selection.map { ParcelSheetContent($0, viewer: viewerId, nowMs: nowMs) }
    }

    // MARK: - «Старт»

    /// «Старт»: сначала подсказки к разрешениям, которых ещё не спрашивали (PLAN.md, §6.6), потом — забег. Экрана
    /// забега (HUD) ещё нет, поэтому вместо него — «Забег — скоро».
    func start() {
        if permissions.location == .notDetermined {
            primer = .location
        } else if permissions.motion == .notDetermined {
            primer = .motion
        } else {
            startNoticeShown = true
        }
    }

    /// «Продолжить» на подсказке: системный запрос, затем следующий шаг «Старта».
    func primerContinue() async {
        guard let step = primer else { return }
        primer = nil
        switch step {
        case .location: await permissions.requestLocation()
        case .motion: await permissions.requestMotion()
        }
        start()
    }
}

/// Разрешения без системы: всё уже решено — «Старт» сразу говорит «Забег — скоро». Для тестов и экранов без забега.
@MainActor
final class NoRunPermissions: RunPermissions {
    var location = PermissionState.allowed
    var motion = PermissionState.allowed

    init() {}

    func requestLocation() async {}
    func requestMotion() async {}
}
