import Foundation
import GameCore
import Observation
import Platform
import Sync

/// Счёт пробного забега — только числа, без координат: сводку можно смело прислать в чат.
struct ProbeCounters: Codable, Equatable {
    /// Точки, принятые судьёй в след.
    var accepted = 0
    /// Точки, записанные в очередь, но отброшенные судьёй (точность, скорость, устаревшая…).
    var rejected = 0
    /// Замкнутые и заявленные петли.
    var loops = 0
    /// Клетки тумана, открытые на телефоне.
    var fogCells = 0
    var distanceMeters = 0.0

    init() {}

    init(_ stats: RunStats) {
        accepted = stats.acceptedPoints
        rejected = stats.points - stats.acceptedPoints
        loops = stats.loops
        fogCells = stats.fogCells
        distanceMeters = stats.distanceMeters
    }

    /// После перезапуска приложения забег считает заново (`RunSession.resume`) — счёт до перезапуска прибавляется.
    static func + (lhs: ProbeCounters, rhs: ProbeCounters) -> ProbeCounters {
        var sum = lhs
        sum.accepted += rhs.accepted
        sum.rejected += rhs.rejected
        sum.loops += rhs.loops
        sum.fogCells += rhs.fogCells
        sum.distanceMeters += rhs.distanceMeters
        return sum
    }
}

/// Пробный забег между запусками приложения (UserDefaults): какой забег, когда начат, счёт до перезапуска.
struct ProbeRecord: Codable, Equatable {
    var runId: UUID
    var startedAt: Date
    /// Счёт до последнего перезапуска приложения.
    var beforeRelaunch = ProbeCounters()
    /// Последний известный счёт — сохраняется раз в 5 секунд и в конце забега.
    var total = ProbeCounters()
    var relaunches = 0
}

/// «Лаборатория», пробный забег: настоящий путь забега без входа и без сервера (PLAN.md, §10, спайк S1). Источники
/// забега (`RunLocationSource`, `RunMotionSource`) → `RunTracker` → `RunSession` (судья, детектор петель, туман) →
/// `RunRecorder` → очередь в базе. Хозяин забега — `LocalRun.labOwnerId`: синхронизация его не видит, история забегов
/// не сохраняет. После перезапуска приложения забег продолжается, как настоящий (`RunTracker.recover(labProbe:)`).
///
/// Трекер на телефоне один за раз: пока идёт настоящий забег (`RunController`), пробный не начать, и наоборот.
@MainActor
@Observable
final class ProbeRun {
    static let shared = ProbeRun()

    private(set) var state = TrackerState()
    /// Последний пробный забег; `nil` — не было или стёрт.
    private(set) var record: ProbeRecord?
    /// Что забег записал в очередь (перечитывается, пока открыт экран).
    private(set) var queued: QueuedRunSummary?
    /// Пробных забегов в очереди.
    private(set) var storedRuns = 0
    /// Идёт «Начать», «Закончить», удаление или продолжение после перезапуска.
    private(set) var busy = false
    /// Что не получилось — экран показывает.
    private(set) var problem: String?

    var isRunning: Bool { state.isRunning }

    /// Счёт на экране: до перезапуска + текущий забег; забег не идёт — последний сохранённый.
    var counters: ProbeCounters {
        guard let record else { return ProbeCounters() }
        guard state.runId == record.runId else { return record.total }
        return record.beforeRelaunch + ProbeCounters(state.stats)
    }

    var summary: String { Self.summaryText(record, counters: counters, queued: queued) }

    @ObservationIgnored private let tracker: RunTracker
    @ObservationIgnored private let location = RunLocationSource()
    @ObservationIgnored private let motion = RunMotionSource()
    @ObservationIgnored private var ticker: Task<Void, Never>?
    @ObservationIgnored private var activityID: String?
    @ObservationIgnored private var lastSave = Date.distantPast
    @ObservationIgnored private var lastActivityUpdate = Date.distantPast

    private static let recordKey = "lab.probe.record"
    /// Подсказка «пробный забег мог идти» — как у настоящего (`RunController`): после перезапуска сразу запустить
    /// геопозицию, ещё до чтения базы. Решает база (`RunTracker.recover`).
    private static let hintKey = "lab.probe.maybeTracking"
    /// Своя плашка Live Activity — по сохранённому идентификатору (`LiveActivitySlot`).
    private static let activitySlot = LiveActivitySlot(key: "lab.probe.activityID")

    private init() {
        // Синхронизации нет: забег остаётся на телефоне.
        tracker = RunTracker(onChange: { state in
            Task { @MainActor in ProbeRun.shared.apply(state) }
        })
        record = Self.loadRecord()
    }

    // MARK: - Экран

