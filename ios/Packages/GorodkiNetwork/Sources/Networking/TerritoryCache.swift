import Foundation
import GorodkiAPI
import OpenAPIRuntime

/// Земля на телефоне — тайлы `GET /territory` с видимыми версиями (docs/architecture/territory-map.md). Карта просит
/// видимые тайлы, кэш решает, какие перезапросить, и шлёт известные версии: сервер отдаёт только изменившееся.
///
/// Тайл перезапрашивается, если:
/// - его нет в кэше;
/// - пришла подсказка реального времени `TilesChanged` (`markChanged`);
/// - он старше `pollInterval` — запасной опрос раз в 5 минут, если подсказка потерялась.
///
/// Угасание сервер считает при чтении, и версия тайла его не отражает: уровень падает без смены версии. Поэтому тайл
/// старше `maxAge` перезапрашивается целиком, без версии. Версии у каждого зрителя свои (скрытые чужие захваты), поэтому
/// при смене аккаунта кэш очищается (`reset`).
///
/// С `disk` тайлы переживают перезапуск: каждый принятый тайл пишется в файл, а недостающие в памяти читаются с диска
/// (`restore`) — карта рисуется без сети. Восстановленный тайл один раз перепроверяется у сервера, а время его загрузки
/// берётся из файла: угасание после перезапуска всё так же перезапрашивается целиком (docs/architecture/ios-app.md).
public actor TerritoryCache {
    /// Не больше стольких тайлов за запрос — предел сервера.
    public static let maxTilesPerRequest = 25
    /// Запасной опрос, секунды.
    public static let pollInterval: Double = 300
    /// Старше этого тайл перезапрашивается без версии (угасание), секунды.
    public static let maxAge: Double = 3_600
    /// Тайлов в памяти — не больше; лишние, давно не нужные карте, выбрасываются.
    public static let capacity = 400

    public struct Tile: Equatable, Sendable {
        public var key: TileKey
        /// Видимая версия — какой её видит этот игрок.
        public var version: Int64
        public var parcels: [Components.Schemas.ParcelView]
        /// Зоны «спорная» (большая петля, §3.3). Сервер присылает только неистёкшие, но тайл живёт здесь до часа,
        /// а версия при истечении зоны не меняется: истёкшие по `untilMs` скрывает карта.
        public var contestedZones: [Components.Schemas.ContestedZoneView]
        /// Когда тайл пришёл с сервера целиком (секунды): от этого считается `maxAge`.
        public var loadedAt: Double
        /// Когда сервер последний раз подтвердил тайл (целиком или «без изменений»): от этого считается опрос.
        public var checkedAt: Double
    }

    public nonisolated let league: League
    private let api: any APIProtocol
    private let now: @Sendable () -> Double
    private var tiles: [TileKey: Tile] = [:]
    /// Тайлы с подсказкой → номер последней подсказки: ответ снимает пометку, только если после его запроса новых
    /// подсказок про тайл не было.
    private var changed: [TileKey: Int] = [:]
    private var hints = 0
    private var lastWanted: [TileKey: Double] = [:]
    /// Номер «поколения»: `reset` его меняет, и ответ, начатый до смены аккаунта, выбрасывается.
    private var generation = 0
    private var disk: TileDiskBinding?
    /// Файлы, отброшенные при чтении как испорченные (они стёрты, тайл запрашивается заново).
    public private(set) var corruptedFiles = 0
    /// Тайлы, не записанные на диск: кэш в памяти от этого не страдает, после перезапуска их просто запросят.
    public private(set) var diskWriteFailures = 0

    /// - Parameters:
    ///   - disk: где хранить тайлы между запусками; `nil` — только в памяти.
    ///   - now: часы в секундах — подменяются в проверках.
    public init(
        api: any APIProtocol, league: League, disk: TileCacheDisk? = nil,
        now: @escaping @Sendable () -> Double = { Date().timeIntervalSince1970 }
    ) {
        self.api = api
        self.league = league
        self.disk = disk.map { TileDiskBinding(disk: $0, cache: "territory-\(league.rawValue)") }
        self.now = now
    }

    public func tile(_ key: TileKey) -> Tile? { tiles[key] }

    public var count: Int { tiles.count }

    /// Подсказка реального времени: эти тайлы изменились — перезапросить, когда карта их покажет.
    public func markChanged(_ keys: some Sequence<TileKey>) {
        hints += 1
        for key in keys {
            changed[key] = hints
        }
    }

    /// Смена аккаунта: версии прежнего зрителя новому не подходят. Файлы стираются все — и других игроков.
    public func reset() {
        resetMemory()
        disk?.forget()
    }

    private func resetMemory() {
        tiles = [:]
        changed = [:]
        lastWanted = [:]
        generation += 1
    }

    /// Показать тайлы с диска без сети (авиарежим, сразу после запуска). Тайлы, уже бывшие в памяти, не трогаются.
    /// - Returns: восстановленные тайлы — их нужно нарисовать.
    @discardableResult
    public func restore(visible: Set<TileKey>) async -> Set<TileKey> {
        await bindCurrentOwner()
        return restoreMissing(visible)
    }

    /// Сначала — кто вошёл: без этого после смены аккаунта (до `reset` от приложения) на карту попали бы файлы прежнего.
    private func bindCurrentOwner() async {
        guard let owner = await disk?.disk.owner() else { return }
        if disk?.bind(owner, capacity: Self.capacity) == true {
            resetMemory()
        }
    }

    private func restoreMissing(_ keys: Set<TileKey>) -> Set<TileKey> {
        guard let store = disk?.store else { return [] }
        let time = now()
        var restored: Set<TileKey> = []
        for key in keys where tiles[key] == nil {
            switch store.read(x: key.x, y: key.y) {
            case .missing:
                continue
            case .corrupted:
                corruptedFiles += 1
            case .ok(let file):
                guard let payload = DiskPayload.decode(file.payload) else {
                    store.delete(x: key.x, y: key.y)
                    corruptedFiles += 1
                    continue
                }
                // Загружен «в будущем» — часы телефона перевели назад: такой тайл считался бы свежим, пока часы его
                // не догонят, и угасание не перезапрашивалось бы целиком. Считать его старым. Файл прежнего вида
                // (без зон) — тоже: тогда он придёт целиком, с зонами.
                let loadedAt = Double(file.loadedAtMs) / 1_000
                tiles[key] = Tile(
                    key: key, version: file.version, parcels: payload.parcels,
                    contestedZones: payload.contestedZones ?? [],
                    loadedAt: loadedAt <= time && payload.contestedZones != nil ? loadedAt : 0,
                    checkedAt: 0)  // перепроверить один раз: подсказки, пока приложение было выгружено, потеряны
                restored.insert(key)
            }
        }
        return restored
    }

    private func persist(_ tile: Tile) {
        guard let store = disk?.store else { return }
        do {
            try store.write(
                TileFile(
                    x: tile.key.x, y: tile.key.y, version: tile.version,
                    loadedAtMs: TileFile.milliseconds(tile.loadedAt), savedAtMs: TileFile.milliseconds(now()),
                    cellCount: nil,
                    payload: try JSONEncoder().encode(
                        DiskPayload(parcels: tile.parcels, contestedZones: tile.contestedZones))))
        } catch {
            diskWriteFailures += 1
        }
    }

    /// Обновить тайлы, которые показывает карта. Запросы — по 25 тайлов.
    /// - Returns: тайлы, пришедшие с сервера с новыми данными или восстановленные с диска, — их нужно перерисовать.
    /// - Throws: ошибку сети или сервера; уже обновлённые до ошибки тайлы остаются в кэше.
    @discardableResult
    public func refresh(visible: Set<TileKey>) async throws -> Set<TileKey> {
        await bindCurrentOwner()
        let restored = restoreMissing(visible)
        let time = now()
        for key in visible {
            lastWanted[key] = time
        }
        let due = visible.filter { key in
            guard let tile = tiles[key] else { return true }
            return changed[key] != nil || time - tile.checkedAt >= Self.pollInterval
        }
        .sorted()
        var updated = restored
        for start in stride(from: 0, to: due.count, by: Self.maxTilesPerRequest) {
            let batch = Array(due[start..<min(start + Self.maxTilesPerRequest, due.count)])
            updated.formUnion(try await fetch(batch, at: time))
        }
        evict()  // файлы остаются: диск — второй уровень кэша, обрезается при привязке к игроку
        return updated
    }

    private func fetch(_ keys: [TileKey], at time: Double) async throws -> Set<TileKey> {
        let query = keys.map { key in
            // Версия — только у свежего тайла: у старого могло пройти угасание, которого версия не отражает.
            if let tile = tiles[key], time - tile.loadedAt < Self.maxAge {
                return "\(key.x):\(key.y)@\(tile.version)"
            }
            return "\(key.x):\(key.y)"
        }
        let requestedIn = generation
        let marks = keys.map { changed[$0] }
        let output = try await api.getTerritory(
            query: .init(league: league.rawValue, tiles: query.joined(separator: ",")))
        let response: Components.Schemas.TerritoryResponse
        switch output {
        case .ok(let ok):
            response = try ok.body.json
        case .badRequest:
            throw TerritoryCacheError.unexpectedStatus(400)
        case .undocumented(let status, _):
            throw TerritoryCacheError.unexpectedStatus(status)
        }

        guard generation == requestedIn else { return [] }  // пока шёл запрос, аккаунт сменился

        // Подсказка, пришедшая во время запроса, могла опоздать к ответу — такой тайл остаётся помеченным.
        for (key, mark) in zip(keys, marks) where changed[key] == mark {
            changed[key] = nil
        }
        var updated: Set<TileKey> = []
        for tile in response.tiles {
            let key = TileKey(x: Int(tile.x), y: Int(tile.y))
            // Ответ на более ранний запрос мог прийти позже: более новую версию он не затирает. Видимая версия у зрителя
            // только растёт; при равной — угасание новее у того, кто загружен позже.
            if let known = tiles[key],
                known.version > tile.version || (known.version == tile.version && known.loadedAt > time)
            {
                continue
            }
            let accepted = Tile(
                key: key, version: tile.version, parcels: tile.parcels, contestedZones: tile.contestedZones,
                loadedAt: time, checkedAt: time)
            tiles[key] = accepted
            persist(accepted)
            updated.insert(key)
        }
        for ref in response.unchanged {
            let key = TileKey(x: Int(ref.x), y: Int(ref.y))
            if let checked = tiles[key]?.checkedAt {
                tiles[key]?.checkedAt = max(checked, time)
            }
        }
        return updated
    }

    /// Лишние тайлы — те, что карта давно не просила.
    private func evict() {
        guard tiles.count > Self.capacity else { return }
        // При равном времени — по ключу: выбор не зависит от порядка словаря.
        let oldest = tiles.keys.sorted { ((lastWanted[$0] ?? 0), $0) < ((lastWanted[$1] ?? 0), $1) }
        for key in oldest.prefix(tiles.count - Self.capacity) {
            tiles[key] = nil
            lastWanted[key] = nil
            changed[key] = nil
        }
    }
}

/// Земля тайла в файле: куски и зоны «спорная». Файлы до зон хранили один список кусков — они читаются, а зон
/// в них нет (`nil`).
struct DiskPayload: Codable, Equatable {
    var parcels: [Components.Schemas.ParcelView]
    var contestedZones: [Components.Schemas.ContestedZoneView]?

    static func decode(_ data: Data) -> DiskPayload? {
        if let payload = try? JSONDecoder().decode(DiskPayload.self, from: data) {
            return payload
        }
        return (try? JSONDecoder().decode([Components.Schemas.ParcelView].self, from: data))
            .map { DiskPayload(parcels: $0, contestedZones: nil) }
    }
}

public enum TerritoryCacheError: Error, Equatable {
    /// Сервер ответил не так, как описано в контракте (например, 400 на неверный список тайлов).
    case unexpectedStatus(Int)
}
