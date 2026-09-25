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

/// Папка кэша на одну проверку и «кто вошёл», которого проверка меняет.
final class DiskFixture: Sendable {
    let location = TileCacheLocation(
        root: FileManager.default.temporaryDirectory.appendingPathComponent("tiles-\(UUID().uuidString)"))
    private let state = Mutex<SessionOwner>(.signedIn(playerId: "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b"))

    var disk: TileCacheDisk {
        TileCacheDisk(location: location, owner: { self.state.withLock { $0 } })
    }

    func set(_ owner: SessionOwner) { state.withLock { $0 = owner } }

    /// Все файлы тайлов под корнем — путь от корня.
    func files() -> [String] {
        let root = location.root.standardizedFileURL.path
        let all = FileManager.default.enumerator(atPath: root)?.allObjects as? [String] ?? []
        return all.filter { $0.hasSuffix(".tile") }.sorted()
    }

    func exists(_ relative: String) -> Bool {
        FileManager.default.fileExists(atPath: location.root.appendingPathComponent(relative).path)
    }

    deinit { try? FileManager.default.removeItem(at: location.root) }
}

@Suite("Тайлы на диске: файл, проверка, обрезка")
struct TileDiskStoreTests {
    private let fixture = DiskFixture()
    private var store: TileDiskStore { fixture.location.store(owner: "p1", cache: "territory-run") }

    private func file(x: Int = 684, y: Int = 5775, version: Int64 = 3, savedAtMs: Int64 = 1_000) -> TileFile {
        TileFile(
            x: x, y: y, version: version, loadedAtMs: 900, savedAtMs: savedAtMs, cellCount: nil,
            payload: Data("[]".utf8))
    }

    @Test("Записанный тайл читается тем же")
    func roundTrip() throws {
        try store.write(file())
        #expect(store.read(x: 684, y: 5775) == .ok(file()))
        #expect(store.read(x: 685, y: 5775) == .missing)
    }

    enum Damage: String, CaseIterable, CustomTestStringConvertible {
        case truncated, payloadByte, versionDigit, garbage, empty, renamed
        var testDescription: String { rawValue }
    }

    @Test("Испорченный файл отбрасывается и стирается", arguments: Damage.allCases)
    func corrupted(_ damage: Damage) throws {
        try store.write(file())
        let url = store.url(x: 684, y: 5775)
        let text = try String(contentsOf: url, encoding: .utf8)
        switch damage {
        case .truncated:
            try Data(text.utf8.prefix(text.utf8.count / 2)).write(to: url)
        case .payloadByte:
            // "[]" → "[}" в base64: полезная нагрузка другая, сумма — прежняя.
            let other = Data("[}".utf8).base64EncodedString()
            try text.replacingOccurrences(of: Data("[]".utf8).base64EncodedString(), with: other)
                .write(to: url, atomically: true, encoding: .utf8)
        case .versionDigit:
            try text.replacingOccurrences(of: #""version":3"#, with: #""version":7"#)
                .write(to: url, atomically: true, encoding: .utf8)
        case .garbage:
            try Data([0xFF, 0x00, 0x13]).write(to: url)
        case .empty:
            try Data().write(to: url)
        case .renamed:
            try FileManager.default.moveItem(at: url, to: store.url(x: 685, y: 5775))
        }
        let (x, y) = damage == .renamed ? (685, 5775) : (684, 5775)
        #expect(store.read(x: x, y: y) == .corrupted)
        #expect(!FileManager.default.fileExists(atPath: store.url(x: x, y: y).path))
        #expect(store.read(x: x, y: y) == .missing)  // второй раз не считается
    }

    @Test("Замороженный файл формата 1 читается и после обновлений")
    func frozenFormat() throws {
        let frozen =
            #"{"format":1,"x":684,"y":5775,"version":3,"loadedAtMs":900,"savedAtMs":1000,"payload":"W10=","crc32":\#(file().crc32)}"#
        try FileManager.default.createDirectory(at: store.directory, withIntermediateDirectories: true)
        try Data(frozen.utf8).write(to: store.url(x: 684, y: 5775))
        #expect(store.read(x: 684, y: 5775) == .ok(file()))
    }

    @Test("Обрезка — сначала давно записанные; временные файлы стираются")
    func trimOldestFirst() throws {
        try store.write(file(x: 1, savedAtMs: 300))
        try store.write(file(x: 2, savedAtMs: 100))
        try store.write(file(x: 3, savedAtMs: 200))
        try Data("x".utf8).write(to: store.directory.appendingPathComponent(".1_1.tile.tmp-crash"))
        #expect(store.trim(capacity: 2) == 1)
        #expect(store.tileNames().sorted() == ["1_5775.tile", "3_5775.tile"])
        #expect(try FileManager.default.contentsOfDirectory(atPath: store.directory.path).count == 2)
    }

    @Test("Остаются только тайлы вошедшего; папка прежнего формата стирается")
    func removeOwners() throws {
        try store.write(file())
        try fixture.location.store(owner: "p2", cache: "territory-run").write(file())
        let old = fixture.location.root.appendingPathComponent("v0/p1")
        try FileManager.default.createDirectory(at: old, withIntermediateDirectories: true)
        fixture.location.removeOwners(except: "p2")
        #expect(fixture.files() == ["v1/p2/territory-run/684_5775.tile"])
        #expect(!fixture.exists("v0"))
    }

    @Test("Имя папки игрока — только безопасные символы")
    func ownerKey() {
        #expect(TileCacheLocation.ownerKey("0199a1b2-c3d4") == "0199a1b2-c3d4")
        #expect(TileCacheLocation.ownerKey("../x") == "h-2e2e2f78")
    }
}

@Suite("Земля переживает перезапуск")
struct TerritoryCacheDiskTests {
    private let fixture = DiskFixture()
    private let clock = TerritoryCacheTests.Clock()
    private static let a = TileKey(x: 684, y: 5775)
    private static let b = TileKey(x: 685, y: 5775)