    /// «Начать пробный забег»: разрешения — как у настоящего «Старта», затем забег в очередь.
    func start() async {
        guard !busy else { return }
        busy = true
        defer { busy = false }
        problem = nil
        guard await !tracker.state.isRunning else { return }
        guard await !RunController.shared.isOccupied() else {
            problem = "Идёт настоящий забег — пробный можно начать после его «Финиша»."
            return
        }
        guard await RunController.shared.requestLocationAuthorization() else {
            problem = "Геопозиция запрещена — разреши её в Настройках → Городки."
            return
        }
        guard await RunController.shared.requestFullAccuracy() else {
            problem = "Нужна точная геопозиция: приблизительные точки судья отбросит."
            return
        }
        let motionAuthorized = await RunController.motionAuthorization()
        let startedAt = Date.now
        // Как у настоящего: источники — сразу, поступившее, пока забег записывается, трекер копит и отдаёт в него.
        startSources(motionSince: startedAt)
        do {
            try await tracker.start {
                try await AppDependencies.shared.startProbeRun(league: .run, motionAuthorized: motionAuthorized)
            }
        } catch {
            stopSources()
            await tracker.flush()
            problem = "Забег не записался в очередь: \(error.localizedDescription)"
            return
        }
        guard let runId = await tracker.state.runId else { return }
        record = ProbeRecord(runId: runId, startedAt: startedAt)
        save()
        queued = nil
        UserDefaults.standard.set(true, forKey: Self.hintKey)
        startLiveActivity(startedAt: startedAt)
        if !motionAuthorized {
            problem = "Нет разрешения «Движение и фитнес»: забег идёт, но судья не видит вид движения и шаги."
        }
    }

    /// «Закончить пробный забег». Точки, пришедшие раньше, войдут в забег.
    func finish() async {
        guard !busy else { return }
        busy = true
        defer { busy = false }
        guard await tracker.state.isRunning else { return }
        do {
            try await tracker.finish(at: Date.now.timeIntervalSince1970)
        } catch {
            problem = "Конец забега не записался (\(error.localizedDescription)) — нажми «Закончить» ещё раз."
            return
        }
        problem = nil
        apply(await tracker.state)
    }

    /// «Удалить пробные забеги»: из очереди — с кусками и заявками; забеги игрока остаются. Идущий — сначала закончить.
    func removeProbeRuns() async {
        guard !busy else { return }
        busy = true
        defer { busy = false }
        guard await !tracker.state.isRunning else {
            problem = "Сначала закончи пробный забег."
            return
        }
        do {
            try await AppDependencies.shared.syncStore.removeLabRuns()
        } catch {
            problem = "Пробные забеги не удалились: \(error.localizedDescription)"
            return
        }
        record = nil
        save()
        queued = nil
        storedRuns = 0
        problem = nil
    }

    /// Пробный забег идёт, начинается, заканчивается или продолжается после перезапуска — настоящий «Старт» ждёт
    /// (`RunController`): трекер на телефоне один за раз.
    func isOccupied() async -> Bool {
        if busy { return true }
        return await tracker.state.isRunning
    }

    /// Пока экран открыт — раз в 5 секунд перечитать очередь: сколько там пробных забегов и что записал последний.
    func watchQueue() async {
        while !Task.isCancelled {
            await refreshQueue()
            try? await Task.sleep(for: .seconds(5))
        }
    }

    private func refreshQueue() async {
        let store = AppDependencies.shared.syncStore
        storedRuns = ((try? await store.runs()) ?? []).filter(\.isLabProbe).count
        if let runId = record?.runId {
            queued = try? await QueuedRunSummary.of(runId, in: store)
        } else {
            queued = nil
        }
    }

    // MARK: - Перезапуск

    /// При запуске приложения (`AppDelegate`), синхронно — как у настоящего забега (`RunController.resumeAtLaunch`).
    func resumeAtLaunch() {
        busy = true  // «Начать» и настоящий «Старт» подождут, пока база не прочитана
        if UserDefaults.standard.bool(forKey: Self.hintKey) {
            location.start(sending: tracker)
        }
        Task { await recover() }
    }

    private func recover() async {
        defer { busy = false }
        let dependencies = AppDependencies.shared
        let deviceId = await dependencies.installation.value()
        let rules = dependencies.rules
        let session = try? await RunTracker.recover(
            store: dependencies.syncStore, deviceId: deviceId, signedIn: LocalRun.labOwnerId,
            now: Date.now.timeIntervalSince1970, rules: { await rules.rules(version: $0)?.rules }, labProbe: true)
        guard let session else {
            // Продолжать нечего: накопленное геопозицией выбросить, плашку прежнего процесса — закрыть.
            stopSources()
            await tracker.flush()
            UserDefaults.standard.set(false, forKey: Self.hintKey)
            if let leftover = Self.activitySlot.saved {
                await RunActivityController.end(id: leftover)
                Self.activitySlot.forget()
            }
            return
        }
        let startedAt = Date(timeIntervalSince1970: Double(session.startedAtMs) / 1_000)
        // Счёт до перезапуска — до подключения забега: первое же обновление трекера прибавит к нему текущий.
        if var saved = record, saved.runId == session.runId {
            saved.beforeRelaunch = saved.total
            saved.relaunches += 1
            record = saved
        } else {
            record = ProbeRecord(runId: session.runId, startedAt: startedAt)
        }
        save()
        let since = Date(timeIntervalSince1970: await session.sensorsResumeFrom)
        try? await tracker.resume(session)
        startSources(motionSince: since)
        activityID = Self.activitySlot.adopt(running: RunActivityController.runningIDs)
        if activityID == nil {
            startLiveActivity(startedAt: startedAt)  // в фоне iOS её не запустит — тогда `becameActive`
        }
        UserDefaults.standard.set(true, forKey: Self.hintKey)
    }

