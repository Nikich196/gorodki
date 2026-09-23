import Network
import SwiftUI
import Synchronization

/// Точка входа приложения «Городки».
@main
struct GorodkiApp: App {
    @UIApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        WindowGroup {
            RootView()
                .task { NetworkWatcher.shared.start() }
        }
        .onChange(of: scenePhase) { _, phase in
            // Приложение на переднем плане — дослать очередь и сбросить шаг повторов (docs/architecture/sync.md).
            guard phase == .active else { return }
            Task { await AppDependencies.shared.syncScheduler()?.trigger(.appActive) }
        }
    }
}

/// Сеть вернулась — дослать очередь сразу, не дожидаясь таймера повторов.
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
                Task { await AppDependencies.shared.syncScheduler()?.trigger(.networkRestored) }
            }
        }
        monitor.start(queue: DispatchQueue(label: "gorodki.network"))
    }

    /// Была ли сеть в прошлый раз. Mutex нельзя копировать — обработчик держит его через ссылку.
    private final class OnlineFlag: Sendable {
        let state = Mutex(false)
    }
}
