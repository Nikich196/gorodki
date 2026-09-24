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
    /// Идентификаторы — из Info.plist (`BGTaskSchedulerPermittedIdentifiers`): других iOS не даст зарегистрировать.
    static let refreshIdentifier = identifier("sync.refresh")
    static let uploadIdentifier = identifier("sync.upload")

    private static func identifier(_ suffix: String) -> String {
        BackgroundTaskIdentifiers.pick(
            suffix,
            permitted: Bundle.main.object(forInfoDictionaryKey: BackgroundTaskIdentifiers.infoPlistKey) as? [String],
            bundleIdentifier: Bundle.main.bundleIdentifier)
    }

    /// Как iOS приняла фоновые задачи — для «Проверки установки»: отказ иначе ничем не виден, досылка просто не идёт.
    struct Status: Equatable, Sendable {
        /// Идентификаторы, которые iOS не дала зарегистрировать.
        var rejected: [String] = []
        /// Ошибка последней заявки по идентификатору; принятая или снятая заявка ошибку своего идентификатора стирает.
        var submitErrors: [String: String] = [:]
    }

    static var status: Status { statusStore.value.withLock { $0 } }
    private static let statusStore = StatusStore()

    /// Обработчики фоновых задач. Только до конца запуска приложения (`application(_:didFinishLaunchingWithOptions:)`):
    /// iOS может запустить приложение ради задачи и отдать её сразу.
    static func register() {
        var rejected: [String] = []
        for identifier in [refreshIdentifier, uploadIdentifier] {
            let registered = BGTaskScheduler.shared.register(forTaskWithIdentifier: identifier, using: nil) { task in
                run(task)
            }
            if !registered {
                rejected.append(identifier)
            }
        }
        let result = rejected
        statusStore.value.withLock { $0.rejected = result }
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
    /// снимаются. Итога нет (проход не дошёл до конца) — прежние заявки остаются.
    private static func pass() async {
        guard let scheduler = await AppDependencies.shared.syncScheduler() else {
            schedule([])
            return
        }
        await scheduler.trigger(.backgroundTask)
        if let requests = await scheduler.backgroundRequests {
            schedule(requests)
        }
    }

    private static func run(_ task: BGTask) {
        // Разбудившая заявка уже израсходована. Если iOS оборвёт задачу раньше итога прохода, без новой заявки
        // приложение больше не проснётся, — поэтому сразу такая же, её заменит итог прохода.
        resubmit(task.identifier)
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

    private static func resubmit(_ identifier: String) {
        let kind: BackgroundRequest.Kind = identifier == uploadIdentifier ? .upload : .refresh
        submit(BackgroundRequest(kind, after: BackgroundSyncPlan.minimumDelay), as: identifier)
    }

    /// Попросить у iOS эти пробуждения, остальные — снять. Новая заявка с тем же идентификатором заменяет прежнюю.
    private static func schedule(_ requests: [BackgroundRequest]) {
        let scheduler = BGTaskScheduler.shared
        let kinds: [(BackgroundRequest.Kind, String)] = [(.refresh, refreshIdentifier), (.upload, uploadIdentifier)]
        for (kind, identifier) in kinds {
            guard let request = requests.first(where: { $0.kind == kind }) else {
                scheduler.cancel(taskRequestWithIdentifier: identifier)
                // Заявки больше нет — нет и её ошибки: иначе «Проверка установки» предупреждала бы о ней, пока тот же
                // идентификатор не заявят снова, а с пустой очередью или без входа его не заявляют.
                statusStore.value.withLock { $0.submitErrors[identifier] = nil }
                continue
            }
            submit(request, as: identifier)
        }
    }

    private static func submit(_ request: BackgroundRequest, as identifier: String) {
        let taskRequest: BGTaskRequest
        switch request.kind {
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
            try BGTaskScheduler.shared.submit(taskRequest)
            statusStore.value.withLock { $0.submitErrors[identifier] = nil }
        } catch {
            // Симулятор, выключенное «Обновление контента», лимит заявок или идентификатор не из Info.plist: очередь
            // дошлётся, когда приложение откроют. Причину покажет «Проверка установки».
            let message = error.localizedDescription
            statusStore.value.withLock { $0.submitErrors[identifier] = message }
        }
    }
}

/// Итог регистрации и заявок: пишут фоновые задачи с любых потоков, читает экран. Mutex нельзя копировать — поэтому
/// в классе.
private final class StatusStore: Sendable {
    let value = Mutex(BackgroundSync.Status())
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
