import Foundation
import GameCore
import Observation
import Platform

/// Итоги прогулки для спайка S1 — только числа, без координат: сводку можно смело прислать в чат.
struct WalkStats: Codable, Equatable {
    var api: LocationAPI
    var startedAt: Date
    var fixes = 0
    var ignored = 0
    var breaks: [String: Int] = [:]
    var longestGapSeconds = 0.0
    var gapsOver15Seconds = 0
    var distanceMeters = 0.0
    var loops = 0
    var loopAreaSquareMeters = 0.0
    var fogCells = 0
    var fogAreaSquareMeters = 0.0
    var lastAccuracy: Double?
    var bestAccuracy: Double?
    var relaunches = 0
    /// Сколько секунд после перезапуска приложения пришла первая точка (цель спайка — не больше 5 с).
    var lastRecoverySeconds: Double?
    /// Запаздывание записей CoreMotion (получение − время записи), секунды: по нему выбирается отметка «все датчики до
    /// этого момента получены» у трекера (`RunTracker.sensorLagSeconds`). Первая запись — текущий вид движения с давним
    /// началом — не считается.
    var motionLags: [Double]?
    /// То же для шагомера (получение − конец интервала).
    var stepLags: [Double]?
    var lastFixAt: Date?

    init(api: LocationAPI, startedAt: Date) {
        self.api = api
        self.startedAt = startedAt
    }
}

/// «Лаборатория», прогулка: проверка фонового трекинга (спайк S1) вместе с живыми правилами GameCore.
///
/// Каждая точка проходит тот же путь, что в игре: античит (`SegmentJudge`) → детектор петли (`LoopDetector`)
/// → туман (`FogLayer`). Статистика сохраняется на каждой десятой точке, поэтому переживает перезапуск приложения:
/// после перезапуска запись продолжается сама, а время до первой точки записывается как «восстановление».
@MainActor
@Observable
final class WalkLab {
    static let shared = WalkLab()

    var api: LocationAPI = .liveUpdates
    private(set) var stats: WalkStats?
    private(set) var isRunning = false
    private(set) var lastEvent: String?

    private var feed: LocationFeed?
    private let motion = MotionFeed()
    private var judge = SegmentJudge(league: .run)
    private var detector = LoopDetector()
    private var fog = FogLayer()
    private var lastAccepted: TrackPoint?
    private var lastFixTime: Double?
    private var seq = 0
    private var resumedAt: Date?
    private var activityID: String?
    private var lastActivityUpdate = Date.distantPast
    private var sawFirstActivity = false
    /// Замеров запаздывания храним не больше стольких — хватает на прогулку, UserDefaults не раздувается.
    private static let maxLagSamples = 600

    private static let storageKey = "lab.walk.stats"

    private init() {
        stats = Self.loadStats()
    }

    // MARK: - Управление

    func start() {
        guard !isRunning else { return }
        stats = WalkStats(api: api, startedAt: .now)
        fog = FogLayer()
        seq = 0
        lastEvent = nil
        save()
        begin()
        startLiveActivity()
    }

    func stop() {
        guard isRunning else { return }
        feed?.stop()
        feed = nil
        motion.stop()
        isRunning = false
        UserDefaults.standard.set(false, forKey: Self.activeKey)
        save()
        if let activityID {
            // Как у забега (`RunController.ended`): забыть плашку — только когда она закрыта.
            Task {
                await RunActivityController.end(id: activityID)
                Self.activitySlot.forget(activityID)
            }
        }
        activityID = nil
    }

    /// Вызывается при запуске приложения: если прогулка шла, когда приложение закрыли, — продолжаем.
    func resumeIfNeeded() {
        guard UserDefaults.standard.bool(forKey: Self.activeKey), var saved = stats, !isRunning else { return }
        saved.relaunches += 1
        stats = saved
        api = saved.api
        resumedAt = .now
        begin()
        // Своя плашка — по сохранённому идентификатору: первая попавшаяся могла бы оказаться плашкой забега.
        activityID = Self.activitySlot.adopt(running: RunActivityController.runningIDs)
    }

