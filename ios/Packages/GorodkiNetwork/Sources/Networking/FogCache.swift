import Foundation
import GorodkiAPI
import OpenAPIRuntime

/// Тайл тумана — тайл веб-меркатора уровня 14 (256 × 256 клеток G22), как в `GET /fog` (docs/architecture/fog.md).
public struct FogTileRef: Hashable, Sendable, Comparable {
    public var x: Int
    public var y: Int

    public init(x: Int, y: Int) {
        self.x = x
        self.y = y
    }

    public static func < (a: FogTileRef, b: FogTileRef) -> Bool { a.x != b.x ? a.x < b.x : a.y < b.y }
}

/// Свой туман на телефоне — тайлы `GET /fog` одного слоя и сезона с версиями.
///
/// Туман меняется только от своих забегов: сервер открывает его один раз за забег, когда тот доставлен. Поэтому опроса
/// нет — тайл запрашивается, если его нет в кэше или после `invalidate()` (подсказка `FogChanged`; подключение реального
/// времени — подсказки за разрыв потеряны). Версии свои у каждого игрока — при смене аккаунта кэш очищается (`reset`).
/// Повреждённый тайл пропускается и считается в `corruptedTiles`, остальные — нет.
public actor FogCache {
    /// Не больше стольких тайлов за запрос — предел сервера.
    public static let maxTilesPerRequest = 25
    /// Тайлов в памяти — не больше (8 КБ каждый); лишние, давно не нужные карте, выбрасываются.
    public static let capacity = 400

    public struct Tile: Equatable, Sendable {
        public var key: FogTileRef
        public var version: Int64
        public var cellCount: Int
        /// Биты тайла: 1 024 слова по 64 бита, клетка `(x, y)` — бит `y · 256 + x`. Пусто — в тайле ничего не открыто
        /// (версия 0: сервер его не хранит).
        public var words: [UInt64]
    }

    public nonisolated let layer: Components.Schemas.FogLayerKind
    /// Сезон; `nil` — за всё время.
    public nonisolated let season: Int?
    private let api: any APIProtocol
    private let now: @Sendable () -> Double
    private var tiles: [FogTileRef: Tile] = [:]
    private var stale: Set<FogTileRef> = []
    /// Сколько раз туман объявлялся изменившимся (`invalidate`): ответ на запрос, начатый до последнего раза, мог быть
    /// собран до изменения — пометку «устарел» он не снимает.
    private var invalidations = 0
    private var lastWanted: [FogTileRef: Double] = [:]
    /// Номер «поколения»: `reset` его меняет, и ответ, начатый до смены аккаунта, выбрасывается.
    private var generation = 0
    public private(set) var corruptedTiles = 0

    public init(
        api: any APIProtocol, layer: Components.Schemas.FogLayerKind, season: Int? = nil,
        now: @escaping @Sendable () -> Double = { Date().timeIntervalSince1970 }
    ) {
        self.api = api
        self.layer = layer
        self.season = season
        self.now = now
    }

    public func tile(_ key: FogTileRef) -> Tile? { tiles[key] }

    public var count: Int { tiles.count }

    /// Туман мог измениться: все тайлы в кэше — и те, что запрашиваются сейчас, — перезапросить с версиями, когда карта
    /// их покажет.
    public func invalidate() {
        invalidations += 1
        stale.formUnion(tiles.keys)
    }

    /// Смена аккаунта.
    public func reset() {
        tiles = [:]
        stale = []
        lastWanted = [:]
        generation += 1
    }

    /// Обновить тайлы, которые показывает карта.
    /// - Returns: тайлы с новыми данными — их нужно перерисовать.
    @discardableResult
    public func refresh(visible: Set<FogTileRef>) async throws -> Set<FogTileRef> {
        let time = now()
        for key in visible {
            lastWanted[key] = time
        }
        let due = visible.filter { tiles[$0] == nil || stale.contains($0) }.sorted()
        var updated: Set<FogTileRef> = []
        for start in stride(from: 0, to: due.count, by: Self.maxTilesPerRequest) {
            let batch = Array(due[start..<min(start + Self.maxTilesPerRequest, due.count)])
            updated.formUnion(try await fetch(batch))
        }
        evict()
        return updated
    }

    private func fetch(_ keys: [FogTileRef]) async throws -> Set<FogTileRef> {
        // Неизвестный тайл — с версией 0: пустой тайл сервер не хранит и вернёт в `unchanged`, а не промолчит.
        let query = keys.map { key in "\(key.x):\(key.y)@\(tiles[key]?.version ?? 0)" }
        let requestedIn = generation
        let invalidationsBefore = invalidations
        let output = try await api.getFog(
            query: .init(layer: layer.rawValue, tiles: query.joined(separator: ","), season: season.map { Int32($0) }))
        let response: Components.Schemas.FogResponse
        switch output {
        case .ok(let ok):
            response = try ok.body.json
        case .badRequest:
            throw FogCacheError.unexpectedStatus(400)
        case .undocumented(let status, _):
            throw FogCacheError.unexpectedStatus(status)
        }
        guard generation == requestedIn else { return [] }  // пока шёл запрос, аккаунт сменился

        var updated: Set<FogTileRef> = []
        for view in response.tiles {
            let key = FogTileRef(x: Int(view.x), y: Int(view.y))
            // Ответ на более ранний запрос мог прийти позже: туман только растёт, более новую версию он не затирает.
            if let known = tiles[key], known.version > view.version {
                continue
            }
            // Повреждённый (или не сошёлся с `cellCount`) — прежний тайл остаётся и будет перезапрошен.
            guard let words = try? FogTileCodec.words(fromCompressed: Data(view.bits.data)),
                FogTileCodec.cellCount(words) == Int(view.cellCount)
            else {
                corruptedTiles += 1
                continue
            }
            stale.remove(key)
            tiles[key] = Tile(key: key, version: view.version, cellCount: Int(view.cellCount), words: words)
            updated.insert(key)
        }
        for ref in response.unchanged {
            let key = FogTileRef(x: Int(ref.x), y: Int(ref.y))
            stale.remove(key)
            if tiles[key] == nil {
                tiles[key] = Tile(key: key, version: 0, cellCount: 0, words: [])  // пустой тайл
            }
        }
        // `invalidate`, пришедший во время запроса, мог опоздать к ответу: сервер собрал его до того, как открыл туман.
        // Такие тайлы остаются устаревшими — иначе свежий туман не показался бы до следующей подсказки.
        if invalidations != invalidationsBefore {
            stale.formUnion(keys.filter { tiles[$0] != nil })
        }
        return updated
    }

    private func evict() {
        guard tiles.count > Self.capacity else { return }
        let oldest = tiles.keys.sorted { ((lastWanted[$0] ?? 0), $0) < ((lastWanted[$1] ?? 0), $1) }
        for key in oldest.prefix(tiles.count - Self.capacity) {
            tiles[key] = nil
            lastWanted[key] = nil
            stale.remove(key)
        }
    }
}

public enum FogCacheError: Error, Equatable {
    /// Сервер ответил не так, как описано в контракте.
    case unexpectedStatus(Int)
}
