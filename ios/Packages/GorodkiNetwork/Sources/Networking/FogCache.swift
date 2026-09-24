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
/// нет — тайл запрашивается, если его нет в кэше или после `invalidate()` (синхронизация доставила забег). Версии свои
/// у каждого игрока — при смене аккаунта кэш очищается (`reset`). Повреждённый тайл пропускается и считается
/// в `corruptedTiles`, остальные — нет.
public actor FogCache {
    /// Не больше стольких тайлов за запрос — предел сервера.
    public static let maxTilesPerRequest = 25
    /// Тайлов в памяти — не больше (8 КБ каждый); лишние, давно не нужные карте, выбрасываются.
    public static let capacity = 400

    public struct Tile: Equatable, Sendable {
        public var key: FogTileRef
        public var version: Int64
        public var cellCount: Int
        /// Биты тайла: 1 024 слова по 64 бита, клетка `(x, y)` — бит `y · 256 + x`.
        public var words: [UInt64]
    }

    public nonisolated let layer: Components.Schemas.FogLayerKind
    /// Сезон; `nil` — за всё время.
    public nonisolated let season: Int?
    private let api: any APIProtocol
    private let now: @Sendable () -> Double
    private var tiles: [FogTileRef: Tile] = [:]
    private var stale: Set<FogTileRef> = []
    private var lastWanted: [FogTileRef: Double] = [:]
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

    /// Туман мог измениться (забег доставлен): все тайлы в кэше перезапросить с версиями, когда карта их покажет.
    public func invalidate() {
        stale.formUnion(tiles.keys)
    }

    /// Смена аккаунта.
    public func reset() {
        tiles = [:]
        stale = []
        lastWanted = [:]
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
        let query = keys.map { key in
            tiles[key].map { "\(key.x):\(key.y)@\($0.version)" } ?? "\(key.x):\(key.y)"
        }
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

        var updated: Set<FogTileRef> = []
        for view in response.tiles {
            let key = FogTileRef(x: Int(view.x), y: Int(view.y))
            stale.remove(key)
            guard let words = try? FogTileCodec.words(fromCompressed: Data(view.bits.data)) else {
                corruptedTiles += 1
                continue
            }
            tiles[key] = Tile(key: key, version: view.version, cellCount: Int(view.cellCount), words: words)
            updated.insert(key)
        }
        for ref in response.unchanged {
            stale.remove(FogTileRef(x: Int(ref.x), y: Int(ref.y)))
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