    /// Сводка без координат — для снимка экрана или отправки в чат.
    var summary: String {
        guard let stats else { return "Прогулки ещё не было." }
        let minutes = Int((stats.lastFixAt ?? .now).timeIntervalSince(stats.startedAt) / 60)
        var lines = [
            "Спайк S1 · \(stats.api.title) · \(minutes) мин",
            "Точек: \(stats.fixes) (отброшено \(stats.ignored))",
            "Самый длинный разрыв: \(Int(stats.longestGapSeconds)) с · разрывов > 15 с: \(stats.gapsOver15Seconds)",
            "Дистанция: \(NumberText.kilometers(fromMeters: stats.distanceMeters, fractionDigits: 2))",
            "Петель: \(stats.loops) (≈\(NumberText.hectares(fromSquareMeters: stats.loopAreaSquareMeters, fractionDigits: 2)))",
            "Туман: \(CountText.fogCells(stats.fogCells)) (≈\(NumberText.hectares(fromSquareMeters: stats.fogAreaSquareMeters, fractionDigits: 2)))",
            "Точность: последняя \(stats.lastAccuracy.map { "\(Int($0)) м" } ?? "—"), лучшая \(stats.bestAccuracy.map { "\(Int($0)) м" } ?? "—")",
            "Перезапусков: \(stats.relaunches), восстановление: \(stats.lastRecoverySeconds.map { NumberText.seconds($0, fractionDigits: 1) } ?? "—")",
            "Запаздывание вида движения: \(Self.lagSummary(stats.motionLags))",
            "Запаздывание шагомера: \(Self.lagSummary(stats.stepLags))",
        ]
        if !stats.breaks.isEmpty {
            lines.append(
                "Разрывы следа: "
                    + stats.breaks.sorted { $0.key < $1.key }.map { "\($0.key) \($0.value)" }.joined(separator: ", "))
        }
        return lines.joined(separator: "\n")
    }

    // MARK: - Обработка точек

    private static let activeKey = "lab.walk.active"
    private static let activitySlot = LiveActivitySlot(key: "lab.walk.activityID")

    private func begin() {
        judge = SegmentJudge(league: .run)
        detector = LoopDetector()
        lastAccepted = nil
        lastFixTime = nil
        let feed: LocationFeed = api == .liveUpdates ? LiveUpdatesFeed() : ManagerFeed()
        self.feed = feed
        isRunning = true
        UserDefaults.standard.set(true, forKey: Self.activeKey)
        feed.start { [weak self] fix in self?.handle(fix) }
        sawFirstActivity = false
        motion.start(
            onActivity: { [weak self] sample, receivedAt in self?.record(sample, receivedAt: receivedAt) },
            onSteps: { [weak self] sample, receivedAt in self?.record(sample, receivedAt: receivedAt) }
        )
    }

    private func handle(_ fix: LocationFix) {
        guard var stats else { return }
        let now = Date.now

        if let resumedAt {
            stats.lastRecoverySeconds = now.timeIntervalSince(resumedAt)
            self.resumedAt = nil
        }

        stats.fixes += 1
        stats.lastAccuracy = fix.horizontalAccuracy
        stats.bestAccuracy = min(stats.bestAccuracy ?? .infinity, fix.horizontalAccuracy)
        stats.lastFixAt = now
        if let last = lastFixTime {
            let gap = fix.timestamp - last
            stats.longestGapSeconds = max(stats.longestGapSeconds, gap)
            if gap > 15 {
                stats.gapsOver15Seconds += 1
            }
        }
        lastFixTime = fix.timestamp

        let point = TrackPoint(
            seq: seq,
            coordinate: Coordinate(latitude: fix.latitude, longitude: fix.longitude),
            timestamp: fix.timestamp,
            horizontalAccuracy: fix.horizontalAccuracy,
            speed: fix.speed >= 0 ? fix.speed : nil
        )
        seq += 1

        switch judge.judge(point, now: now.timeIntervalSince1970) {
        case .accepted:
            accept(point, into: &stats)
        case .ignored:
            stats.ignored += 1
        case .segmentBroken(let issue):
            stats.breaks[issue.rawValue, default: 0] += 1
            lastEvent = "След порван: \(issue.rawValue)"
            detector.reset()
            lastAccepted = nil
            accept(point, into: &stats)
        }

        self.stats = stats
        if stats.fixes % 10 == 0 {
            save()
        }
        updateLiveActivity(stats)
    }