    /// Новый экземпляр кэша над той же папкой — как после перезапуска приложения.
    private func cache(_ server: TerritoryCacheTests.Server) -> TerritoryCache {
        let clock = self.clock
        let api = Client(
            serverURL: URL(string: "https://api.example")!, configuration: GorodkiAPI.configuration, transport: server)
        return TerritoryCache(api: api, league: .run, disk: fixture.disk, now: { clock.now })
    }

    private func seeded() async throws -> TerritoryCacheTests.Server {
        let server = TerritoryCacheTests.Server()
        await server.set(Self.a, version: 3)
        try await cache(server).refresh(visible: [Self.a, Self.b])
        return server
    }

    @Test("После перезапуска без сети — те же тайлы; первый запрос — с версией с диска")
    func survivesRestart() async throws {
        _ = try await seeded()
        let offline = TerritoryCacheTests.Server()
        await offline.fail(status: 503)
        let restarted = cache(offline)
        #expect(await restarted.restore(visible: [Self.a, Self.b]) == [Self.a, Self.b])
        #expect(await restarted.tile(Self.a)?.version == 3)
        #expect(await restarted.tile(Self.a)?.parcels.first?.level == 3)

        let online = TerritoryCacheTests.Server()
        await online.set(Self.a, version: 3)
        let again = cache(online)
        try await again.refresh(visible: [Self.a, Self.b])
        #expect(await online.requests == [["684:5775@3", "685:5775@0"]])
    }

    @Test("Зоны «спорная» переживают перезапуск вместе с кусками")
    func zonesSurviveRestart() async throws {
        let server = TerritoryCacheTests.Server()
        await server.set(Self.a, version: 3)
        await server.setZone(Self.a, untilMs: 1_790_086_800_000)
        try await cache(server).refresh(visible: [Self.a])
        let offline = TerritoryCacheTests.Server()
        await offline.fail(status: 503)
        let restarted = cache(offline)
        #expect(await restarted.restore(visible: [Self.a]) == [Self.a])
        #expect(await restarted.tile(Self.a)?.contestedZones.map(\.untilMs) == [1_790_086_800_000])
    }

    @Test("Файл прежнего вида (только куски) читается без зон, а тайл запрашивается целиком — чтобы пришли зоны")
    func fileWithoutZones() async throws {
        let store = fixture.location.store(owner: "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b", cache: "territory-run")
        let ms = TileFile.milliseconds(clock.now)
        try store.write(
            TileFile(
                x: Self.a.x, y: Self.a.y, version: 3, loadedAtMs: ms, savedAtMs: ms, cellCount: nil,
                payload: Data(("[" + TerritoryCacheTests.Server.parcel(3) + "]").utf8)))
        let offline = TerritoryCacheTests.Server()
        await offline.fail(status: 503)
        let restarted = cache(offline)
        #expect(await restarted.restore(visible: [Self.a]) == [Self.a])
        #expect(await restarted.tile(Self.a)?.parcels.first?.level == 3)
        #expect(await restarted.tile(Self.a)?.contestedZones == [])

        let online = TerritoryCacheTests.Server()
        await online.set(Self.a, version: 3)
        try await cache(online).refresh(visible: [Self.a])
        #expect(await online.requests == [["684:5775"]])
    }

