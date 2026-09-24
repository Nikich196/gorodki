import Foundation
import GorodkiAPI
import HTTPTypes
import OpenAPIRuntime
import Synchronization
import Testing

@testable import Networking

#if canImport(FoundationNetworking)
    import FoundationNetworking
#endif

@Suite("Земля на телефоне: тайлы с версиями")
struct TerritoryCacheTests {
    /// Сервер карты без сети: тайл → версия; `x:y@v` с той же версией — «без изменений», иначе тайл целиком.
    actor Server: ClientTransport {
        private var versions: [TileKey: Int64] = [:]
        private(set) var requests: [[String]] = []
        private var failWith: Int?
        private var holdNext = false
        private var held: CheckedContinuation<Void, Never>?

        func set(_ key: TileKey, version: Int64) { versions[key] = version }
        func fail(status: Int) { failWith = status }
        /// Следующий ответ — готов, но придержан до `release()`: как медленная сеть.
        func holdNextResponse() { holdNext = true }
        func release() {
            held?.resume()
            held = nil
        }
        func waitUntilHeld() async throws {
            for _ in 0..<2_500 where held == nil {
                try await Task.sleep(for: .milliseconds(2))
            }
        }

        func send(
            _ request: HTTPRequest, body: HTTPBody?, baseURL: URL, operationID: String
        ) async throws -> (HTTPResponse, HTTPBody?) {
            let items = URLComponents(string: request.path ?? "")?.queryItems ?? []
            let asked = items.first { $0.name == "tiles" }?.value?.split(separator: ",").map(String.init) ?? []
            requests.append(asked)
            if let failWith {
                return (HTTPResponse(status: .init(code: failWith)), nil)
            }
            var tiles: [String] = []
            var unchanged: [String] = []
            for item in asked {
                let parts = item.split(separator: "@")
                let xy = parts[0].split(separator: ":").compactMap { Int($0) }
                let key = TileKey(x: xy[0], y: xy[1])
                let version = versions[key] ?? 0
                if parts.count == 2, Int64(parts[1]) == version {
                    unchanged.append(#"{"x":\#(key.x),"y":\#(key.y)}"#)
                } else {
                    tiles.append(
                        #"{"x":\#(key.x),"y":\#(key.y),"version":\#(version),"parcels":[\#(Self.parcel(version))]}"#)
                }
            }
            if holdNext {
                holdNext = false
                await withCheckedContinuation { held = $0 }
            }
            let json =
                #"{"league":"run","tiles":[\#(tiles.joined(separator: ","))],"unchanged":[\#(unchanged.joined(separator: ","))]}"#
            var fields = HTTPFields()
            fields[.contentType] = "application/json"
            return (HTTPResponse(status: .ok, headerFields: fields), HTTPBody(Data(json.utf8)))
        }

        /// Кусок, по которому видно, какой версии тайл: уровень = версия (до 3).
        static func parcel(_ version: Int64) -> String {
            #"{"id":\#(version + 1),"ownerId":"0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b","colorIndex":3,"level":\#(min(max(version, 1), 3)),"ghost":false,"lastVisitAtMs":1790000000000,"exterior":[52.1,23.7,52.1,23.71,52.11,23.71,52.1,23.7],"holes":[]}"#
        }
    }

    final class Clock: Sendable {
        private let seconds = Mutex(1_790_000_000.0)
        var now: Double { seconds.withLock { $0 } }
        func advance(_ by: Double) { seconds.withLock { $0 += by } }
    }

    private let server = Server()
    private let clock = Clock()

    private func cache() -> TerritoryCache {
        let clock = self.clock
        let api = Client(
            serverURL: URL(string: "https://api.example")!, configuration: GorodkiAPI.configuration, transport: server)
        return TerritoryCache(api: api, league: .run, now: { clock.now })
    }

    private static func keys(_ range: Range<Int>, y: Int = 5775) -> Set<TileKey> {
        Set(range.map { TileKey(x: $0, y: y) })
    }

    @Test("Первый раз — тайлы целиком, без версий; сразу снова — без запросов")
    func firstLoadThenNothing() async throws {
        await server.set(TileKey(x: 684, y: 5775), version: 3)
        let cache = cache()

        let updated = try await cache.refresh(visible: Self.keys(684..<686))
        #expect(updated == Self.keys(684..<686))
        #expect(await server.requests == [["684:5775", "685:5775"]])
        #expect(await cache.tile(TileKey(x: 684, y: 5775))?.version == 3)
        #expect(await cache.tile(TileKey(x: 685, y: 5775))?.version == 0)

        #expect(try await cache.refresh(visible: Self.keys(684..<686)).isEmpty)
        #expect(await server.requests.count == 1)
    }

    @Test("Подсказка TilesChanged — перезапрос только этого тайла с версией; не изменился — данные прежние")
    func hintRefetchesWithVersion() async throws {
        let cache = cache()
        try await cache.refresh(visible: Self.keys(684..<686))
        let key = TileKey(x: 684, y: 5775)

        await cache.markChanged([key])
        #expect(try await cache.refresh(visible: Self.keys(684..<686)).isEmpty)  // версия та же — «без изменений»
        await server.set(key, version: 2)
        await cache.markChanged([key])
        #expect(try await cache.refresh(visible: Self.keys(684..<686)) == [key])

        #expect(await server.requests.suffix(2) == [["684:5775@0"], ["684:5775@0"]])
        #expect(await cache.tile(key)?.version == 2)
        #expect(await cache.tile(key)?.parcels.first?.level == 2)
    }

    @Test("Подсказка про тайл, которого карта не показывает, запроса не вызывает — до его показа")
    func hintForInvisibleTileWaits() async throws {
        let cache = cache()
        try await cache.refresh(visible: Self.keys(684..<686))
        let away = TileKey(x: 700, y: 5775)

        await cache.markChanged([away])
        try await cache.refresh(visible: Self.keys(684..<686))
        #expect(await server.requests.count == 1)

        try await cache.refresh(visible: [away])
        #expect(await server.requests.last == ["700:5775"])
    }

    @Test("Запасной опрос: через 5 минут видимые тайлы перезапрашиваются с версиями")
    func pollsEveryFiveMinutes() async throws {
        let cache = cache()
        try await cache.refresh(visible: Self.keys(684..<686))

        clock.advance(TerritoryCache.pollInterval - 1)
        try await cache.refresh(visible: Self.keys(684..<686))
        #expect(await server.requests.count == 1)

        clock.advance(1)
        try await cache.refresh(visible: Self.keys(684..<686))
        #expect(await server.requests.last == ["684:5775@0", "685:5775@0"])

        // «Без изменений» тоже продлевает: следующий опрос — снова через 5 минут.
        clock.advance(TerritoryCache.pollInterval - 1)
        try await cache.refresh(visible: Self.keys(684..<686))
        #expect(await server.requests.count == 2)
    }

    @Test("Старше часа — целиком, без версии: угасание версию не меняет")
    func oldTilesReloadWithoutVersion() async throws {
        let cache = cache()
        try await cache.refresh(visible: Self.keys(684..<685))

        for _ in 0..<12 {  // 12 опросов по 5 минут — «без изменений»
            clock.advance(TerritoryCache.pollInterval)
            try await cache.refresh(visible: Self.keys(684..<685))
        }

        #expect(await server.requests.last == ["684:5775"])
        #expect(await server.requests.dropFirst().dropLast().allSatisfy { $0 == ["684:5775@0"] })
    }

    @Test("60 тайлов — три запроса: 25, 25 и 10")
    func batchesOfTwentyFive() async throws {
        let cache = cache()

        try await cache.refresh(visible: Self.keys(600..<660))

        #expect(await server.requests.map(\.count) == [25, 25, 10])
        #expect(await cache.count == 60)
    }

    @Test("Смена аккаунта — кэш пуст, версии прежнего зрителя не шлются")
    func resetForgetsVersions() async throws {
        await server.set(TileKey(x: 684, y: 5775), version: 5)
        let cache = cache()
        try await cache.refresh(visible: Self.keys(684..<685))

        await cache.reset()
        #expect(await cache.count == 0)
        try await cache.refresh(visible: Self.keys(684..<685))

        #expect(await server.requests.last == ["684:5775"])
    }

    @Test("Больше 400 тайлов — выбрасываются те, что карта дольше всего не просила")
    func evictsLeastRecentlyWanted() async throws {
        let cache = cache()
        try await cache.refresh(visible: Self.keys(0..<300))
        clock.advance(1)
        try await cache.refresh(visible: Self.keys(300..<500))

        #expect(await cache.count == TerritoryCache.capacity)
        #expect(await cache.tile(TileKey(x: 0, y: 5775)) == nil)
        #expect(await cache.tile(TileKey(x: 99, y: 5775)) == nil)
        #expect(await cache.tile(TileKey(x: 100, y: 5775)) != nil)
        #expect(await cache.tile(TileKey(x: 499, y: 5775)) != nil)
    }

    @Test("Подсказка пришла, пока тайл запрашивался, — тайл остаётся помеченным и перезапрашивается снова")
    func hintDuringRequestIsKept() async throws {
        let key = TileKey(x: 9270, y: 5775)
        await server.set(key, version: 1)
        let cache = cache()
        try await cache.refresh(visible: [key])
        await server.set(key, version: 2)
        await cache.markChanged([key])
        await server.holdNextResponse()

        let loading = Task { try await cache.refresh(visible: [key]) }
        try await server.waitUntilHeld()  // ответ с версией 2 уже собран
        await server.set(key, version: 3)
        await cache.markChanged([key])
        await server.release()
        #expect(try await loading.value == [key])
        #expect(await cache.tile(key)?.version == 2)

        #expect(try await cache.refresh(visible: [key]) == [key])
        #expect(await cache.tile(key)?.version == 3)
    }

    @Test("Ответ на ранний запрос пришёл позже нового — новая версия не затирается старой")
    func olderResponseDoesNotOverwrite() async throws {
        let key = TileKey(x: 9270, y: 5775)
        await server.set(key, version: 1)
        let cache = cache()
        await server.holdNextResponse()

        let slow = Task { try await cache.refresh(visible: [key]) }
        try await server.waitUntilHeld()
        await server.set(key, version: 2)
        #expect(try await cache.refresh(visible: [key]) == [key])  // тайла ещё нет — второй запрос
        await server.release()

        #expect(try await slow.value.isEmpty)
        #expect(await cache.tile(key)?.version == 2)
    }

    @Test("Ответ, начатый до смены аккаунта, выбрасывается: версии прежнего зрителя в кэш не попадают")
    func responseAfterResetIsDropped() async throws {
        let key = TileKey(x: 9270, y: 5775)
        await server.set(key, version: 4)
        let cache = cache()
        await server.holdNextResponse()

        let loading = Task { try await cache.refresh(visible: [key]) }
        try await server.waitUntilHeld()
        await cache.reset()
        await server.release()

        #expect(try await loading.value.isEmpty)
        #expect(await cache.count == 0)
    }

    @Test("Ошибка сервера — исключение; тайлы, пришедшие до неё, остаются")
    func serverErrorKeepsEarlierBatches() async throws {
        let cache = cache()
        try await cache.refresh(visible: Self.keys(0..<25))
        await server.fail(status: 503)

        await #expect(throws: TerritoryCacheError.unexpectedStatus(503)) {
            try await cache.refresh(visible: Self.keys(25..<30))
        }
        #expect(await cache.count == 25)
    }
}