    private func accept(_ point: TrackPoint, into stats: inout WalkStats) {
        if let last = lastAccepted {
            stats.distanceMeters += Geodesy.distance(from: last.coordinate, to: point.coordinate)
            fog.reveal(from: last.coordinate, to: point.coordinate)
        } else {
            fog.reveal(around: point.coordinate)
        }
        lastAccepted = point
        stats.fogCells = fog.cellCount
        stats.fogAreaSquareMeters = fog.areaSquareMeters

        if let claim = detector.add(point) {
            stats.loops += 1
            stats.loopAreaSquareMeters += claim.estimatedArea
            lastEvent = "Петля замкнута: ≈\(NumberText.squareMeters(claim.estimatedArea))"
        }
    }

    // MARK: - Датчики

    private func record(_ sample: MotionSample, receivedAt: Double) {
        judge.record(sample)
        guard sawFirstActivity else {
            sawFirstActivity = true  // текущий вид движения: начало может быть часы назад — не запаздывание
            return
        }
        stats?.motionLags = Self.appending(receivedAt - sample.timestamp, to: stats?.motionLags)
    }

    private func record(_ sample: PedometerSample, receivedAt: Double) {
        judge.record(sample)
        stats?.stepLags = Self.appending(receivedAt - sample.end, to: stats?.stepLags)
    }

    private static func appending(_ lag: Double, to lags: [Double]?) -> [Double] {
        Array(((lags ?? []) + [lag]).suffix(maxLagSamples))
    }

    /// «медиана 3,1 с · 99 % — 8,4 с · макс 12,0 с (n = 140)» — без выбросов не обойтись, поэтому и максимум.
    nonisolated static func lagSummary(_ lags: [Double]?) -> String {
        guard let lags, !lags.isEmpty else { return "—" }
        let sorted = lags.sorted()
        func percentile(_ p: Double) -> String {
            NumberText.seconds(
                sorted[min(sorted.count - 1, Int((Double(sorted.count - 1) * p).rounded()))], fractionDigits: 1)
        }
        let maximum = NumberText.seconds(sorted.last ?? 0, fractionDigits: 1)
        return "медиана \(percentile(0.5)) · 99 % — \(percentile(0.99)) · макс \(maximum) (n = \(sorted.count))"
    }

    // MARK: - Live Activity

    private func startLiveActivity() {
        guard RunActivityController.areActivitiesEnabled else { return }
        activityID = try? RunActivityController.start(
            startedAt: .now,
            state: RunActivityAttributes.ContentState(title: "Прогулка · проверка", detail: "Ждём GPS…")
        )
        Self.activitySlot.remember(activityID)
    }

    /// Не чаще раза в 5 секунд: чаще система всё равно не покажет.
    private func updateLiveActivity(_ stats: WalkStats) {
        guard let activityID, Date.now.timeIntervalSince(lastActivityUpdate) >= 5 else { return }
        lastActivityUpdate = .now
        let state = Self.activityContent(stats)
        Task { await RunActivityController.update(id: activityID, state: state) }
    }

    /// Текст плашки: «Прогулка · 1,23 км», «2 петли · 5 точек · разрывов >15 с: 0».
    nonisolated static func activityContent(_ stats: WalkStats) -> RunActivityAttributes.ContentState {
        RunActivityAttributes.ContentState(
            title: "Прогулка · " + NumberText.kilometers(fromMeters: stats.distanceMeters, fractionDigits: 2),
            detail: CountText.loops(stats.loops) + " · " + CountText.points(stats.fixes)
                + " · разрывов >15 с: \(stats.gapsOver15Seconds)")
    }

    // MARK: - Хранение

    private func save() {
        guard let stats, let data = try? JSONEncoder().encode(stats) else { return }
        UserDefaults.standard.set(data, forKey: Self.storageKey)
    }

    private static func loadStats() -> WalkStats? {
        guard let data = UserDefaults.standard.data(forKey: storageKey) else { return nil }
        return try? JSONDecoder().decode(WalkStats.self, from: data)
    }
}
