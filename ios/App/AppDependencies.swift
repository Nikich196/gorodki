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
        installationStorage: any TokenStorage = InMemoryTokenStorage(),
        now: @escaping @Sendable () -> Double = { Date.now.timeIntervalSince1970 }
    ) {
        let tokens = TokenStore(storage: tokenStorage)
        let transport = ClientFactory.urlSessionTransport()
        self.serverURL = serverURL
        self.tokens = tokens
        let api = serverURL.map { ClientFactory.make(serverURL: $0, tokens: tokens, transport: transport) }
        self.api = api
        self.signIn = api.map { SignInService(api: $0, tokens: tokens) }
        self.territory = api.map { TerritoryCache(api: $0, league: .run) }
        self.fog = api.map { FogCache(api: $0, layer: .foot) }
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
        AppDependencies(
            serverURL: ServerURL.parse(bundle.object(forInfoDictionaryKey: ServerURL.infoPlistKey) as? String),
            // Bundle ID у приложения есть всегда; запасное имя — только чтобы не падать.
            tokenStorage: KeychainTokenStorage(bundleIdentifier: bundle.bundleIdentifier ?? "gorodki"),
            syncStore: liveSyncStore(),
            rulesStorage: FileRulesStorage(url: liveRulesURL()),
            installationStorage: KeychainTokenStorage(
                service: (bundle.bundleIdentifier ?? "gorodki") + ".install", account: "id"))
    }

    /// Последний полученный конфиг — рядом с базой приложения (Application Support).
    private static func liveRulesURL() -> URL {
        URL.applicationSupportDirectory.appendingPathComponent("rules.json")
    }

    /// Очередь в базе приложения (Application Support). Если базу не открыть (например, нет места), — в памяти:
    /// приложение не падает, но забег, не доставленный до выгрузки приложения, пропадёт — `queueSurvivesRestart`
    /// скажет об этом экрану забега (этап 2).
    private static func liveSyncStore() -> any SyncStore {
        do {
            return GRDBSyncStore(try AppDatabase.live())
        } catch {
            return InMemorySyncStore()
        }
    }

    /// Начать забег (экран забега — этап 2): правила последней известной версии конфига, идентификатор установки,
    /// вошедший игрок. Забег сразу в очереди; `nil` — никто не вошёл: без входа забег некому отправить.
    /// - Parameter motionAuthorized: разрешён ли доступ к датчикам движения (без него захватов нет — сервер знает).
    func startRun(league: GameCore.League, motionAuthorized: Bool) async throws -> RunSession? {
        guard let ownerId = await tokens.current()?.playerId else { return nil }
        let rules = await self.rules.current()
        let run = LocalRun(
            id: UUID(), ownerId: ownerId, league: league, configVersion: rules.version,
            startedAtMs: StoragePrecision.milliseconds(now()), deviceId: await installation.value(),
            appVersion: Self.appVersion, motionAuthorized: motionAuthorized)
        let newcomer = try await RunSession.isNewcomer(ownerId: ownerId, store: syncStore)
        let session = try await RunSession.start(run, store: syncStore, rules: rules.rules, newcomer: newcomer)
        Task { await syncScheduler()?.trigger(.recorded) }
        return session
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
