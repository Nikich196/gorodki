import Foundation
import GorodkiAPI
import Networking
import Persistence
import Sync
import Synchronization

/// Зависимости приложения (PLAN.md, §7.2 «Паттерны»): адрес сервера, клиент API, токены входа и очередь
/// синхронизации. Создаются один раз при запуске (`shared`), экраны получают их параметром.
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
    /// Очередь синхронизации — общая для записи забега (`RunRecorder`) и доставки (`SyncEngine`). В приложении — в базе
    /// GRDB (`GRDBSyncStore`): неотправленные забеги переживают выгрузку приложения и перезапуск телефона.
    let syncStore: any SyncStore
    /// «Сейчас» для синхронизации, секунды Unix: часы внедряются, чтобы их можно было подменить в проверках.
    private let now: @Sendable () -> Double
    private let engine = Mutex<(ownerId: String, engine: SyncEngine)?>(nil)

    init(
        serverURL: URL?, tokenStorage: any TokenStorage, syncStore: any SyncStore = InMemorySyncStore(),
        now: @escaping @Sendable () -> Double = { Date.now.timeIntervalSince1970 }
    ) {
        let tokens = TokenStore(storage: tokenStorage)
        self.serverURL = serverURL
        self.tokens = tokens
        self.api = serverURL.map { ClientFactory.make(serverURL: $0, tokens: tokens) }
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
        guard let api, let ownerId = await tokens.current()?.playerId else { return nil }
        return engine.withLock { cached in
            if let cached, cached.ownerId == ownerId {
                return cached.engine
            }
            let created = SyncEngine(store: syncStore, api: api, ownerId: ownerId, now: now)
            cached = (ownerId, created)
            return created
        }
    }
}
