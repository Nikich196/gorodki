import Foundation
import GameCore

/// Точка GPS, как её отдаёт CoreLocation, — ещё без номера.
public struct LocationFix: Equatable, Sendable {
    public var coordinate: Coordinate
    /// Время точки, секунды Unix.
    public var timestamp: Double
    public var horizontalAccuracy: Double
    /// Скорость, м/с; `nil` — телефон её не знает.
    public var speed: Double?
    public var source: PointSource

    public init(
        coordinate: Coordinate, timestamp: Double, horizontalAccuracy: Double, speed: Double? = nil,
        source: PointSource = []
    ) {
        self.coordinate = coordinate
        self.timestamp = timestamp
        self.horizontalAccuracy = horizontalAccuracy
        self.speed = speed
        self.source = source
    }
}

/// Что стало с точкой.
public enum RunEvent: Equatable, Sendable {
    /// Точка принята в текущий отрезок следа.
    case accepted
    /// Точка записана, но в след не пошла (плохая точность, устаревшая…) — сервер решит так же.
    case ignored(TrackIssue)
    /// След порван на этой точке: петли до неё и после неё не соединяются (PLAN.md, D16).
    case broken(TrackIssue)
    /// Петля замкнута и заявлена; `claimNo` — номер заявки в забеге.
    case loopClaimed(claimNo: Int, LoopClaim)
    /// Точка отброшена целиком: время не выросло (GPS повторил точку). Номер не израсходован.
    case dropped
    /// Забег достиг предела длины (`capture.maxRunHours`) и завершён.
    case finishedAtLimit
}

/// Сводка забега для экрана и Live Activity.
public struct RunStats: Equatable, Sendable {
    public var points = 0
    public var acceptedPoints = 0
    /// Путь по принятым точкам — сразу, для экрана. Засчитанный сервером путь бывает короче (30 с перед разрывом).
    public var distanceMeters = 0.0
    public var loops = 0
    /// Оценка площади заявленных петель на телефоне; итог решает сервер.
    public var loopAreaSquareMeters = 0.0
    /// Открытый туман — по правилу сервера: путь за 30 с до разрыва следа не открывает, поэтому туман отстаёт на 30 с.
    public var fogCells = 0
    public var fogAreaSquareMeters = 0.0
    /// Последняя причина, по которой точка не пошла в след или след порвался, — подсказка игроку.
    public var lastIssue: TrackIssue?

    public init() {}
}