    /// Приложение на переднем плане: забег, продолженный после перезапуска в фоне, мог остаться без Live Activity.
    func becameActive() {
        guard state.isRunning, activityID == nil, let record else { return }
        startLiveActivity(startedAt: record.startedAt)
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
        guard var record, fresh.runId == record.runId else { return }
        record.total = counters
        self.record = record
        if wasRunning, !fresh.isRunning {
            ended()  // в том числе конец по пределу длины
        } else if fresh.isRunning {
            updateLiveActivity(record.total)
            if Date.now.timeIntervalSince(lastSave) >= 5 {
                save()
            }
        }
    }

    private func ended() {
        stopSources()
        UserDefaults.standard.set(false, forKey: Self.hintKey)
        save()
        if let activityID {
            // Как у настоящего забега: забыть плашку — только когда она закрыта.
            Task {
                await RunActivityController.end(id: activityID)
                Self.activitySlot.forget(activityID)
            }
        }
        activityID = nil
    }

    // MARK: - Live Activity

    private func startLiveActivity(startedAt: Date) {
        guard RunActivityController.areActivitiesEnabled else { return }
        activityID = try? RunActivityController.start(
            startedAt: startedAt,
            state: RunActivityAttributes.ContentState(title: "Пробный забег", detail: "Ждём GPS…"))
        Self.activitySlot.remember(activityID)
    }

    /// Не чаще раза в 5 секунд: чаще система всё равно не покажет.
    private func updateLiveActivity(_ counters: ProbeCounters) {
        guard let activityID, Date.now.timeIntervalSince(lastActivityUpdate) >= 5 else { return }
        lastActivityUpdate = .now
        let state = Self.activityContent(counters)
        Task { await RunActivityController.update(id: activityID, state: state) }
    }

    /// Текст плашки: «Пробный забег · 1,23 км», «2 петли · принято 120 точек».
    nonisolated static func activityContent(_ counters: ProbeCounters) -> RunActivityAttributes.ContentState {
        RunActivityAttributes.ContentState(
            title: "Пробный забег · " + NumberText.kilometers(fromMeters: counters.distanceMeters, fractionDigits: 2),
            detail: CountText.loops(counters.loops) + " · принято " + CountText.points(counters.accepted))
    }

    // MARK: - Сводка

    /// Сводка без координат — для снимка экрана или отправки в чат.
    nonisolated static func summaryText(
        _ record: ProbeRecord?, counters: ProbeCounters, queued: QueuedRunSummary?
    ) -> String {
        guard let record else { return "Пробного забега ещё не было." }
        let minutes = Int((queued?.recordedSeconds ?? 0) / 60)
        let accepted = NumberText.integer(counters.accepted)
        let rejected = NumberText.integer(counters.rejected)
        var lines = [
            "Пробный забег · \(minutes) мин · перезапусков: \(record.relaunches)",
            "Точки: принято \(accepted), отброшено судьёй \(rejected)",
            "Петель: \(counters.loops) · туман на телефоне: \(CountText.fogCells(counters.fogCells))",
            "Дистанция: \(NumberText.kilometers(fromMeters: counters.distanceMeters, fractionDigits: 2))",
        ]
        if let queued {
            let longest = NumberText.seconds(queued.longestGapSeconds, fractionDigits: 0)
            let lag = queued.sensorLagSeconds.map { NumberText.seconds($0, fractionDigits: 1) } ?? "—"
            let stored = CountText.pieces(queued.chunks) + " · " + CountText.points(queued.points)
            lines += [
                "Разрывов GPS > 15 с: \(queued.gapsOverLimit) · самый длинный: \(longest)",
                "Отметка датчиков отстаёт на \(lag)",
                "В очереди: \(stored) · заявок: \(queued.claims)",
            ]
        }
        return lines.joined(separator: "\n")
    }

    // MARK: - Хранение

    private func save() {
        lastSave = .now
        guard let record else {
            UserDefaults.standard.removeObject(forKey: Self.recordKey)
            return
        }
        guard let data = try? JSONEncoder().encode(record) else { return }
        UserDefaults.standard.set(data, forKey: Self.recordKey)
    }

    private static func loadRecord() -> ProbeRecord? {
        guard let data = UserDefaults.standard.data(forKey: recordKey) else { return nil }
        return try? JSONDecoder().decode(ProbeRecord.self, from: data)
    }
}
