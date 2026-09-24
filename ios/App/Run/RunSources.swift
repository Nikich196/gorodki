import CoreLocation
import CoreMotion
import Foundation
import GameCore
import Sync
import Synchronization

/// Геопозиция забега — способ A спайка S1: `CLLocationUpdate.liveUpdates(.fitness)` и сессия фоновой активности
/// (синяя плашка; разрешения «При использовании» достаточно, PLAN.md §6.6). Точки уходят в трекер сразу по получении —
/// со временем получения: по нему судья решает, не устарела ли точка.
@MainActor
final class RunLocationSource {
    private var task: Task<Void, Never>?
    private var serviceSession: CLServiceSession?
    private var backgroundSession: CLBackgroundActivitySession?

    var isRunning: Bool { task != nil }

    func start(sending tracker: RunTracker) {
        guard task == nil else { return }
        serviceSession = CLServiceSession(authorization: .whenInUse)
        backgroundSession = CLBackgroundActivitySession()
        task = Task.detached {
            do {
                for try await update in CLLocationUpdate.liveUpdates(.fitness) {
                    guard let location = update.location else { continue }
                    tracker.send(.fix(Self.fix(location), receivedAt: Date.now.timeIntervalSince1970))
                }
            } catch {
                // Поток обновлений закончился (остановка) — делать нечего.
            }
        }
    }

    func stop() {
        task?.cancel()
        task = nil
        backgroundSession?.invalidate()
        backgroundSession = nil
        serviceSession?.invalidate()
        serviceSession = nil
    }

    /// Точка CoreLocation → точка забега. Неверные (точность −1) отбросит сам забег — до нумерации.
    nonisolated static func fix(_ location: CLLocation) -> Sync.LocationFix {
        var source: PointSource = []
        if let information = location.sourceInformation {
            if information.isSimulatedBySoftware { source.insert(.simulated) }
            if information.isProducedByAccessory { source.insert(.accessory) }
        }
        return Sync.LocationFix(
            coordinate: Coordinate(latitude: location.coordinate.latitude, longitude: location.coordinate.longitude),
            timestamp: location.timestamp.timeIntervalSince1970, horizontalAccuracy: location.horizontalAccuracy,
            speed: location.speed >= 0 ? location.speed : nil, source: source)
    }
}

/// Датчики движения забега: вид движения (CoreMotion) и шаги (шагомер) — для судьи и сервера (PLAN.md §3.9). Записи
/// уходят в трекер прямо из обработчиков, на одной последовательной очереди: порядок не теряется (через `Task {}` он
/// не гарантирован). Датчики недоступны или запрещены — это «неизвестно», а не нарушение.
@MainActor
final class RunMotionSource {
    private let activity = CMMotionActivityManager()
    private let pedometer = CMPedometer()
    private let queue: OperationQueue = {
        let queue = OperationQueue()
        queue.maxConcurrentOperationCount = 1
        queue.name = "Городки: датчики забега"
        return queue
    }()
    /// Остановлено ли включение: история CoreMotion приходит позже, и без отметки живые обновления включились бы уже
    /// после `stop()`.
    private var stopped = StopFlag()

    /// - Parameter since: с какого момента нужны записи. После перезапуска — с отметки уже отправленных датчиков:
    ///   за время выгрузки CoreMotion отдаёт историю.
    func start(sending tracker: RunTracker, since: Date) {
        stopped = StopFlag()
        if CMMotionActivityManager.isActivityAvailable() {
            Self.startActivity(activity, queue: queue, since: since, tracker: tracker, stopped: stopped)
        }
        if CMPedometer.isStepCountingAvailable() {
            Self.startSteps(pedometer, queue: queue, since: since, tracker: tracker)
        }
    }

    // Обработчики CoreMotion вызываются на фоновой очереди. Замыкание, созданное в коде главного актора, в Swift 6
    // наследует его изоляцию, и вызов не с главной очереди обрывает приложение, — поэтому подписка в `nonisolated`.

    /// Сначала история (после перезапуска — за время выгрузки), потом живые обновления: порядок сохраняется.
    private nonisolated static func startActivity(
        _ activity: CMMotionActivityManager, queue: OperationQueue, since: Date, tracker: RunTracker,
        stopped: StopFlag
    ) {
        activity.queryActivityStarting(from: since, to: .now, to: queue) { history, _ in
            guard !stopped.isSet else { return }
            for item in history ?? [] {
                tracker.send(.motion(sample(item)))
            }
            activity.startActivityUpdates(to: queue) { item in
                guard let item else { return }
                tracker.send(.motion(sample(item)))
            }
        }
    }

    /// Шагомер отдаёт накопленное число шагов с `since` — оно переводится в интервалы «шаги за отрезок».
    private nonisolated static func startSteps(
        _ pedometer: CMPedometer, queue: OperationQueue, since: Date, tracker: RunTracker
    ) {
        let intervals = StepIntervals(start: since.timeIntervalSince1970)
        pedometer.startUpdates(from: since) { data, _ in
            guard let data else { return }
            let steps = data.numberOfSteps.intValue
            let end = data.endDate.timeIntervalSince1970
            queue.addOperation {
                if let sample = intervals.next(totalSteps: steps, at: end) {
                    tracker.send(.steps(sample))
                }
            }
        }
    }

    func stop() {
        stopped.set()
        activity.stopActivityUpdates()
        pedometer.stopUpdates()
    }

    nonisolated static func sample(_ activity: CMMotionActivity) -> MotionSample {
        let kind: MotionActivity =
            if activity.automotive { .automotive } else if activity.cycling { .cycling } else if activity.running {
                .running
            } else if activity.walking { .walking } else if activity.stationary { .stationary } else { .unknown }
        return MotionSample(timestamp: activity.startDate.timeIntervalSince1970, activity: kind)
    }
}

/// Отметка «остановлено» — читается на очереди CoreMotion.
private final class StopFlag: Sendable {
    private let value = Mutex(false)
    var isSet: Bool { value.withLock { $0 } }
    func set() { value.withLock { $0 = true } }
}

/// Накопленное число шагов → интервалы «шаги за отрезок».
private final class StepIntervals: Sendable {
    private let last: Mutex<(time: Double, steps: Int)>

    init(start: Double) {
        last = Mutex((start, 0))
    }

    func next(totalSteps: Int, at time: Double) -> PedometerSample? {
        last.withLock { last in
            guard time > last.time else { return nil }
            let sample = PedometerSample(start: last.time, end: time, steps: max(0, totalSteps - last.steps))
            last = (time, totalSteps)
            return sample
        }
    }
}
