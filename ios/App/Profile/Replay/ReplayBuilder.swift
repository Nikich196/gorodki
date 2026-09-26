import BackgroundTasks
import Foundation
import GameCore
import Synchronization

/// Как собирается видео-повтор.
enum ReplayBuildMode: Equatable, Sendable {
    /// Фоновая задача iOS 26 (`BGContinuedProcessingTask`, пункт 8 листика): прогресс — в системной плашке, сборка
    /// продолжается, даже если свернуть приложение.
    case continuedProcessing
    /// Обычная задача с прогрессом на экране: система не приняла фоновую задачу (симулятор, лимит, отказ).
    case foreground(reason: String)
}

/// Сборка ролика: в приложении — `ReplayVideoWriter` в фоновой задаче, в фикстурах — ничего.
@MainActor
protocol ReplayBuilding {
    /// Собрать MP4. `progress` — доля 0…1 (не с главного потока). `mode` — как пошла сборка, сразу после старта.
    func build(
        coordinates: [Coordinate], style: ReplayStyle, to url: URL,
        mode: @escaping @MainActor @Sendable (ReplayBuildMode) -> Void,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws
}

/// Фоновый сервис — iOS-аналог (пункт 8 листика): «Собрать видео-повтор» — `BGContinuedProcessingTask`, начатая
/// игроком работа с системным прогрессом (плашка Live Activity), которую iOS не прерывает при сворачивании приложения.
/// Идентификатор — из Info.plist (`BGTaskSchedulerPermittedIdentifiers`, `….replay.build`), регистрация — один раз,
/// при первой сборке (задачи этого вида регистрируются и после запуска). Не приняла система задачу — та же сборка
/// обычной задачей, прогресс — на экране.
struct ContinuedReplayBuilder: ReplayBuilding {
    func build(
        coordinates: [Coordinate], style: ReplayStyle, to url: URL,
        mode: @escaping @MainActor @Sendable (ReplayBuildMode) -> Void,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws {
        let job = ReplayJob(coordinates: coordinates, style: style, url: url, progress: progress)
        switch ReplayTaskRelay.shared.submit(job) {
        case .submitted:
            mode(.continuedProcessing)
            // Страховка: заявку приняли, но iOS так и не запустила задачу — через 5 секунд собрать самим.
            let watchdog = Task { @MainActor in
                try? await Task.sleep(for: .seconds(5))
                guard !Task.isCancelled, ReplayTaskRelay.shared.reclaim(job) else { return }
                mode(.foreground(reason: "iOS не запустила фоновую задачу"))
                do {
                    try await ReplayVideoWriter.write(
                        coordinates: coordinates, style: style, to: url, progress: progress)
                    job.finish(.success(()))
                } catch {
                    job.finish(.failure(error))
                }
            }
            defer { watchdog.cancel() }
            try await job.result()
        case .rejected(let reason):
            mode(.foreground(reason: reason))
            try await ReplayVideoWriter.write(coordinates: coordinates, style: style, to: url, progress: progress)
        }
    }
}

/// Работа сборки: что собрать и куда сообщить итог. Итог ждёт экран (`result()`), а выполняет — фоновая задача.
final class ReplayJob: Sendable {
    let coordinates: [Coordinate]
    let style: ReplayStyle
    let url: URL
    let progress: @Sendable (Double) -> Void
    private let state = Mutex<State>(State())

    private struct State {
        var outcome: Result<Void, any Error>?
        var waiter: CheckedContinuation<Void, any Error>?
    }

    init(coordinates: [Coordinate], style: ReplayStyle, url: URL, progress: @escaping @Sendable (Double) -> Void) {
        self.coordinates = coordinates
        self.style = style
        self.url = url
        self.progress = progress
    }

    func finish(_ outcome: Result<Void, any Error>) {
        let waiter = state.withLock { state -> CheckedContinuation<Void, any Error>? in
            guard state.outcome == nil else { return nil }
            state.outcome = outcome
            defer { state.waiter = nil }
            return state.waiter
        }
        waiter?.resume(with: outcome)
    }

    func result() async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
            let ready = state.withLock { state -> Result<Void, any Error>? in
                if state.outcome == nil { state.waiter = continuation }
                return state.outcome
            }
            if let ready {
                continuation.resume(with: ready)
            }
        }
    }
}

