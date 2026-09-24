import CZlib
import Foundation
import GorodkiAPI
import HTTPTypes
import OpenAPIRuntime
import Testing

@testable import Networking

#if canImport(FoundationNetworking)
    import FoundationNetworking
#endif

/// Сжать raw DEFLATE, как сервер (`DeflateStream`), — для подменного сервера.
func deflateRaw(_ bytes: [UInt8]) -> Data {
    var stream = z_stream()
    _ = deflateInit2_(
        &stream, Z_BEST_COMPRESSION, Z_DEFLATED, -15, 8, Z_DEFAULT_STRATEGY, ZLIB_VERSION,
        Int32(MemoryLayout<z_stream>.size))
    defer { deflateEnd(&stream) }
    var input = bytes
    var output = [UInt8](repeating: 0, count: bytes.count + 1_024)
    let written = input.withUnsafeMutableBufferPointer { source in
        output.withUnsafeMutableBufferPointer { target in
            stream.next_in = source.baseAddress
            stream.avail_in = uInt(source.count)
            stream.next_out = target.baseAddress
            stream.avail_out = uInt(target.count)
            _ = deflate(&stream, Z_FINISH)
            return Int(stream.total_out)
        }
    }
    return Data(output.prefix(written))
}

/// Тайл, в котором открыты первые `cells` клеток, — сжатый, как с сервера.
func compressedTile(cells: Int) -> Data {
    var bytes = [UInt8](repeating: 0, count: FogTileCodec.byteCount)
    for bit in 0..<cells {
        bytes[bit / 8] |= 1 << UInt8(bit % 8)
    }
    return deflateRaw(bytes)
}

@Suite("Туман: распаковка тайла")
struct FogTileCodecTests {
    @Test("Образец сервера (contracts/samples/fog.json) распаковывается: открытых клеток столько, сколько в cellCount")
    func serverSample() throws {
        let response = try JSONDecoder().decode(Components.Schemas.FogResponse.self, from: sample("fog"))
        let tile = try #require(response.tiles.first)

        let words = try FogTileCodec.words(fromCompressed: Data(tile.bits.data))

        #expect(words.count == FogTileCodec.wordCount)
        #expect(FogTileCodec.cellCount(words) == Int(tile.cellCount))
        #expect(tile.cellCount == 55)  // круг 25 м вокруг эталонной точки — как в contracts/fog.v1.json
    }

    @Test("Слова little-endian: бит N — клетка N, как FogTileBits в GameCore")
    func bitOrder() throws {
        var bytes = [UInt8](repeating: 0, count: FogTileCodec.byteCount)
        bytes[0] = 0b0000_0001  // клетка 0
        bytes[9] = 0b0000_0010  // клетка 64 + 8 + 1 = 73
        let words = try FogTileCodec.words(fromCompressed: deflateRaw(bytes))

        #expect(words[0] == 1)
        #expect(words[1] == 1 << 9)
        #expect(FogTileCodec.cellCount(words) == 2)
    }

    @Test("Повреждённые данные и не 8 КБ после распаковки — ошибка", arguments: [0, 100, 8_191, 8_193])
    func corrupted(length: Int) {
        let data = length == 0 ? Data([1, 2, 3, 4]) : deflateRaw([UInt8](repeating: 7, count: length))
        #expect(throws: FogTileCodec.Failure.corrupted) {
            try FogTileCodec.words(fromCompressed: data)
        }
    }

    private func sample(_ name: String) throws -> Data {
        var directory = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
        for _ in 0..<10 {
            let candidate = directory.appendingPathComponent("contracts/samples/\(name).json")
            if FileManager.default.fileExists(atPath: candidate.path) {
                return try Data(contentsOf: candidate)
            }
            directory.deleteLastPathComponent()
        }
        throw CocoaError(.fileNoSuchFile)
    }
}

