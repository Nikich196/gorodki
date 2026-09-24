import BackgroundTasks
import Sync
import Synchronization
import UIKit

/// Досылка очереди, когда приложение в фоне (PLAN.md, §4 п. 8 «Фоновый сервис»; docs/architecture/sync.md#в-фоне).
///
/// - Уходя в фон, приложение просит у iOS немного времени (`beginBackgroundTask`, около 30 секунд) и делает проход.
/// - По итогу прохода просит фоновые пробуждения (`BackgroundSyncPlan`): короткое (`BGAppRefreshTask`) и, если есть
///   недоставленные забеги, длинное с сетью (`BGProcessingTask`). Когда будить, решает iOS.
/// - Разбуженное приложение делает проход и просит пробуждения снова — пока очередь не опустеет.
///
/// Проход можно оборвать где угодно: состояние после каждого ответа сервера уже в базе. Поэтому истёкшее время ничего
/// не портит — следующий проход продолжит с того же места.
enum BackgroundSync {
    /// Идентификаторы — как в Info.plist (`BGTaskSchedulerPermittedIdentifiers`): иначе iOS не даст их зарегистрировать.
    static let refreshIdentifier = identifier("sync.refresh")
    static let uploadIdentifier = identifier("sync.upload")

    private static func identifier(_ suffix: String) -> String {
        "\(Bundle.main.bundleIdentifier ?? "gorodki").\(suffix)"
    }

    /// Обработчики фоновых задач. Только до конца запуска приложения (`application(_:didFinishLaunchingWithOptions:)`):
    /// iOS может запустить приложение ради задачи и отдать её сразу.
    static func register() {
        for identifier in [refreshIdentifier, uploadIdentifier] {
            _ = BGTaskScheduler.shared.register(forTaskWithIdentifier: identifier, using: nil) { task in
                run(task)
            }
        }
    }

    /// Приложение уходит в фон: дослать очередь, пока iOS даёт время, и попросить фоновые пробуждения.
    @MainActor
    static func enterBackground() {
        let time = ExtraTime()
        Task {
            await pass()
            time.end()
        }
    }

    /// Проход синхронизации и фоновые пробуждения по его итогу. Сервер не задан или никто не вошёл — пробуждения
    /// снимаются.
    private static func pass() async {
        guard let scheduler = await AppDependencies.shared.syncScheduler() else {
            schedule([])
            return
        }
        await scheduler.trigger(.backgroundTask)
        schedule(await scheduler.backgroundRequests)
    }

    private static func run(_ task: BGTask) {
        let completion = Completion(task)
        let work = Task {
            await pass()
            completion.finish(success: true)
        }
        task.expirationHandler = {
            work.cancel()
            completion.finish(success: false)
        }
    }

    /// Попросить у iOS эти пробуждения, остальные — снять. Новая заявка с тем же идентификатором заменяет прежнюю.
    private static func schedule(_ requests: [BackgroundRequest]) {
        let scheduler = BGTaskScheduler.shared
        let kinds: [(BackgroundRequest.Kind, String)] = [(.refresh, refreshIdentifier), (.upload, uploadIdentifier)]
        for (kind, identifier) in kinds {
            guard let request = requests.first(where: { $0.kind == kind }) else {
                scheduler.cancel(taskRequestWithIdentifier: identifier)
                continue
            }
            let taskRequest: BGTaskRequest
            switch kind {
            case .refresh:
                taskRequest = BGAppRefreshTaskRequest(identifier: identifier)
            case .upload:
                let processing = BGProcessingTaskRequest(identifier: identifier)
                processing.requiresNetworkConnectivity = true
                processing.requiresExternalPower = false
                taskRequest = processing
            }
            taskRequest.earliestBeginDate = Date.now.addingTimeInterval(Double(request.earliest.components.seconds))
            do {
                try scheduler.submit(taskRequest)
            } catch {
                // Симулятор, выключенное «Обновление контента» или лимит заявок: очередь дошлётся, когда приложение
                // откроют.
            }
        }
    }
}

/// Время, которое iOS даёт приложению, уходящему в фон. Вернуть его нужно ровно один раз — по концу прохода или когда
/// iOS скажет, что время вышло: иначе iOS завершит приложение.
@MainActor
private final class ExtraTime {
    private var identifier = UIBackgroundTaskIdentifier.invalid

    init() {
        identifier = UIApplication.shared.beginBackgroundTask(withName: "Городки: досылка очереди") { [weak self] in
            self?.end()
        }
    }

    func end() {
        guard identifier != .invalid else { return }
        UIApplication.shared.endBackgroundTask(identifier)
        identifier = .invalid
    }
}

/// Фоновую задачу iOS завершают ровно один раз — по концу прохода или по истечении времени. `BGTask` можно завершать
/// из любого потока, но `Sendable` он не объявлен.
private final class Completion: @unchecked Sendable {
    private let task: BGTask
    private let finished = Mutex(false)

    init(_ task: BGTask) {
        self.task = task
    }

    func finish(success: Bool) {
        let first = finished.withLock { finished in
            defer { finished = true }
            return !finished
        }
        if first {
            task.setTaskCompleted(success: success)
        }
    }
}
