import Foundation
import GameCore
import GorodkiAPI
import Networking
import Persistence
import Realtime
import Sync
import Synchronization
import UIKit

/// Зависимости приложения (PLAN.md, §7.2 «Паттерны»): адрес сервера, клиент API, токены входа, очередь
/// синхронизации и реальное время. Создаются один раз при запуске (`shared`), экраны получают их параметром.
/// Как это собрано — docs/architecture/ios-app.md.
///
/// Сервер ещё не развёрнут, поэтому «адрес не задан» — обычное состояние, а не ошибка: клиента нет, синхронизация
/// не запускается, а очередь забегов копится на телефоне и уйдёт, когда адрес появится.
final class AppDependencies: Sendable {
    static let shared = AppDependencies.live()

    /// Адрес сервера; `nil` — не задан в настройках сборки.
    let serverURL: URL?
    let tokens: TokenStore
    /// Клиент API: подписывает запросы и обновляет токен при 401. `nil`, пока адрес сервера не задан.
    let api: Client?
    /// Вход и выход (`SignInService`); `nil`, пока адрес сервера не задан. Экран входа ждёт Client ID Google (#4).
    let signIn: SignInService?
    /// Реальное время (docs/architecture/realtime.md): одно соединение на приложение — под тем, кто сейчас вошёл.
    /// `nil`, пока адрес сервера не задан.
    let realtime: RealtimeClient?
    /// Земля «Бега» по тайлам с видимыми версиями (docs/architecture/territory-map.md): карта (этап 2) берёт тайлы
    /// отсюда. `nil`, пока адрес сервера не задан.
    let territory: TerritoryCache?
    /// Свой туман «Пешком» за всё время (docs/architecture/fog.md): карта (этап 2) берёт тайлы отсюда. `nil`, пока адрес
    /// сервера не задан.
    let fog: FogCache?
    /// Где земля и туман лежат на диске (`Caches/gorodki-tiles`); `nil` — тайлы только в памяти.
    let tileLocation: TileCacheLocation?
    /// Зоны приватности, удаление аккаунта, «мои данные»; `nil`, пока адрес сервера не задан.
    let account: AccountService?
    /// История законченных забегов — точки для GPX после того, как очередь их стёрла; `nil` — база не открылась.
    let history: RunHistory?
    /// Выгрузки для приложения «Файлы» (`Documents/Exports`).
    let exports: ExportFolder
    /// Запись для демо-повтора (`RunController`): одна, последняя, только на телефоне.
    let demoRecordingURL: URL?
    /// Очередь синхронизации — общая для записи забега (`RunRecorder`) и доставки (`SyncEngine`). В приложении — в базе
    /// GRDB (`GRDBSyncStore`): неотправленные забеги переживают выгрузку приложения и перезапуск телефона.
    let syncStore: any SyncStore
    /// Правила для нового забега (`GET /config`): последняя известная версия — в файле, свежая запрашивается в фоне.
    let rules: RulesStore
    /// Идентификатор установки — `deviceId` каждого забега (Keychain, своя запись).
    let installation: InstallationID
    /// Очередь переживёт выгрузку приложения (в базе). `false` — база не открылась, очередь в памяти.
    let queueSurvivesRestart: Bool
    /// «Сейчас» для синхронизации, секунды Unix: часы внедряются, чтобы их можно было подменить в проверках.
    private let now: @Sendable () -> Double
    private let engine = Mutex<(ownerId: String, engine: SyncEngine, scheduler: SyncScheduler)?>(nil)

    init(
        serverURL: URL?, tokenStorage: any TokenStorage, syncStore: any SyncStore = InMemorySyncStore(),
        rulesStorage: any RulesStorage = InMemoryRulesStorage(),
        installationStorage: any TokenStorage = InMemoryTokenStorage(), tileLocation: TileCacheLocation? = nil,
        history: RunHistory? = nil, exports: ExportFolder = .live(), demoRecordingURL: URL? = nil,
        now: @escaping @Sendable () -> Double = { Date.now.timeIntervalSince1970 }
    ) {
        let tokens = TokenStore(storage: tokenStorage)
        let transport = ClientFactory.urlSessionTransport()
        self.serverURL = serverURL
        self.tokens = tokens
        let api = serverURL.map { ClientFactory.make(serverURL: $0, tokens: tokens, transport: transport) }
        self.api = api
        self.signIn = api.map { SignInService(api: $0, tokens: tokens) }
        self.account = api.map { AccountService(api: $0) }
        // Файлы тайлов у каждого игрока свои: кэш сам сверяется, кто вошёл, перед каждым чтением с диска.
        let disk = tileLocation.map { location in
            TileCacheDisk(location: location, owner: { await tokens.sessionOwner() })
        }
        self.tileLocation = tileLocation
        self.territory = api.map { TerritoryCache(api: $0, league: .run, disk: disk) }
        self.fog = api.map { FogCache(api: $0, layer: .foot, disk: disk) }
        self.history = history
        self.exports = exports
        self.demoRecordingURL = demoRecordingURL
        self.realtime = serverURL.map { url in
            RealtimeClient(
                connector: SignalRConnector(
                    serverURL: url, tokens: tokens,
                    refresher: ClientFactory.refresher(serverURL: url, transport: transport)))
        }
        self.syncStore = syncStore
        self.rules = RulesStore(api: api, storage: rulesStorage)
        self.installation = InstallationID(storage: installationStorage)
        self.queueSurvivesRestart = !(syncStore is InMemorySyncStore)
        self.now = now
    }

