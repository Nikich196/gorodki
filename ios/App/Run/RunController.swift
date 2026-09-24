import CoreLocation
import CoreMotion
import Foundation
import GameCore
import Observation
import Platform
import Sync

/// Забег в игре — то, что вызывает экран (этап 2): «Старт», «Финиш», снимок состояния, Live Activity
/// (PLAN.md §7.2 «Трекинг»; docs/architecture/sync.md#трекер-runtracker). Логика забега — в `RunTracker` (пакет Sync,
/// проверена тестами на Linux); здесь — источники iOS и склейка с зависимостями приложения.
@MainActor
@Observable
final class RunController {
    static let shared = RunController()

    /// Почему «Старт» не состоялся — экран объясняет игроку, что сделать.
    enum StartProblem: Error, Equatable {
        /// Сервер не задан или никто не вошёл.
        case notSignedIn
        /// Нет разрешения на геопозицию «При использовании».
        case locationNotAllowed
        /// Включена приблизительная геопозиция: точки не пройдут судью, и забег сожжёт лимит забегов впустую.
        case reducedAccuracy
    }

    private(set) var state = TrackerState()

    @ObservationIgnored private let tracker: RunTracker
    @ObservationIgnored private let location = RunLocationSource()
    @ObservationIgnored private let motion = RunMotionSource()
    @ObservationIgnored private var ticker: Task<Void, Never>?
    @ObservationIgnored private var activityID: String?
    @ObservationIgnored private var lastActivityUpdate = Date.distantPast

    /// Подсказка «забег мог идти», чтобы после перезапуска сразу, ещё до чтения базы, запустить геопозицию (иначе iOS
    /// может снова усыпить приложение). Решает база (`RunTracker.recover`), подсказка — только ускоряет.
    private static let hintKey = "run.maybeTracking"

    private init() {
        tracker = RunTracker(
            onQueued: {
                // Проход не ждём: расписание склеит частые подсказки в один проход.
                Task { await AppDependencies.shared.syncScheduler()?.trigger(.recorded) }
            },
            onChange: { state in
                Task { @MainActor in RunController.shared.apply(state) }
            })
    }

    // MARK: - Экран

    /// «Старт». Разрешения проверяются до записи забега: пустой забег съел бы суточный лимит (10 забегов).
    func start(league: League) async throws {
        let manager = CLLocationManager()
        guard [.authorizedWhenInUse, .authorizedAlways].contains(manager.authorizationStatus) else {
            throw StartProblem.locationNotAllowed
        }
        guard manager.accuracyAuthorization == .fullAccuracy else { throw StartProblem.reducedAccuracy }
        let motionAuthorized = CMMotionActivityManager.authorizationStatus() == .authorized
        let startedAt = Date.now
        // Источники — сразу: точки до записи забега копятся в очереди трекера и уходят в него (GPS «догоняет»).
        startSources(motionSince: startedAt)
        do {
            try await tracker.start {
                guard
                    let session = try await AppDependencies.shared.startRun(
                        league: league, motionAuthorized: motionAuthorized)
                else { throw StartProblem.notSignedIn }
                return session
            }
        } catch {
            stopSources()
            await tracker.flush()
            throw error
        }
        UserDefaults.standard.set(true, forKey: Self.hintKey)
        startLiveActivity(startedAt: startedAt)
    }

    /// «Финиш». Точки, пришедшие раньше, войдут в забег.
    /// - Throws: ошибку записи в очередь — забег продолжается, «Финиш» можно повторить.
    func finish() async throws {
        try await tracker.finish(at: Date.now.timeIntervalSince1970)
        ended()
    }

    /// Выход из аккаунта: сначала завершить забег (он принадлежит этому игроку), потом стереть вход. Событие о смене
    /// входа приходит уже после стирания — поэтому завершать надо здесь, в самом действии выхода.
    func signOut() async {
        if state.isRunning {
            try? await finish()
        }
        await AppDependencies.shared.signIn?.signOut()
    }

    // MARK: - Перезапуск

    /// При запуске приложения (`AppDelegate`), синхронно: если забег мог идти — сразу геопозиция, потом забег из базы.
    func resumeAtLaunch() {
        let maybeTracking = UserDefaults.standard.bool(forKey: Self.hintKey)
        if maybeTracking {
            location.start(sending: tracker)
        }
        Task { await recover() }
    }

    private func recover() async {
        let dependencies = AppDependencies.shared
        let deviceId = await dependencies.installation.value()
        let playerId = await dependencies.tokens.current()?.playerId
        let rules = dependencies.rules
        let session = try? await RunTracker.recover(
            store: dependencies.syncStore, deviceId: deviceId, signedIn: playerId,
            now: Date.now.timeIntervalSince1970, rules: { await rules.rules(version: $0)?.rules })
        guard let session else {
            // Продолжать нечего (прерванные забеги закрыты): накопленное геопозицией выбросить.
            stopSources()
            await tracker.flush()
            UserDefaults.standard.set(false, forKey: Self.hintKey)
            return
        }
        let since = Date(timeIntervalSince1970: await session.acceptsSensorsAfter)
        try? await tracker.resume(session)
        startSources(motionSince: since)
        activityID = RunActivityController.currentActivityID
        UserDefaults.standard.set(true, forKey: Self.hintKey)
    }

    // MARK: - Источники

    private func startSources(motionSince: Date) {
        location.start(sending: tracker)
        motion.start(sending: tracker, since: motionSince)
        guard ticker == nil else { return }
        let tracker = self.tracker
        ticker = Task {
            while !Task.isCancelled {
                try? await Task.sleep(for: RunTracker.tickInterval)
                tracker.send(.tick(now: Date.now.timeIntervalSince1970))
            }
        }
    }

    private func stopSources() {
        location.stop()
        motion.stop()
        ticker?.cancel()
        ticker = nil
    }

    private func apply(_ fresh: TrackerState) {
        let wasRunning = state.isRunning
        state = fresh
        if wasRunning, !fresh.isRunning {
            ended()  // в том числе конец по пределу длины
        } else if fresh.isRunning {
            updateLiveActivity(fresh.stats)
        }
    }

    private func ended() {
        stopSources()
        UserDefaults.standard.set(false, forKey: Self.hintKey)
        if let activityID {
            Task { await RunActivityController.end(id: activityID) }
        }
        activityID = nil
    }

    // MARK: - Live Activity

    private func startLiveActivity(startedAt: Date) {
        guard RunActivityController.areActivitiesEnabled else { return }
        activityID = try? RunActivityController.start(
            startedAt: startedAt, state: RunActivityAttributes.ContentState(title: "Забег", detail: "Ждём GPS…"))
    }

    /// Не чаще раза в 5 секунд: чаще система всё равно не покажет.
    private func updateLiveActivity(_ stats: RunStats) {
        guard let activityID, Date.now.timeIntervalSince(lastActivityUpdate) >= 5 else { return }
        lastActivityUpdate = .now
        let state = RunActivityAttributes.ContentState(
            title: "Забег · \(String(format: "%.2f", stats.distanceMeters / 1_000)) км",
            detail: "Петель \(stats.loops) · туман \(String(format: "%.2f", stats.fogAreaSquareMeters / 10_000)) га")
        Task { await RunActivityController.update(id: activityID, state: state) }
    }
}
