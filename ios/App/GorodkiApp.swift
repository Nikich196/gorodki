import GorodkiAPI
import Network
import Networking
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
                // Свежие правила — в фоне: «Старт» их не ждёт, берёт последнюю известную версию.
                Task { try? await dependencies.rules.refresh() }
                // Забег, продолженный после перезапуска в фоне, мог остаться без Live Activity: в фоне её не запустить.
                RunController.shared.becameActive()
                ProbeRun.shared.becameActive()
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
                    if event == .connected {
                        // Подсказки «туман изменился» за время разрыва потеряны.
                        await AppDependencies.shared.fog?.invalidate()
                    }
                case .fogChanged:
                    // Сервер открыл туман по доставленному забегу: тайлы перезапросятся с версиями, когда карта их покажет,
                    // а «+N га» забега — в итог (docs/architecture/run-hud.md, «Итог забега»).
                    await AppDependencies.shared.fog?.invalidate()
                    await AppDependencies.shared.syncEngine()?.refreshFog()
                case .tilesChanged(let league, let tiles):
                    // Пометить тайлы: карта (этап 2) перезапросит их с известными версиями, когда покажет.
                    if league == .run {
                        await AppDependencies.shared.territory?.markChanged(tiles)
                    }
                }
            }
        }
    }
}

/// Вход и выход (`TokenStore.events`; подписчик у потока один — этот): соединение реального времени держит токен того,
/// кто вошёл, поэтому при смене входа оно закрывается и открывается заново. Корень приложения узнаёт о входе отсюда же
/// (`AppSession`): не вошёл — онбординг, вошёл — вкладки.
@MainActor
final class SessionRelay {
    static let shared = SessionRelay()

    private var started = false

    func start() {
        guard !started else { return }
        started = true
        let dependencies = AppDependencies.shared
        Task.detached {
            // Вход, сохранённый в Keychain с прошлого запуска, событием не приходит — прочитать его сразу.
            let saved = await dependencies.tokens.current()
            await AppSession.shared.update(saved)
            for await event in dependencies.tokens.events {
                // Экран — первым: вышедший игрок сразу видит онбординг, не дожидаясь сброса кэшей.
                let current = await dependencies.tokens.current()
                await AppSession.shared.update(current)
                await dependencies.realtime?.stop()
                // Видимые версии у каждого игрока свои (скрытые чужие захваты) — кэш земли прежнего входа не годится.
                await dependencies.territory?.reset()
                await dependencies.fog?.reset()  // туман — свой у каждого игрока
                guard case .signedIn = event else { continue }
                if await MainActor.run(body: { UIApplication.shared.applicationState == .active }) {
                    await dependencies.realtime?.start()
                }
                // Не ждать прохода (досылка очереди может идти долго): выход, пришедший во время него, должен сразу
                // закрыть соединение и сбросить кэши прежнего игрока.
                Task { await dependencies.syncScheduler()?.trigger(.signedIn) }
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