    /// Для запуска приложения: адрес — из Info.plist (`GorodkiServerURL`), токены — в Keychain под bundle ID.
    static func live(bundle: Bundle = .main) -> AppDependencies {
        // Очередь и история — в базе приложения (Application Support). Если базу не открыть (например, нет места),
        // очередь — в памяти: приложение не падает, но забег, не доставленный до выгрузки приложения, пропадёт —
        // `queueSurvivesRestart` скажет об этом экрану забега (этап 2); истории забегов тогда нет.
        let database = try? AppDatabase.live()
        // Конфиг и запись демо-повтора — рядом с базой (Application Support).
        let support = URL.applicationSupportDirectory
        return AppDependencies(
            serverURL: ServerURL.parse(bundle.object(forInfoDictionaryKey: ServerURL.infoPlistKey) as? String),
            // Bundle ID у приложения есть всегда; запасное имя — только чтобы не падать.
            tokenStorage: KeychainTokenStorage(bundleIdentifier: bundle.bundleIdentifier ?? "gorodki"),
            syncStore: database.map { GRDBSyncStore($0) } ?? InMemorySyncStore(),
            rulesStorage: FileRulesStorage(url: support.appendingPathComponent("rules.json")),
            installationStorage: KeychainTokenStorage(
                service: (bundle.bundleIdentifier ?? "gorodki") + ".install", account: "id"),
            tileLocation: .live(), history: database.map { RunHistory($0) }, exports: .live(),
            demoRecordingURL: support.appendingPathComponent("demo-recording.json"))
    }

    /// Начать забег (экран забега — этап 2): правила последней известной версии конфига, идентификатор установки,
    /// вошедший игрок. Забег сразу в очереди; `nil` — никто не вошёл: без входа забег некому отправить.
    /// - Parameter motionAuthorized: разрешён ли доступ к датчикам движения (без него захватов нет — сервер знает).
    /// - Parameters:
    ///   - source: `.replay` — демо-повтор (сервер разрешает его только ролям `demo` и `admin`).
    ///   - startedAt: начало, секунды Unix; у повтора — в прошлом (`RunReplay.startedAt`), иначе «сейчас».
    func startRun(
        league: GameCore.League, motionAuthorized: Bool, source: LocalRun.Source = .live, startedAt: Double? = nil
    ) async throws -> RunSession? {
        guard let ownerId = await tokens.current()?.playerId else { return nil }
        let session = try await beginRun(
            ownerId: ownerId, league: league, motionAuthorized: motionAuthorized, source: source, startedAt: startedAt)
        Task { await syncScheduler()?.trigger(.recorded) }
        return session
    }

    /// Пробный забег «Лаборатории» (`ProbeRun`): тот же путь, что у забега игрока, — правила последней известной версии
    /// (без сервера — с которыми собрано приложение), очередь в базе, — но без входа: хозяин `LocalRun.labOwnerId`.
    /// Синхронизация его не видит — она берёт только забеги вошедшего игрока.
    func startProbeRun(league: GameCore.League, motionAuthorized: Bool) async throws -> RunSession {
        try await beginRun(ownerId: LocalRun.labOwnerId, league: league, motionAuthorized: motionAuthorized)
    }

    private func beginRun(
        ownerId: String, league: GameCore.League, motionAuthorized: Bool, source: LocalRun.Source = .live,
        startedAt: Double? = nil
    ) async throws -> RunSession {
        let rules = await self.rules.current()
        let run = LocalRun(
            id: UUID(), ownerId: ownerId, league: league, source: source, configVersion: rules.version,
            startedAtMs: StoragePrecision.milliseconds(startedAt ?? now()), deviceId: await installation.value(),
            appVersion: Self.appVersion, motionAuthorized: motionAuthorized)
        let newcomer = try await RunSession.isNewcomer(ownerId: ownerId, store: syncStore)
        return try await RunSession.start(run, store: syncStore, rules: rules.rules, newcomer: newcomer)
    }

    // MARK: - Данные на телефоне

