import Foundation
import GameCore
import Networking
import Persistence
import Sync
import Testing

@testable import Gorodki

/// Синхронизация вошедшего игрока (`AppDependencies.syncEngine()`, docs/architecture/ios-app.md): движок и расписание —
/// одни на игрока. Два движка над одной очередью шли бы проходами вперемешку и задваивали бы запросы, а таймер
/// прежнего игрока будил бы синхронизацию и после смены входа.
@Suite("Зависимости приложения: синхронизация — одна на вошедшего игрока")
struct AppDependenciesTests {
    /// Адрес только для того, чтобы появился клиент API: проходов в этих тестах нет, в сеть никто не ходит.
    private static let serverURL = URL(string: "https://gorodki.invalid")

    @Test("Тот же игрок — тот же движок и расписание; другой — новые, а расписание прежнего остановлено")
    func oneEnginePerPlayer() async throws {
        let dependencies = AppDependencies(
            serverURL: try #require(Self.serverURL), tokenStorage: InMemoryTokenStorage())
        await dependencies.tokens.signIn(Self.tokens(player: "player-1"))
        let first = try #require(await dependencies.syncEngine())
        let firstScheduler = try #require(await dependencies.syncScheduler())
        #expect(await dependencies.syncEngine() === first)
        #expect(await dependencies.syncScheduler() === firstScheduler)
        // Прохода не было, остановки тоже: итога нет, и прежние заявки iOS расписание не трогает.
        #expect(await firstScheduler.backgroundRequests == nil)

        await dependencies.tokens.signIn(Self.tokens(player: "player-2"))
        let second = try #require(await dependencies.syncEngine())
        #expect(second !== first)
        #expect(await dependencies.syncScheduler() !== firstScheduler)
        // `stop()` — единственное, что даёт итог без прохода: пустой, то есть таймер прежнего игрока снят.
        let afterStop = await firstScheduler.backgroundRequests
        #expect(afterStop?.isEmpty == true, "фоновые заявки прежнего игрока: \(String(describing: afterStop))")
        #expect(await dependencies.syncEngine() === second)
    }

    @Test("Вышел и вошёл тот же игрок: без входа синхронизации нет, после входа — прежние движок и расписание")
    func sameEngineAfterSignOutAndBack() async throws {
        let dependencies = AppDependencies(
            serverURL: try #require(Self.serverURL), tokenStorage: InMemoryTokenStorage())
        await dependencies.tokens.signIn(Self.tokens(player: "player-1"))
        let first = try #require(await dependencies.syncEngine())
        let firstScheduler = try #require(await dependencies.syncScheduler())

        await dependencies.tokens.signOut()
        #expect(await dependencies.syncEngine() == nil)
        #expect(await dependencies.syncScheduler() == nil)

        // Выход движок игрока не сбрасывает: очередь та же, а проход, начатый до выхода и ещё идущий, на следующем
        // запросе увидит, что вошёл снова он, и продолжит. Второй движок шёл бы рядом вперемешку и задваивал запросы.
        await dependencies.tokens.signIn(Self.tokens(player: "player-1"))
        #expect(await dependencies.syncEngine() === first)
        #expect(await dependencies.syncScheduler() === firstScheduler)
    }

    @Test("Без входа или без адреса сервера синхронизации нет — очередь просто копится")
    func noSyncWithoutPlayerOrServer() async throws {
        let signedOut = AppDependencies(serverURL: try #require(Self.serverURL), tokenStorage: InMemoryTokenStorage())
        #expect(await signedOut.syncEngine() == nil)

        let offline = AppDependencies(serverURL: nil, tokenStorage: InMemoryTokenStorage())
        await offline.tokens.signIn(Self.tokens(player: "player-1"))
        #expect(await offline.syncEngine() == nil)
        #expect(await offline.syncScheduler() == nil)
    }

    @Test("Пробный забег начинается без входа и сервера: хозяин «lab», правила — с которыми собрано приложение")
    func probeRunWithoutSignIn() async throws {
        let queue = InMemorySyncStore()
        let dependencies = AppDependencies(serverURL: nil, tokenStorage: InMemoryTokenStorage(), syncStore: queue)

        let session = try await dependencies.startProbeRun(league: GameCore.League.run, motionAuthorized: true)

        let run = try #require(await queue.runs().first)
        #expect(run.id == session.runId && run.isLabProbe)
        #expect(run.configVersion == RulesVersion.bundled.version)
        #expect(try await dependencies.startRun(league: GameCore.League.run, motionAuthorized: true) == nil)
        #expect(await queue.runs().count == 1)
    }

    @Test("Выход стирает всё на телефоне: вход, очередь, правила, тайлы на диске, запись повтора, историю")
    func wipeLocalData() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: folder) }
        let database = try AppDatabase.inMemory()
        let queue = GRDBSyncStore(database)
        let rules = InMemoryRulesStorage(Data("{}".utf8))
        let tiles = TileCacheLocation(root: folder.appendingPathComponent("tiles"))
        let demo = folder.appendingPathComponent("demo-recording.json")
        let dependencies = AppDependencies(
            serverURL: try #require(Self.serverURL), tokenStorage: InMemoryTokenStorage(), syncStore: queue,
            rulesStorage: rules, tileLocation: tiles, history: RunHistory(database),
            exports: ExportFolder(url: folder.appendingPathComponent("Exports")), demoRecordingURL: demo)
        await dependencies.tokens.signIn(Self.tokens(player: "player-1"))
        _ = try #require(await dependencies.syncEngine())
        var run = LocalRun(
            id: UUID(), ownerId: "player-1", league: GameCore.League.run, configVersion: 1, startedAtMs: 1_000,
            deviceId: UUID(), appVersion: "test", motionAuthorized: false)
        run.endedAtMs = 2_000
        try await queue.insert(run)
        try await dependencies.archiveFinishedRuns()
        #expect(try await dependencies.history?.entries().count == 1)
        try FileManager.default.createDirectory(at: tiles.root, withIntermediateDirectories: true)
        try Data("x".utf8).write(to: tiles.root.appendingPathComponent("tile"))
        try Data("{}".utf8).write(to: demo)

        try await dependencies.wipeLocalData()

        #expect(await dependencies.tokens.current() == nil)
        #expect(await dependencies.syncEngine() == nil)
        #expect(try await queue.runs().isEmpty)
        #expect(rules.load() == nil)
        #expect(!FileManager.default.fileExists(atPath: tiles.root.path))
        #expect(!FileManager.default.fileExists(atPath: demo.path))
        #expect(try await dependencies.history?.entries().isEmpty == true)
    }

    /// Вошедший игрок — claim `sub` access-токена; подпись телефон не проверяет (docs/architecture/auth.md).
    private static func tokens(player: String) -> AuthTokens {
        AuthTokens(
            accessToken: "\(base64URL(#"{"alg":"HS256","typ":"JWT"}"#)).\(base64URL(#"{"sub":"\#(player)"}"#)).c2ln",
            refreshToken: "refresh-\(player)")
    }

    private static func base64URL(_ text: String) -> String {
        Data(text.utf8).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}