/// Забег на телефоне (PLAN.md, §7.2 «Трекинг»): точка GPS получает номер → античит (`SegmentJudge`) → запись в очередь
/// (`RunRecorder`); принятые точки — в детектор петли и туман; замкнутая петля — сразу заявка.
///
/// В очередь уходят **все** точки, и отброшенные судьёй тоже: сервер судит тот же след теми же правилами, и их вердикты
/// должны совпасть. Поэтому и судья на телефоне видит точку такой, какой её сохранит сервер (`quantizedForStorage`).
/// Судья и детектор живут в памяти: после перезапуска приложения (`resume`) они начинают заново — петля, начатая
/// до перезапуска, не заявляется, но сервер всё равно судит весь след.
///
/// Туман на телефоне — предпросмотр по правилу сервера (`JudgedPath`): судья рвёт след с запаздыванием (транспорт —
/// после 20 с, велосипед — после 30 с), поэтому путь за `fogBreakLookback` до разрыва (кроме телепорта) и сама точка
/// разрыва туман не открывают. Точка открывает туман, когда после неё прошло больше 30 с без разрыва.
///
/// Точки передаются по одной, по порядку: следующий вызов `handle` — после ответа на предыдущий.
public actor RunSession {
    /// Сколько пути перед разрывом не открывает туман — как `JudgedPath.BreakLookback` на сервере.
    public static let fogBreakLookbackMs: Int64 = 30_000

    public nonisolated let runId: UUID
    public nonisolated let league: League
    public private(set) var stats = RunStats()
    public private(set) var isFinished = false

    private let recorder: RunRecorder
    private var judge: SegmentJudge
    private var detector: LoopDetector
    /// Открытый туман этого забега — для карты во время забега (итог решает сервер).
    public private(set) var fog = FogLayer()
    private let exploration = ExplorationSettings()
    private var lastAccepted: TrackPoint?
    /// Принятые точки, которые ещё могут оказаться «за 30 с до разрыва».
    private var fogPending: [TrackPoint] = []
    /// Последняя точка, открывшая туман, в текущем отрезке: следующая открывает полосу от неё.
    private var fogLast: TrackPoint?
    private var lastPointMs: Int64?
    private var lastTimestamp: Double?

    private init(runId: UUID, league: League, recorder: RunRecorder, detector: LoopDetectorSettings) {
        self.runId = runId
        self.league = league
        self.recorder = recorder
        self.judge = SegmentJudge(league: league)
        self.detector = LoopDetector(settings: detector)
    }

    /// Начать забег: он встаёт в очередь синхронизации, прерванные забеги игрока закрываются.
    public static func start(
        _ run: LocalRun, store: any SyncStore, policy: ChunkPolicy = ChunkPolicy(),
        detector: LoopDetectorSettings = LoopDetectorSettings()
    ) async throws -> RunSession {
        let recorder = try await RunRecorder.begin(run, store: store, policy: policy)
        return RunSession(runId: run.id, league: run.league, recorder: recorder, detector: detector)
    }

    /// Продолжить забег после перезапуска приложения; `nil` — его нет, он завершён или отвергнут сервером.
    public static func resume(
        runId: UUID, store: any SyncStore, policy: ChunkPolicy = ChunkPolicy(),
        detector: LoopDetectorSettings = LoopDetectorSettings()
    ) async throws -> RunSession? {
        guard let run = try await store.runs().first(where: { $0.id == runId }),
            let recorder = try await RunRecorder.resume(runId: runId, store: store, policy: policy)
        else { return nil }
        let session = RunSession(runId: runId, league: run.league, recorder: recorder, detector: detector)
        await session.restore(lastPointMs: run.lastPointMs)
        return session
    }

    private func restore(lastPointMs: Int64?) {
        self.lastPointMs = lastPointMs
    }

    /// Новая точка GPS.
    /// - Parameter now: «сейчас» по часам телефона, секунды Unix (судья отбрасывает устаревшие точки).
    /// - Returns: что стало с точкой; петля добавляет к вердикту `.loopClaimed`.
    public func handle(_ fix: LocationFix, now: Double) async throws -> [RunEvent] {
        guard !isFinished else { return [] }
        let point = TrackPoint(
            seq: await recorder.nextSeq, coordinate: fix.coordinate, timestamp: fix.timestamp,
            horizontalAccuracy: fix.horizontalAccuracy, speed: fix.speed
        )
        .quantizedForStorage()
        let ms = StoragePrecision.milliseconds(point.timestamp)
        if let lastPointMs, ms <= lastPointMs {
            return [.dropped]
        }
        do {
            try await recorder.record(point, source: fix.source)
        } catch RecorderError.outsideRunWindow {
            try await finish(endedAt: lastTimestamp ?? point.timestamp)
            return [.finishedAtLimit]
        }
        lastPointMs = ms
        lastTimestamp = point.timestamp
        stats.points += 1

        var events: [RunEvent]
        var breaksSegment = false
        switch judge.judge(point, now: now) {
        case .accepted:
            events = [.accepted]
        case .ignored(let issue):
            stats.lastIssue = issue
            return [.ignored(issue)]
        case .segmentBroken(let issue):
            stats.lastIssue = issue
            detector.reset()
            lastAccepted = nil
            breakFog(at: point, issue: issue)
            breaksSegment = true
            events = [.broken(issue)]  // с этой точки начинается новый отрезок
        }
        if let loop = accept(point, breaksSegment: breaksSegment) {
            // Петлю, которую очередь не примет (слишком короткая, уже заявлена), не заявляем: сервер её тоже не принял бы.
            if let claimNo = try? await recorder.claim(loop) {
                stats.loops += 1
                stats.loopAreaSquareMeters += loop.estimatedArea
                events.append(.loopClaimed(claimNo: claimNo, loop))
            }
        }
        return events
    }

    private func accept(_ point: TrackPoint, breaksSegment: Bool) -> LoopClaim? {
        if let last = lastAccepted {
            stats.distanceMeters += Geodesy.distance(from: last.coordinate, to: point.coordinate)
        }
        lastAccepted = point
        stats.acceptedPoints += 1
        if !breaksSegment {  // точка разрыва туман не открывает — см. `breakFog`
            fogPending.append(point)
            settleFog(olderThan: StoragePrecision.milliseconds(point.timestamp) - Self.fogBreakLookbackMs)
        }
        return detector.add(point)
    }

    /// Разрыв следа: точки за 30 с до него не открывают туман (кроме телепорта — путь до скачка честный), отрезок
    /// тумана кончается, сама точка разрыва не открывает ничего.
    private func breakFog(at point: TrackPoint, issue: TrackIssue) {
        let breakMs = StoragePrecision.milliseconds(point.timestamp)
        if issue == .teleport {
            settleFog(olderThan: .max)
        } else {
            // Как на сервере: не засчитываются точки не старше 30 с до разрыва.
            settleFog(olderThan: breakMs - Self.fogBreakLookbackMs)
            fogPending = []
        }
        fogLast = nil
    }

    /// Открыть туман точками старше `limitMs` (по возрастанию времени).
    private func settleFog(olderThan limitMs: Int64) {
        var settled = 0
        for point in fogPending where StoragePrecision.milliseconds(point.timestamp) < limitMs {
            if let last = fogLast {
                fog.reveal(
                    from: last.coordinate, to: point.coordinate, radius: exploration.revealRadiusMeters,
                    maxGap: exploration.maxGapMeters.value(for: league))
            } else {
                fog.reveal(around: point.coordinate, radius: exploration.revealRadiusMeters)
            }
            fogLast = point
            settled += 1
        }
        guard settled > 0 else { return }
        fogPending.removeFirst(settled)
        stats.fogCells = fog.cellCount
        stats.fogAreaSquareMeters = fog.areaSquareMeters
    }

    /// Вид движения от CoreMotion — судье и в очередь (сервер судит по тем же данным).
    public func record(_ sample: MotionSample) async {
        let stored = sample.quantizedForStorage()
        judge.record(stored)
        await recorder.record(stored)
    }

    /// Шаги от шагомера — судье и в очередь.
    public func record(_ sample: PedometerSample) async {
        let stored = sample.quantizedForStorage()
        judge.record(stored)
        await recorder.record(stored)
    }

    /// Телефон уже получил все данные датчиков до этого момента (секунды Unix).
    public func sensorsComplete(through seconds: Double) async {
        await recorder.sensorsComplete(through: seconds)
    }

    /// Раз в несколько секунд: запечатать кусок, если он «созрел» по времени.
    public func tick(now seconds: Double) async throws {
        try await recorder.tick(now: seconds)
    }

    /// Конец забега (игрок нажал «Стоп» или вышел предел длины).
    public func finish(endedAt seconds: Double) async throws {
        guard !isFinished else { return }
        try await recorder.finish(endedAt: seconds)
        settleFog(olderThan: .max)  // конец без разрыва: весь хвост засчитан
        isFinished = true
    }
}