    /// Стереть всё, что телефон хранит об игроке (выход из аккаунта и его удаление, docs/architecture/ios-app.md): вход,
    /// очередь синхронизации (с недоставленными забегами), землю и туман в памяти и на диске, правила, запись
    /// демо-повтора, историю забегов. Каждая часть стирается, даже если предыдущая не стёрлась. Выгрузки в «Файлах»
    /// (`Documents/Exports`) остаются: их игрок сохранил сам.
    /// - Throws: первую ошибку стирания — остальные части к этому времени уже стёрты.
    func wipeLocalData() async throws {
        if await tokens.current() != nil {
            await tokens.signOut()  // выход через `SignInService` уже стёр вход — второе событие «вышел» не нужно
        }
        let scheduler = engine.withLock { cached in
            defer { cached = nil }
            return cached?.scheduler
        }
        await scheduler?.stop()
        var failures: [any Error] = []
        do { try await syncStore.removeAll() } catch { failures.append(error) }
        await territory?.reset()
        await fog?.reset()
        tileLocation?.removeAll()  // и без адреса сервера: файлы могли остаться от сборки, где он был
        do { try await rules.removeLocal() } catch { failures.append(error) }
        if let demoRecordingURL, FileManager.default.fileExists(atPath: demoRecordingURL.path) {
            do { try FileManager.default.removeItem(at: demoRecordingURL) } catch { failures.append(error) }
        }
        do { try await history?.removeAll() } catch { failures.append(error) }
        if let first = failures.first { throw first }
    }

    /// Удалить аккаунт (`DELETE /me`), затем стереть всё на телефоне. Аккаунта на сервере уже нет (404) — тоже стереть.
    /// - Returns: когда сервер сотрёт данные; `nil` — адрес сервера не задан (ничего не сделано) или аккаунта уже нет.
    /// - Throws: ошибку сети или сервера — тогда на телефоне ничего не стёрто: аккаунт ещё есть.
    @discardableResult
    func deleteAccount() async throws -> AccountService.Deletion? {
        guard let account else { return nil }
        let deletion: AccountService.Deletion?
        do {
            deletion = try await account.deleteAccount()
        } catch AccountServiceError.notFound {
            deletion = nil
        }
        try await wipeLocalData()
        return deletion
    }

    /// «Мои данные» (`GET /me/export`) — файлом в `Exports/` (заменяет прежний).
    /// - Returns: где лежит файл; `nil` — адрес сервера не задан.
    func exportMyData() async throws -> URL? {
        guard let account else { return nil }
        return try exports.write(try await account.exportData(), named: "gorodki-my-data.json")
    }

    /// Сохранить законченные забеги в историю (после «Финиша» и при запуске): очередь сотрёт их точки, как только сервер
    /// подтвердит забег, а GPX выгружается из истории.
    func archiveFinishedRuns() async throws {
        _ = try await history?.archiveEnded(from: syncStore)
    }

    /// След забега из истории — файлом GPX в `Exports/`.
    /// - Returns: где лежит файл; `nil` — забега в истории нет.
    func exportRunGPX(_ id: UUID) async throws -> URL? {
        guard let history, let entry = try await history.entries().first(where: { $0.id == id }),
            let points = try await history.points(of: id)
        else { return nil }
        let name = GPX.fileName(startedAt: Double(entry.startedAtMs) / 1_000, league: entry.league)
        let document = GPX.document(name: String(name.dropLast(".gpx".count)), points: points)
        return try exports.write(Data(document.utf8), named: name)
    }

    /// Версия сборки для сервера: «0.1.0 (1)».
    static var appVersion: String {
        let info = Bundle.main.infoDictionary
        let short = info?["CFBundleShortVersionString"] as? String ?? "0"
        let build = info?["CFBundleVersion"] as? String ?? "0"
        return "\(short) (\(build))"
    }

    /// Синхронизация вошедшего игрока; `nil` — адрес сервера не задан или никто не вошёл. Движок один на игрока:
    /// два движка над одной очередью шли бы проходами вперемешку и задваивали бы запросы.
    func syncEngine() async -> SyncEngine? {
        await sync()?.engine
    }

    /// Расписание синхронизации вошедшего игрока (`SyncScheduler`): когда запускать проход — по событиям приложения
    /// и таймеру повторов. Одно на игрока, как и движок.
    func syncScheduler() async -> SyncScheduler? {
        await sync()?.scheduler
    }

    private func sync() async -> (engine: SyncEngine, scheduler: SyncScheduler)? {
        guard let api, let ownerId = await tokens.current()?.playerId else { return nil }
        let store = syncStore
        let tokens = self.tokens
        let (current, replaced) = engine.withLock { cached in
            if let cached, cached.ownerId == ownerId {
                return ((cached.engine, cached.scheduler), SyncScheduler?.none)
            }
            let created = SyncEngine(
                store: store, api: api, ownerId: ownerId, now: now,
                signedInPlayer: { await tokens.current()?.playerId })
            let scheduler = SyncScheduler(
                engine: created,
                backlog: { (try? await SyncBacklog.of(store, ownerId: ownerId)) ?? SyncBacklog() },
                appActive: { await MainActor.run { UIApplication.shared.applicationState == .active } })
            let previous = cached?.scheduler
            cached = (ownerId, created, scheduler)
            return ((created, scheduler), previous)
        }
        // Таймер прежнего игрока больше не нужен; его идущий проход остановится сам на следующем запросе.
        await replaced?.stop()
        return current
    }
}
