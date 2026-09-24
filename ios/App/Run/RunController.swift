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
    /// Идёт демо-повтор — экран показывает значок «ПОВТОР ×N».
    private(set) var replaySpeed: Double?
    /// Есть сохранённая запись для демо-повтора.
    private(set) var hasDemoRecording = RunController.loadDemoRecording() != nil
    /// Разрешения «Движение и фитнес» нет: забег пишется, туман открывается, но захваты сервер не засчитает
    /// (`motion_not_authorized`) — экран предупреждает.
    private(set) var capturesNeedMotion = false

    @ObservationIgnored private let tracker: RunTracker
    @ObservationIgnored private let location = RunLocationSource()
    @ObservationIgnored private let motion = RunMotionSource()
    @ObservationIgnored private var ticker: Task<Void, Never>?
    @ObservationIgnored private var activityID: String?
    @ObservationIgnored private var lastActivityUpdate = Date.distantPast
    @ObservationIgnored private var replay: Task<Void, Never>?
    /// Идёт «Старт», повтор или продолжение после перезапуска: второй «Старт» до этого не запускает источники — иначе
    /// его отказ остановил бы геопозицию и датчики уже идущего забега.
    @ObservationIgnored private var busy = false

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
    /// - Parameter recordDemo: записать забег для демо-повтора (запись сохраняется после «Финиша»).
    func start(league: League, recordDemo: Bool = false) async throws {
        guard !busy else { throw TrackerError.alreadyRunning }
        busy = true
        defer { busy = false }
        guard await !tracker.state.isRunning else { throw TrackerError.alreadyRunning }
        let manager = CLLocationManager()
        guard [.authorizedWhenInUse, .authorizedAlways].contains(manager.authorizationStatus) else {
            throw StartProblem.locationNotAllowed
        }
        guard manager.accuracyAuthorization == .fullAccuracy else { throw StartProblem.reducedAccuracy }
        // До записи забега: `motionAuthorized` в забеге не меняется, а без него сервер отклоняет каждую заявку.
        let motionAuthorized = await Self.motionAuthorization()
        capturesNeedMotion = !motionAuthorized
        let startedAt = Date.now
        // Источники — сразу: поступившее, пока забег записывается, трекер копит и отдаёт в него (GPS «догоняет»,
        // а первая запись CoreMotion о текущем виде движения приходит сразу и может не повториться).
        startSources(motionSince: startedAt)
        do {
            try await tracker.start(recording: recordDemo) {
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

    /// Приблизительная геопозиция (`StartProblem.reducedAccuracy`): попросить точную на время забега — системный запрос
    /// с объяснением из Info.plist (`NSLocationTemporaryUsageDescriptionDictionary`, ключ `RunTracking`).
    /// - Returns: точная геопозиция теперь есть.
    func requestFullAccuracy() async -> Bool {
        let manager = CLLocationManager()
        guard manager.accuracyAuthorization != .fullAccuracy else { return true }
        try? await manager.requestTemporaryFullAccuracyAuthorization(withPurposeKey: "RunTracking")
        return manager.accuracyAuthorization == .fullAccuracy
    }

    /// «Финиш». Точки, пришедшие раньше, войдут в забег.
    /// - Throws: ошибку записи в очередь — забег продолжается, «Финиш» можно повторить.
    func finish() async throws {
        if let replay {
            // Повтор сам заканчивает забег — на достигнутом времени записи, а не на настоящем «сейчас».
            replay.cancel()
            await replay.value
            ended()
            return
        }
        guard await tracker.state.isRunning else { return }  // забег ещё начинается или уже закончен
        try await tracker.finish(at: Date.now.timeIntervalSince1970)
        ended()
    }

    /// Разрешение «Движение и фитнес». Не спрошено — системный запрос сейчас: ответ нужен до записи забега.
    private static func motionAuthorization() async -> Bool {
        switch CMMotionActivityManager.authorizationStatus() {
        case .authorized:
            return true
        case .notDetermined:
            guard CMMotionActivityManager.isActivityAvailable() else { return false }
            await requestMotionAuthorization()
            return CMMotionActivityManager.authorizationStatus() == .authorized
        default:
            return false
        }
    }

    /// Запрос истории вида движения показывает системный вопрос о разрешении; обработчик — после ответа. Вне главного
    /// актора: CoreMotion вызывает обработчик на своей очереди (см. `RunMotionSource`).
    private nonisolated static func requestMotionAuthorization() async {
        let manager = CMMotionActivityManager()
        let now = Date()
        await withCheckedContinuation { continuation in
            manager.queryActivityStarting(from: now.addingTimeInterval(-60), to: now, to: OperationQueue()) { _, _ in
                withExtendedLifetime(manager) {}  // менеджер жив, пока не пришёл ответ
                continuation.resume()
            }
        }
    }

    // MARK: - Демо-повтор

    /// Демо-повтор сохранённой записи (PLAN.md §7.2): ×`speed`, время сдвинуто в прошлое, `source = replay`. Сервер
    /// разрешает его только ролям `demo` и `admin` (403 `replay_forbidden` — забег отвергнут, очередь не ломается).
    /// Геопозиция и датчики не запускаются: всё идёт из записи, таймер — по её времени.
    func startReplay(speed: Double = 20) async throws {
        guard !busy else { throw TrackerError.alreadyRunning }
        busy = true
        defer { busy = false }
        guard let recording = Self.loadDemoRecording() else { return }
        let replay = RunReplay(recording, speed: speed)
        let startedAt = replay.startedAt(now: Date.now.timeIntervalSince1970)
        try await tracker.start {
            guard
                let session = try await AppDependencies.shared.startRun(
                    league: recording.league, motionAuthorized: recording.hasMotion, source: .replay,
                    startedAt: startedAt)
            else { throw StartProblem.notSignedIn }
            return session
        }
        replaySpeed = replay.speed
        let tracker = self.tracker
        self.replay = Task {
            try? await replay.play(into: tracker, startedAt: startedAt)
        }
    }

    /// Остановить повтор: забег заканчивается на достигнутом времени записи.
    func stopReplay() {
        replay?.cancel()
    }

    private static var demoRecordingURL: URL? {
        try? FileManager.default.url(
            for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true
        )
        .appendingPathComponent("demo-recording.json")
    }

    /// Запись для повтора — одна, последняя; только на телефоне (настоящий маршрут в репозиторий не попадает).
    private static func loadDemoRecording() -> RunRecording? {
        guard let url = demoRecordingURL, let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(RunRecording.self, from: data)
    }

    private func saveDemoRecording(_ recording: RunRecording) {
        guard let url = Self.demoRecordingURL, let data = try? JSONEncoder().encode(recording) else { return }
        // Не `completeFileProtection`: забег может закончиться на заблокированном телефоне (предел длины в кармане),
        // и запись молча не сохранилась бы.
        try? data.write(to: url, options: [.atomic, .completeFileProtectionUntilFirstUserAuthentication])
        hasDemoRecording = true
    }

    /// Выход из аккаунта: сначала завершить забег (он принадлежит этому игроку), потом стереть вход. Событие о смене
    /// входа приходит уже после стирания — поэтому завершать надо здесь, в самом действии выхода.
    /// - Throws: ошибку «Финиша» — тогда вход остаётся: иначе забег остался бы без хозяина до следующего входа.
    func signOut() async throws {
        if await tracker.state.isRunning {
            try await finish()
        }
        await AppDependencies.shared.signIn?.signOut()
    }

    // MARK: - Перезапуск

    /// При запуске приложения (`AppDelegate`), синхронно: если забег мог идти — сразу геопозиция, потом забег из базы.
    func resumeAtLaunch() {
        busy = true  // «Старт» подождёт: продолжение закрывает прерванные забеги, и новый оно закрыло бы тоже
        let maybeTracking = UserDefaults.standard.bool(forKey: Self.hintKey)
        if maybeTracking {
            location.start(sending: tracker)
        }
        Task { await recover() }
    }

    private func recover() async {
        defer { busy = false }
        let dependencies = AppDependencies.shared
        let deviceId = await dependencies.installation.value()
        let playerId = await dependencies.tokens.current()?.playerId
        let rules = dependencies.rules
        let session = try? await RunTracker.recover(
            store: dependencies.syncStore, deviceId: deviceId, signedIn: playerId,
            now: Date.now.timeIntervalSince1970, rules: { await rules.rules(version: $0)?.rules })
        guard let session else {
            // Продолжать нечего (прерванные забеги закрыты): накопленное геопозицией выбросить, Live Activity
            // прежнего процесса — закрыть.
            stopSources()
            await tracker.flush()
            UserDefaults.standard.set(false, forKey: Self.hintKey)
            if let leftover = RunActivityController.currentActivityID {
                await RunActivityController.end(id: leftover)
            }
            return
        }
        let since = Date(timeIntervalSince1970: await session.sensorsResumeFrom)
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
        replay = nil
        replaySpeed = nil
        Task {
            if let recording = await tracker.takeRecording() {
                saveDemoRecording(recording)
            }
        }
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