@Suite("Туман на телефоне: тайлы с версиями")
struct FogCacheTests {
    /// Сервер тумана без сети, как `GET /fog`: тайл → версия (открыто клеток = версия × 10). Тайла без открытых клеток
    /// в базе нет: спрошенный с `@0` он «не изменился», без версии — не упоминается вовсе, а спрошенный с версией
    /// больше 0 (стёрт очисткой истории) приходит пустым с версией на 1 больше.
    actor Server: ClientTransport {
        private var versions: [FogTileRef: Int64] = [:]
        private(set) var requests: [(tiles: [String], layer: String?, season: String?)] = []
        private var corrupt: Set<FogTileRef> = []
        private var wrongCount: Set<FogTileRef> = []

        func set(_ key: FogTileRef, version: Int64) { versions[key] = version }
        /// «Очистить историю исследований»: строки тайла больше нет.
        func clear(_ key: FogTileRef) { versions[key] = nil }
        func corrupt(_ key: FogTileRef) { corrupt.insert(key) }
        func heal(_ key: FogTileRef) { corrupt.remove(key) }
        func lieAboutCount(_ key: FogTileRef) { wrongCount.insert(key) }
        /// Следующий ответ — готов, но придержан до `release()`: как медленная сеть.
        private var held: CheckedContinuation<Void, Never>?
        private var holding = false
        private var heldWaiters: [CheckedContinuation<Void, Never>] = []
        func hold() { holding = true }
        func release() {
            held?.resume()
            held = nil
        }
        /// Дождаться, пока ответ соберётся и будет придержан, — без сна: иначе на медленной машине проверка шла бы дальше
        /// раньше, чем ответ собран.
        func waitUntilHeld() async {
            if held != nil { return }
            await withCheckedContinuation { heldWaiters.append($0) }
        }

        func send(
            _ request: HTTPRequest, body: HTTPBody?, baseURL: URL, operationID: String
        ) async throws -> (HTTPResponse, HTTPBody?) {
            let items = URLComponents(string: request.path ?? "")?.queryItems ?? []
            let asked = items.first { $0.name == "tiles" }?.value?.split(separator: ",").map(String.init) ?? []
            requests.append(
                (asked, items.first { $0.name == "layer" }?.value, items.first { $0.name == "season" }?.value))
            var tiles: [String] = []
            var unchanged: [String] = []
            for item in asked {
                let parts = item.split(separator: "@")
                let xy = parts[0].split(separator: ":").compactMap { Int($0) }
                let key = FogTileRef(x: xy[0], y: xy[1])
                let known = parts.count == 2 ? Int64(parts[1]) : nil
                if (versions[key] ?? 0) == known {
                    unchanged.append(#"{"x":\#(key.x),"y":\#(key.y)}"#)
                } else if let version = versions[key] {
                    let bits =
                        corrupt.contains(key)
                        ? Data([9, 9, 9]).base64EncodedString()
                        : compressedTile(cells: Int(version) * 10).base64EncodedString()
                    let count = version * 10 + (wrongCount.contains(key) ? 1 : 0)
                    tiles.append(
                        #"{"x":\#(key.x),"y":\#(key.y),"version":\#(version),"cellCount":\#(count),"bits":"\#(bits)"}"#
                    )
                } else if let known, known > 0 {
                    let bits = compressedTile(cells: 0).base64EncodedString()
                    tiles.append(
                        #"{"x":\#(key.x),"y":\#(key.y),"version":\#(known + 1),"cellCount":0,"bits":"\#(bits)"}"#)
                }
            }
            if holding {
                holding = false
                await withCheckedContinuation { continuation in
                    held = continuation
                    heldWaiters.forEach { $0.resume() }
                    heldWaiters = []
                }
            }
            let json =
                #"{"layer":"foot","season":null,"tiles":[\#(tiles.joined(separator: ","))],"unchanged":[\#(unchanged.joined(separator: ","))]}"#
            var fields = HTTPFields()
            fields[.contentType] = "application/json"
            return (HTTPResponse(status: .ok, headerFields: fields), HTTPBody(Data(json.utf8)))
        }
    }

    private let server = Server()

    private func cache(season: Int? = nil) -> FogCache {
        let api = Client(
            serverURL: URL(string: "https://api.example")!, configuration: GorodkiAPI.configuration, transport: server)
        return FogCache(api: api, layer: .foot, season: season)
    }

    private static let a = FogTileRef(x: 9270, y: 5404)
    private static let b = FogTileRef(x: 9271, y: 5404)
    private static let near: Set<FogTileRef> = [a, b]

    /// Оба тайла уже открыты: у `a` версия 3, у `b` — 1.
    private func seed() async {
        await server.set(Self.a, version: 3)
        await server.set(Self.b, version: 1)
    }

    @Test("Первый раз — тайлы целиком, биты распакованы; сразу снова — без запросов: туман сам не меняется")
    func firstLoadThenNothing() async throws {
        await seed()
        let cache = cache()

        #expect(try await cache.refresh(visible: Self.near) == Self.near)
        let tile = try #require(await cache.tile(Self.a))
        #expect(tile.version == 3 && tile.cellCount == 30 && FogTileCodec.cellCount(tile.words) == 30)
        #expect(try await cache.refresh(visible: Self.near).isEmpty)
        #expect(await server.requests.count == 1)
        #expect(await server.requests.first?.tiles == ["9270:5404@0", "9271:5404@0"])
        #expect(await server.requests.first?.layer == "foot")
        #expect(await server.requests.first?.season == nil)
    }

    @Test("Забег доставлен (invalidate) — перезапрос с версиями; изменился только один тайл")
    func invalidateRefetchesWithVersions() async throws {
        await seed()
        let cache = cache()
        try await cache.refresh(visible: Self.near)
        await server.set(Self.b, version: 2)

        await cache.invalidate()
        let updated = try await cache.refresh(visible: Self.near)

        #expect(updated == [Self.b])
        #expect(await server.requests.last?.tiles == ["9270:5404@3", "9271:5404@1"])
        #expect(await cache.tile(Self.b)?.cellCount == 20)
        #expect(try await cache.refresh(visible: Self.near).isEmpty)
    }

    @Test("Тайл, где ничего не открыто, кэшируется пустым: второй раз не запрашивается; открылся — приходит")
    func emptyTileIsCached() async throws {
        await server.set(Self.a, version: 3)  // `b` пуст: строки в базе нет
        let cache = cache()

        #expect(try await cache.refresh(visible: Self.near) == [Self.a])
        let empty = try #require(await cache.tile(Self.b))
        #expect(empty.version == 0 && empty.cellCount == 0 && empty.words.isEmpty)
        #expect(try await cache.refresh(visible: Self.near).isEmpty)
        #expect(await server.requests.count == 1)

        await server.set(Self.b, version: 1)
        await cache.invalidate()
        #expect(try await cache.refresh(visible: Self.near) == [Self.b])
        #expect(await server.requests.last?.tiles == ["9270:5404@3", "9271:5404@0"])
        #expect(await cache.tile(Self.b)?.cellCount == 10)
    }

    @Test("Сезонный слой — номер сезона в запросе")
    func seasonIsSent() async throws {
        await seed()
        try await cache(season: 2).refresh(visible: Self.near)
        #expect(await server.requests.last?.season == "2")
    }

    @Test("Повреждённый тайл пропускается и считается, остальные приходят")
    func corruptedTileIsSkipped() async throws {
        await seed()
        await server.corrupt(Self.a)
        let cache = cache()

        let updated = try await cache.refresh(visible: Self.near)

        #expect(updated == [Self.b])
        #expect(await cache.corruptedTiles == 1)
        #expect(await cache.tile(Self.a) == nil)
    }

    @Test("Повреждённый при перезапросе — прежний тайл остаётся и перезапрашивается снова, пока не придёт целым")
    func corruptedRefetchStaysStale() async throws {
        await seed()
        let cache = cache()
        try await cache.refresh(visible: Self.near)
        await server.set(Self.a, version: 4)
        await server.corrupt(Self.a)

        await cache.invalidate()
        #expect(try await cache.refresh(visible: Self.near).isEmpty)
        #expect(await cache.tile(Self.a)?.version == 3)

        await server.heal(Self.a)
        #expect(try await cache.refresh(visible: Self.near) == [Self.a])
        #expect(await server.requests.last?.tiles == ["9270:5404@3"])
        #expect(await cache.tile(Self.a)?.version == 4)
    }

    @Test("Число клеток не сходится с битами — тайл считается повреждённым")
    func cellCountMismatchIsCorrupted() async throws {
        await seed()
        await server.lieAboutCount(Self.a)
        let cache = cache()

        #expect(try await cache.refresh(visible: Self.near) == [Self.b])
        #expect(await cache.corruptedTiles == 1)
        #expect(await cache.tile(Self.a) == nil)
    }

    @Test("Смена аккаунта — кэш пуст")
    func resetForgets() async throws {
        await seed()
        let cache = cache()
        try await cache.refresh(visible: Self.near)
        await cache.reset()
        #expect(await cache.count == 0)
        try await cache.refresh(visible: Self.near)
        #expect(await server.requests.last?.tiles == ["9270:5404@0", "9271:5404@0"])
    }

    @Test("Ответ на ранний запрос пришёл позже нового — новая версия не затирается старой")
    func olderResponseDoesNotOverwrite() async throws {
        await seed()
        let cache = cache()
        await server.hold()

        let slow = Task { try await cache.refresh(visible: [Self.a]) }  // соберёт версию 3
        await server.waitUntilHeld()
        await server.set(Self.a, version: 4)
        #expect(try await cache.refresh(visible: [Self.a]) == [Self.a])  // тайла ещё нет — второй запрос
        await server.release()

        #expect(try await slow.value.isEmpty)
        #expect(await cache.tile(Self.a)?.version == 4)
    }

    @Test("Туман изменился, пока тайл запрашивался впервые, — после ответа тайл перезапрашивается с версией")
    func invalidateDuringFirstRequestIsKept() async throws {
        await seed()
        let cache = cache()
        await server.hold()

        let loading = Task { try await cache.refresh(visible: [Self.a]) }
        await server.waitUntilHeld()  // ответ с версией 3 уже собран
        await server.set(Self.a, version: 4)  // сервер открыл туман по доставленному забегу
        await cache.invalidate()  // подсказка `FogChanged` пришла раньше ответа
        await server.release()
        #expect(try await loading.value == [Self.a])
        #expect(await cache.tile(Self.a)?.version == 3)

        #expect(try await cache.refresh(visible: [Self.a]) == [Self.a])
        #expect(await server.requests.last?.tiles == ["9270:5404@3"])
        #expect(await cache.tile(Self.a)?.version == 4)
    }

    @Test("Туман изменился, пока устаревший тайл перезапрашивался, — ответ «без изменений» пометку не снимает")
    func invalidateDuringRefetchIsKept() async throws {
        await seed()
        let cache = cache()
        try await cache.refresh(visible: [Self.a])
        await cache.invalidate()
        await server.hold()

        let loading = Task { try await cache.refresh(visible: [Self.a]) }
        await server.waitUntilHeld()  // «без изменений» уже собрано
        await server.set(Self.a, version: 4)
        await cache.invalidate()
        await server.release()
        #expect(try await loading.value.isEmpty)

        #expect(try await cache.refresh(visible: [Self.a]) == [Self.a])
        #expect(await cache.tile(Self.a)?.version == 4)
        #expect(try await cache.refresh(visible: [Self.a]).isEmpty)  // дальше — снова без запросов
    }

    @Test("История очищена — пустой тайл заменяет стёртый и дальше приходит «без изменений»; открытый заново приходит")
    func clearedHistoryReplacesCachedTiles() async throws {
        await seed()
        let cache = cache()
        try await cache.refresh(visible: Self.near)
        await server.clear(Self.a)

        await cache.invalidate()  // подсказка `FogChanged` после очистки
        #expect(try await cache.refresh(visible: Self.near) == [Self.a])
        let empty = try #require(await cache.tile(Self.a))
        #expect(empty.version == 0 && empty.cellCount == 0 && empty.words.isEmpty)  // как любой пустой
        #expect(await cache.tile(Self.b)?.cellCount == 10)  // другой тайл не тронут
        #expect(await cache.corruptedTiles == 0)

        // Следующая подсказка или переподключение: стёртый тайл спрашивается с версией 0 и не перерисовывается снова.
        await cache.invalidate()
        #expect(try await cache.refresh(visible: Self.near).isEmpty)
        #expect(await server.requests.last?.tiles == ["9270:5404@0", "9271:5404@1"])

        // Новый забег открыл его снова — приходит, даже с версией меньше стёртой (часы сервера пошли назад).
        await server.set(Self.a, version: 2)
        await cache.invalidate()
        #expect(try await cache.refresh(visible: Self.near) == [Self.a])
        #expect(await cache.tile(Self.a)?.cellCount == 20)
    }

    @Test("Ответ, начатый до смены аккаунта, выбрасывается: чужой туман в кэш не попадает")
    func responseAfterResetIsDropped() async throws {
        await seed()
        let cache = cache()
        await server.hold()

        let loading = Task { try await cache.refresh(visible: Self.near) }
        await server.waitUntilHeld()
        await cache.reset()
        await server.release()

        #expect(try await loading.value.isEmpty)
        #expect(await cache.count == 0)
    }
}
