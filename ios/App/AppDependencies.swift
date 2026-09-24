import Foundation
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
    /// Очередь синхронизации — общая для записи забега (`RunRecorder`) и доставки (`SyncEngine`). В приложении — в базе
    /// GRDB (`GRDBSyncStore`): неотправленные забеги переживают выгрузку приложения и перезапуск телефона.
    let syncStore: any SyncStore
    /// «Сейчас» для синхронизации, секунды Unix: часы внедряются, чтобы их можно было подменить в проверках.
    private let now: @Sendable () -> Double
    private let engine = Mutex<(ownerId: String, engine: SyncEngine, scheduler: SyncScheduler)?>(nil)

    init(
        serverURL: URL?, tokenStorage: any TokenStorage, syncStore: any SyncStore = InMemorySyncStore(),
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
        self.realtime = serverURL.map { url in
            RealtimeClient(
                connector: SignalRConnector(
                    serverURL: url, tokens: tokens,
                    refresher: ClientFactory.refresher(serverURL: url, transport: transport)))
        }
        self.syncStore = syncStore
        self.now = now
    }

    /// Для запуска приложения: адрес — из Info.plist (`GorodkiServerURL`), токены — в Keychain под bundle ID.
    static func live(bundle: Bundle = .main) -> AppDependencies {
        AppDependencies(
            serverURL: ServerURL.parse(bundle.object(forInfoDictionaryKey: ServerURL.infoPlistKey) as? String),
            // Bundle ID у приложения есть всегда; запасное имя — только чтобы не падать.
            tokenStorage: KeychainTokenStorage(bundleIdentifier: bundle.bundleIdentifier ?? "gorodki"),
            syncStore: liveSyncStore())
    }

    /// Очередь в базе приложения (Application Support). Если базу не открыть (например, нет места), — в памяти:
    /// приложение не падает, забеги этого запуска уйдут, пока его не выгрузили.
    private static func liveSyncStore() -> any SyncStore {
        do {
            return GRDBSyncStore(try AppDatabase.live())
        } catch {
            return InMemorySyncStore()
        }
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
        return engine.withLock { cached in
            if let cached, cached.ownerId == ownerId {
                return (cached.engine, cached.scheduler)
            }
            let created = SyncEngine(store: store, api: api, ownerId: ownerId, now: now)
            let scheduler = SyncScheduler(
                engine: created,
                backlog: { (try? await SyncBacklog.of(store, ownerId: ownerId)) ?? SyncBacklog() },
                appActive: { await MainActor.run { UIApplication.shared.applicationState == .active } })
            cached = (ownerId, created, scheduler)
            return (created, scheduler)
        }
    }
}