    @Test("Время загрузки — из файла: через два часа после перезапуска тайл запрашивается целиком")
    func decayAfterRestart() async throws {
        _ = try await seeded()
        clock.advance(7_200)
        let server = TerritoryCacheTests.Server()
        await server.set(Self.a, version: 3)
        try await cache(server).refresh(visible: [Self.a])
        #expect(await server.requests == [["684:5775"]])
    }

    @Test("Часы перевели назад — восстановленный тайл запрашивается целиком")
    func clockSetBack() async throws {
        _ = try await seeded()
        clock.advance(-86_400)
        let server = TerritoryCacheTests.Server()
        await server.set(Self.a, version: 3)
        try await cache(server).refresh(visible: [Self.a])
        #expect(await server.requests == [["684:5775"]])
    }

    @Test("Версия не уменьшается: сервер отдал вторую — остаётся третья, и после ещё одного перезапуска тоже")
    func versionsNeverGoDown() async throws {
        _ = try await seeded()
        let older = TerritoryCacheTests.Server()
        await older.set(Self.a, version: 2)
        let restarted = cache(older)
        try await restarted.refresh(visible: [Self.a])
        #expect(await restarted.tile(Self.a)?.version == 3)

        let offline = TerritoryCacheTests.Server()
        await offline.fail(status: 503)
        let again = cache(offline)
        await again.restore(visible: [Self.a])
        #expect(await again.tile(Self.a)?.version == 3)
    }

    @Test("Смена аккаунта: тайлы прежнего не видны, его папка стёрта")
    func accountSwitch() async throws {
        _ = try await seeded()
        fixture.set(.signedIn(playerId: "other"))
        let offline = TerritoryCacheTests.Server()
        await offline.fail(status: 503)
        let restarted = cache(offline)
        #expect(await restarted.restore(visible: [Self.a]).isEmpty)
        #expect(await restarted.tile(Self.a) == nil)
        #expect(fixture.files().isEmpty)
    }

    @Test("Смена аккаунта в запущенном приложении — до `reset`: чужие тайлы из памяти не видны")
    func accountSwitchInMemory() async throws {
        let server = TerritoryCacheTests.Server()
        await server.set(Self.a, version: 3)
        let cache = cache(server)
        try await cache.refresh(visible: [Self.a])
        fixture.set(.signedIn(playerId: "other"))
        #expect(await cache.restore(visible: [Self.a]).isEmpty)
        #expect(await cache.tile(Self.a) == nil)
    }

    @Test("Выход: запрос до `reset` стирает папку вышедшего")
    func signOutBeforeReset() async throws {
        let server = try await seeded()
        fixture.set(.signedOut)
        try await cache(server).refresh(visible: [Self.a])
        #expect(fixture.files().isEmpty)
    }

    @Test("Выход, событие которого потерялось: после перезапуска без входа файлов нет")
    func signOutEventLost() async throws {
        _ = try await seeded()
        fixture.set(.signedOut)
        let restarted = cache(TerritoryCacheTests.Server())
        #expect(await restarted.restore(visible: [Self.a]).isEmpty)
        #expect(!FileManager.default.fileExists(atPath: fixture.location.root.path))
    }

    @Test("Хранилище токенов недоступно — это не выход: файлы на месте")
    func unknownOwnerKeepsFiles() async throws {
        _ = try await seeded()
        fixture.set(.unknown)
        #expect(await cache(TerritoryCacheTests.Server()).restore(visible: [Self.a]).isEmpty)
        #expect(fixture.files().count == 2)
    }

