import Network
import Realtime
import SwiftUI
import Synchronization
import UIKit

/// Точка входа приложения «Городки».
@main
struct GorodkiApp: App {
    @UIApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        WindowGroup {
            RootView()
                .task {
                    NetworkWatcher.shared.start()
                    RealtimeRelay.shared.start()
                    SessionRelay.shared.start()
                }
        }
        .onChange(of: scenePhase) { _, phase in
            let dependencies = AppDependencies.shared
            switch phase {
            case .active:
                // Дослать очередь и сбросить шаг повторов (docs/architecture/sync.md); подключить реальное время.
                Task {
                    await dependencies.realtime?.start()
                    await dependencies.syncScheduler()?.trigger(.appActive)
                }
            case .background:
                // В фоне подсказки некому показывать, а соединение тратит батарею: закрыть, а не ждать, пока iOS
                // оборвёт его сама (у сервера — не больше трёх соединений на игрока).
                Task { await dependencies.realtime?.stop() }
                // Дослать очередь, пока iOS даёт время, и попросить фоновые пробуждения.
                BackgroundSync.enterBackground()
            default:
                break
            }
        }
    }
}

/// Подсказки реального времени → синхронизация (docs/architecture/realtime.md, «Контракт для приложения»).
/// Слушает поток всё время жизни приложения — не в `.task` экрана: отмена чтения закрыла бы поток навсегда.
@MainActor
final class RealtimeRelay {
    static let shared = RealtimeRelay()

    private var started = false

    func start() {
        guard !started, let realtime = AppDependencies.shared.realtime else { return }
        started = true
        Task.detached {
            for await event in realtime.events {
                switch event {
                case .connected, .captureDecided:
                    // После подключения — догнать пропущенное; итог заявки забирает синхронизация. Не ждать прохода:
                    // подсказки, пришедшие во время него, расписание склеит в один следующий.
                    Task { await AppDependencies.shared.syncScheduler()?.trigger(.hint) }
                case .tilesChanged:
                    // Карты в приложении ещё нет (этап 2): она перезапросит видимые тайлы с известными версиями.
                    break
                }
            }
        }
    }
}

/// Вход и выход (`TokenStore.events`; подписчик у потока один — этот): соединение реального времени держит токен того,
/// кто вошёл, поэтому при смене входа оно закрывается и открывается заново. Экран входа (ждёт Client ID, #4) будет
/// получать состояние отсюда же.
@MainActor
final class SessionRelay {
    static let shared = SessionRelay()

    private var started = false

    func start() {
        guard !started else { return }
        started = true
        let dependencies = AppDependencies.shared
        Task.detached {
            for await event in dependencies.tokens.events {
                await dependencies.realtime?.stop()
                guard case .signedIn = event else { continue }
                if await MainActor.run(body: { UIApplication.shared.applicationState == .active }) {
                    await dependencies.realtime?.start()
                }
                await dependencies.syncScheduler()?.trigger(.signedIn)
            }
        }
    }
}

/// Сеть вернулась — дослать очередь и переподключить реальное время сразу, не дожидаясь таймеров повторов.
@MainActor
final class NetworkWatcher {
    static let shared = NetworkWatcher()

    private let monitor = NWPathMonitor()
    private var started = false

    func start() {
        guard !started else { return }
        started = true
        let online = OnlineFlag()
        // @Sendable: обработчик зовёт очередь монитора, а не главный поток — изоляцию главного актора ему нельзя.
        monitor.pathUpdateHandler = { @Sendable path in
            let isOnline = path.status == .satisfied
            let cameBack = online.state.withLock { wasOnline in
                defer { wasOnline = isOnline }
                return isOnline && !wasOnline
            }
            if cameBack {
                Task {
                    await AppDependencies.shared.realtime?.wake()
                    await AppDependencies.shared.syncScheduler()?.trigger(.networkRestored)
                }
            }
        }
        monitor.start(queue: DispatchQueue(label: "gorodki.network"))
    }

    /// Была ли сеть в прошлый раз. Mutex нельзя копировать — обработчик держит его через ссылку.
    private final class OnlineFlag: Sendable {
        let state = Mutex(false)
    }
}
