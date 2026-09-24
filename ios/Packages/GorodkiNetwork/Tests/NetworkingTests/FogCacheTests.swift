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
    /// Сервер тумана без сети: тайл → версия (открыто клеток = версия × 10).
    actor Server: ClientTransport {
        private var versions: [FogTileRef: Int64] = [:]
        private(set) var requests: [(tiles: [String], layer: String?, season: String?)] = []
        private var corrupt: Set<FogTileRef> = []

        func set(_ key: FogTileRef, version: Int64) { versions[key] = version }
        func corrupt(_ key: FogTileRef) { corrupt.insert(key) }

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
                let version = versions[key] ?? 1
                if parts.count == 2, Int64(parts[1]) == version {
                    unchanged.append(#"{"x":\#(key.x),"y":\#(key.y)}"#)
                } else {
                    let bits =
                        corrupt.contains(key)
                        ? Data([9, 9, 9]).base64EncodedString()
                        : compressedTile(cells: Int(version) * 10).base64EncodedString()
                    tiles.append(
                        #"{"x":\#(key.x),"y":\#(key.y),"version":\#(version),"cellCount":\#(version * 10),"bits":"\#(bits)"}"#
                    )
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

    private static let near: Set<FogTileRef> = [FogTileRef(x: 9270, y: 5404), FogTileRef(x: 9271, y: 5404)]

    @Test("Первый раз — тайлы целиком, биты распакованы; сразу снова — без запросов: туман сам не меняется")
    func firstLoadThenNothing() async throws {
        await server.set(FogTileRef(x: 9270, y: 5404), version: 3)
        let cache = cache()

        #expect(try await cache.refresh(visible: Self.near) == Self.near)
        let tile = try #require(await cache.tile(FogTileRef(x: 9270, y: 5404)))
        #expect(tile.version == 3 && tile.cellCount == 30 && FogTileCodec.cellCount(tile.words) == 30)
        #expect(try await cache.refresh(visible: Self.near).isEmpty)
        #expect(await server.requests.count == 1)
        #expect(await server.requests.first?.layer == "foot")
        #expect(await server.requests.first?.season == nil)
    }

    @Test("Забег доставлен (invalidate) — перезапрос с версиями; изменился только один тайл")
    func invalidateRefetchesWithVersions() async throws {
        let cache = cache()
        try await cache.refresh(visible: Self.near)
        await server.set(FogTileRef(x: 9271, y: 5404), version: 2)

        await cache.invalidate()
        let updated = try await cache.refresh(visible: Self.near)

        #expect(updated == [FogTileRef(x: 9271, y: 5404)])
        #expect(await server.requests.last?.tiles == ["9270:5404@1", "9271:5404@1"])
        #expect(await cache.tile(FogTileRef(x: 9271, y: 5404))?.cellCount == 20)
        #expect(try await cache.refresh(visible: Self.near).isEmpty)
    }

    @Test("Сезонный слой — номер сезона в запросе")
    func seasonIsSent() async throws {
        try await cache(season: 2).refresh(visible: Self.near)
        #expect(await server.requests.last?.season == "2")
    }

    @Test("Повреждённый тайл пропускается и считается, остальные приходят")
    func corruptedTileIsSkipped() async throws {
        await server.corrupt(FogTileRef(x: 9270, y: 5404))
        let cache = cache()

        let updated = try await cache.refresh(visible: Self.near)

        #expect(updated == [FogTileRef(x: 9271, y: 5404)])
        #expect(await cache.corruptedTiles == 1)
        #expect(await cache.tile(FogTileRef(x: 9270, y: 5404)) == nil)
    }

    @Test("Смена аккаунта — кэш пуст")
    func resetForgets() async throws {
        let cache = cache()
        try await cache.refresh(visible: Self.near)
        await cache.reset()
        #expect(await cache.count == 0)
        try await cache.refresh(visible: Self.near)
        #expect(await server.requests.last?.tiles == ["9270:5404", "9271:5404"])
    }
}