    @Test("`reset` стирает файлы; ответ, пришедший после него, на диск не пишется")
    func resetWipesAndDropsLateResponse() async throws {
        let server = TerritoryCacheTests.Server()
        await server.set(Self.a, version: 3)
        let cache = cache(server)
        try await cache.refresh(visible: [Self.b])
        #expect(fixture.files().count == 1)

        await server.holdNextResponse()
        let pending = Task { try await cache.refresh(visible: [Self.a]) }
        try await server.waitUntilHeld()
        await cache.reset()
        #expect(fixture.files().isEmpty)
        await server.release()
        _ = try await pending.value
        #expect(fixture.files().isEmpty)
        #expect(await cache.tile(Self.a) == nil)
    }

    @Test("Испорченный файл пропускается и запрашивается целиком; остальные восстанавливаются")
    func corruptedFileRefetched() async throws {
        _ = try await seeded()
        let url = fixture.location.store(owner: "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b", cache: "territory-run")
            .url(x: Self.a.x, y: Self.a.y)
        try Data("{".utf8).write(to: url)
        let server = TerritoryCacheTests.Server()
        await server.set(Self.a, version: 3)
        let restarted = cache(server)
        try await restarted.refresh(visible: [Self.a, Self.b])
        #expect(await server.requests == [["684:5775", "685:5775@0"]])
        #expect(await restarted.corruptedFiles == 1)
        #expect(await restarted.tile(Self.a)?.version == 3)
    }
}

/// Сервер тумана по сценарию: ответ на каждый запрос задаёт проверка.
actor ScriptedFogServer: ClientTransport {
    private var answers: [(tiles: [String], unchanged: [FogTileRef])] = []
    private(set) var requests: [[String]] = []

    /// Тайл: `(ключ, версия, открыто клеток)`.
    func answer(tiles: [(FogTileRef, Int64, Int)] = [], unchanged: [FogTileRef] = []) {
        let json = tiles.map { key, version, cells in
            let bits = compressedTile(cells: cells).base64EncodedString()
            return #"{"x":\#(key.x),"y":\#(key.y),"version":\#(version),"cellCount":\#(cells),"bits":"\#(bits)"}"#
        }
        answers.append((json, unchanged))
    }

    func send(
        _ request: HTTPRequest, body: HTTPBody?, baseURL: URL, operationID: String
    ) async throws -> (HTTPResponse, HTTPBody?) {
        let items = URLComponents(string: request.path ?? "")?.queryItems ?? []
        requests.append(items.first { $0.name == "tiles" }?.value?.split(separator: ",").map(String.init) ?? [])
        guard !answers.isEmpty else { return (HTTPResponse(status: .serviceUnavailable), nil) }
        let (tiles, unchanged) = answers.removeFirst()
        let refs = unchanged.map { #"{"x":\#($0.x),"y":\#($0.y)}"# }
        let json =
            #"{"layer":"foot","season":null,"tiles":[\#(tiles.joined(separator: ","))],"unchanged":[\#(refs.joined(separator: ","))]}"#
        var fields = HTTPFields()
        fields[.contentType] = "application/json"
        return (HTTPResponse(status: .ok, headerFields: fields), HTTPBody(Data(json.utf8)))
    }
}

@Suite("Туман переживает перезапуск")
struct FogCacheDiskTests {
    private let fixture = DiskFixture()
    private static let a = FogTileRef(x: 9270, y: 5404)

    private func cache(_ server: ScriptedFogServer) -> FogCache {
        let api = Client(
            serverURL: URL(string: "https://api.example")!, configuration: GorodkiAPI.configuration, transport: server)
        return FogCache(api: api, layer: .foot, disk: fixture.disk)
    }

    private func seeded(version: Int64 = 5, cells: Int = 50) async throws {
        let server = ScriptedFogServer()
        await server.answer(tiles: [(Self.a, version, cells)])
        try await cache(server).refresh(visible: [Self.a])
    }

    @Test("После перезапуска — те же биты; тайл устаревший, запрос — с его версией")
    func survivesRestart() async throws {
        try await seeded()
        let server = ScriptedFogServer()
        let restarted = cache(server)
        #expect(await restarted.restore(visible: [Self.a]) == [Self.a])
        #expect(await restarted.tile(Self.a)?.cellCount == 50)
        #expect(await FogTileCodec.cellCount(restarted.tile(Self.a)?.words ?? []) == 50)

        await server.answer(unchanged: [Self.a])
        try await restarted.refresh(visible: [Self.a])
        #expect(await server.requests == [["9270:5404@5"]])
        #expect(await restarted.corruptedFiles == 0)
    }

    @Test(
        "Стёртый очисткой истории тайл и на диске пустой с версией 0: после перезапуска — запрос @0, открытый заново принимается"
    )
    func erasedTileAfterRestart() async throws {
        try await seeded()
        let server = ScriptedFogServer()
        await server.answer(tiles: [(Self.a, 6, 0)])  // очистка истории: пустой с версией выше
        try await cache(server).refresh(visible: [Self.a])

        let relaunched = ScriptedFogServer()
        let again = cache(relaunched)
        #expect(await again.restore(visible: [Self.a]) == [Self.a])
        let empty = try #require(await again.tile(Self.a))
        #expect(empty.version == 0 && empty.cellCount == 0 && empty.words.isEmpty)
        #expect(await again.corruptedFiles == 0)  // пустой хранится без бит — это не порча

        // Первый запрос после перезапуска — с версией 0: сервер ответит «без изменений», а не новым пустым тайлом.
        await relaunched.answer(unchanged: [Self.a])
        #expect(try await again.refresh(visible: [Self.a]).isEmpty)
        #expect(await relaunched.requests == [["9270:5404@0"]])

        // Новый забег открыл его снова — принимается с любой версией и переживает ещё один перезапуск.
        await relaunched.answer(tiles: [(Self.a, 2, 20)])
        await again.invalidate()
        #expect(try await again.refresh(visible: [Self.a]) == [Self.a])
        let third = cache(ScriptedFogServer())
        await third.restore(visible: [Self.a])
        #expect(await third.tile(Self.a)?.version == 2)
        #expect(await third.tile(Self.a)?.cellCount == 20)
    }

    @Test("Испорченный файл тумана (сумма верна, число клеток нет) — тайл запрашивается с нуля")
    func wrongCellCountRefetched() async throws {
        try await seeded()
        let store = fixture.location.store(owner: "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b", cache: "fog-foot-all")
        guard case .ok(var file) = store.read(x: Self.a.x, y: Self.a.y) else {
            Issue.record("файл не записан")
            return
        }
        file = TileFile(
            x: file.x, y: file.y, version: file.version, loadedAtMs: file.loadedAtMs, savedAtMs: file.savedAtMs,
            cellCount: 49, payload: file.payload)
        try store.write(file)
        let server = ScriptedFogServer()
        await server.answer(tiles: [(Self.a, 5, 50)])
        let restarted = cache(server)
        try await restarted.refresh(visible: [Self.a])
        #expect(await server.requests == [["9270:5404@0"]])
        #expect(await restarted.corruptedFiles == 1)
        #expect(await restarted.tile(Self.a)?.cellCount == 50)
    }

    @Test("Версия тумана не уменьшается: запоздавший старый ответ не затирает ни память, ни файл")
    func versionsNeverGoDown() async throws {
        try await seeded(version: 5, cells: 50)
        let server = ScriptedFogServer()
        await server.answer(tiles: [(Self.a, 4, 40)])
        let restarted = cache(server)
        try await restarted.refresh(visible: [Self.a])
        #expect(await restarted.tile(Self.a)?.version == 5)

        let again = cache(ScriptedFogServer())
        await again.restore(visible: [Self.a])
        #expect(await again.tile(Self.a)?.version == 5)
        #expect(await again.tile(Self.a)?.cellCount == 50)
    }

    @Test("`reset` (выход, смена аккаунта) стирает тайлы тумана в памяти и на диске")
    func resetWipes() async throws {
        let server = ScriptedFogServer()
        await server.answer(tiles: [(Self.a, 5, 50)])
        let cache = cache(server)
        try await cache.refresh(visible: [Self.a])
        #expect(fixture.files().count == 1)
        await cache.reset()
        #expect(fixture.files().isEmpty)
        #expect(await cache.tile(Self.a) == nil)
        #expect(await cache.restore(visible: [Self.a]).isEmpty)
    }

    @Test("Сезонный туман — в своей папке")
    func seasonFolder() async throws {
        let server = ScriptedFogServer()
        await server.answer(tiles: [(Self.a, 5, 50)])
        let api = Client(
            serverURL: URL(string: "https://api.example")!, configuration: GorodkiAPI.configuration, transport: server)
        try await FogCache(api: api, layer: .foot, season: 2, disk: fixture.disk).refresh(visible: [Self.a])
        #expect(fixture.files() == ["v1/0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b/fog-foot-s2/9270_5404.tile"])
    }
}
