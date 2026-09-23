import CoreMotion
import Foundation
import GameCore

/// Датчики движения и шагомер для античита (PLAN.md, §3.9): «транспорт», «велосипед», шаги.
/// Если датчики недоступны или запрещены, игра просто не получает этих подсказок — это «неизвестно», а не нарушение.
@MainActor
final class MotionFeed {
    private let activityManager = CMMotionActivityManager()
    private let pedometer = CMPedometer()

    func start(
        onActivity: @escaping @MainActor @Sendable (MotionSample) -> Void,
        onSteps: @escaping @MainActor @Sendable (PedometerSample) -> Void
    ) {
        if CMMotionActivityManager.isActivityAvailable() {
            activityManager.startActivityUpdates(to: .main) { activity in
                guard let activity else { return }
                let sample = MotionSample(
                    timestamp: activity.startDate.timeIntervalSince1970, activity: Self.kind(of: activity))
                Task { @MainActor in onActivity(sample) }
            }
        }

        if CMPedometer.isStepCountingAvailable() {
            let start = Date()
            // Шагомер отдаёт накопленное число шагов с начала; превращаем его в интервалы «шагов за отрезок времени».
            let counter = StepCounter(start: start.timeIntervalSince1970)
            pedometer.startUpdates(from: start) { data, _ in
                guard let data else { return }
                let steps = data.numberOfSteps.intValue
                let end = data.endDate.timeIntervalSince1970
                Task { @MainActor in
                    if let sample = counter.interval(totalSteps: steps, at: end) {
                        onSteps(sample)
                    }
                }
            }
        }
    }

    func stop() {
        activityManager.stopActivityUpdates()
        pedometer.stopUpdates()
    }

    private nonisolated static func kind(of activity: CMMotionActivity) -> MotionActivity {
        if activity.automotive { return .automotive }
        if activity.cycling { return .cycling }
        if activity.running { return .running }
        if activity.walking { return .walking }
        if activity.stationary { return .stationary }
        return .unknown
    }
}

/// Переводит накопленное число шагов в интервалы.
@MainActor
private final class StepCounter {
    private var lastTime: Double
    private var lastSteps = 0

    init(start: Double) {
        lastTime = start
    }

    func interval(totalSteps: Int, at time: Double) -> PedometerSample? {
        guard time > lastTime else { return nil }
        let sample = PedometerSample(start: lastTime, end: time, steps: max(0, totalSteps - lastSteps))
        lastTime = time
        lastSteps = totalSteps
        return sample
    }
}