/// Регистрация и заявка фоновой задачи сборки; работа, ждущая запуска, — одна за раз.
final class ReplayTaskRelay: Sendable {
    static let shared = ReplayTaskRelay()
    /// Идентификатор задачи из Info.plist (`BGTaskSchedulerPermittedIdentifiers`): других iOS не примет.
    static let identifier = BackgroundTaskIdentifiers.pick(
        "replay.build",
        permitted: Bundle.main.object(forInfoDictionaryKey: BackgroundTaskIdentifiers.infoPlistKey) as? [String],
        bundleIdentifier: Bundle.main.bundleIdentifier)

    enum Submission: Equatable {
        case submitted
        case rejected(String)
    }

    private struct State {
        var registered: Bool?
        var pending: ReplayJob?
    }

    private let state = Mutex(State())

    func submit(_ job: ReplayJob) -> Submission {
        guard register() else { return .rejected("iOS не приняла фоновую задачу") }
        let busy = state.withLock { state -> Bool in
            guard state.pending == nil else { return true }
            state.pending = job
            return false
        }
        guard !busy else { return .rejected("видео уже собирается") }
        let request = BGContinuedProcessingTaskRequest(
            identifier: Self.identifier, title: "Видео-повтор забега", subtitle: "Собираю кадры…")
        // Не ждать в очереди: игрок нажал кнопку и смотрит на экран — нельзя сразу, значит, соберём сами.
        request.strategy = .fail
        do {
            try BGTaskScheduler.shared.submit(request)
            return .submitted
        } catch {
            state.withLock { $0.pending = nil }
            return .rejected(error.localizedDescription)
        }
    }

    /// Забрать работу, которую iOS так и не запустила, и снять заявку. `false` — задача уже идёт.
    func reclaim(_ job: ReplayJob) -> Bool {
        let taken = state.withLock { state -> Bool in
            guard state.pending === job else { return false }
            state.pending = nil
            return true
        }
        if taken {
            BGTaskScheduler.shared.cancel(taskRequestWithIdentifier: Self.identifier)
        }
        return taken
    }

    /// Обработчик — один раз за запуск: повторная регистрация того же идентификатора обрывает приложение.
    private func register() -> Bool {
        if let registered = state.withLock({ $0.registered }) { return registered }
        let registered = BGTaskScheduler.shared.register(
            forTaskWithIdentifier: Self.identifier, using: nil
        ) { [self] task in
            run(task)
        }
        state.withLock { $0.registered = registered }
        return registered
    }

    private func run(_ task: BGTask) {
        guard let task = task as? BGContinuedProcessingTask,
            let job = state.withLock({ state -> ReplayJob? in
                defer { state.pending = nil }
                return state.pending
            })
        else {
            task.setTaskCompleted(success: false)
            return
        }
        let handle = TaskHandle(task)
        handle.setTotal(100)
        let work = Task {
            do {
                try await ReplayVideoWriter.write(
                    coordinates: job.coordinates, style: job.style, to: job.url
                ) { fraction in
                    job.progress(fraction)
                    handle.report(fraction)
                }
                handle.complete(success: true)
                job.finish(.success(()))
            } catch {
                handle.complete(success: false)
                job.finish(.failure(error))
            }
        }
        task.expirationHandler = {
            // iOS отозвала время (игрок отменил в плашке или не хватило ресурсов): оборвать запись.
            work.cancel()
        }
    }
}

/// `BGContinuedProcessingTask` не объявлен `Sendable`, а прогресс ему сообщают с потока записи. Завершается ровно
/// один раз.
private final class TaskHandle: @unchecked Sendable {
    private let task: BGContinuedProcessingTask
    private let finished = Mutex(false)

    init(_ task: BGContinuedProcessingTask) {
        self.task = task
    }

    func setTotal(_ units: Int64) {
        task.progress.totalUnitCount = units
    }

    func report(_ fraction: Double) {
        task.progress.completedUnitCount = Int64((fraction * 100).rounded())
    }

    func complete(success: Bool) {
        let first = finished.withLock { done in
            defer { done = true }
            return !done
        }
        if first {
            task.setTaskCompleted(success: success)
        }
    }
}
