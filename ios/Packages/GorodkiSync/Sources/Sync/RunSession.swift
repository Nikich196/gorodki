import Foundation
import GameCore

/// Точка GPS, как её отдаёт CoreLocation, — ещё без номера.
public struct LocationFix: Equatable, Sendable {
    /// Точка, которую можно нумеровать: координата в диапазоне, точность известна (CoreLocation даёт −1, когда
    /// координаты нет), время — число.
    public var isValid: Bool {
        coordinate.latitude.isFinite && abs(coordinate.latitude) <= 90 && coordinate.longitude.isFinite
            && abs(coordinate.longitude) <= 180 && horizontalAccuracy.isFinite && horizontalAccuracy >= 0
            && timestamp.isFinite
    }

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
    private let exploration: ExplorationSettings
    private var lastAccepted: TrackPoint?
    /// Принятые точки, которые ещё могут оказаться «за 30 с до разрыва»; `startsSegment` — полоса начинается заново
    /// (после телепорта путь не рисуется через скачок).
    private var fogPending: [(point: TrackPoint, startsSegment: Bool)] = []
    /// Последняя точка, открывшая туман, в текущем отрезке: следующая открывает полосу от неё.
    private var fogLast: TrackPoint?
    private var lastPointMs: Int64?
    private var lastTimestamp: Double?
    /// Начало забега, мс: точка раньше начала больше чем на минуту — не этого забега.
    private let startedAtMs: Int64
    /// Точка старше этого (по часам телефона) — устаревшая: не нумеруется и не отправляется (`TrackJudging` на сервере
    /// считает «сейчас» временем самой точки, свежесть проверяет только телефон).
    private let maxFixAgeSeconds: Double
    /// После разрыва следа следующая точка начинает новую полосу тумана.
    private var fogNextStartsSegment = false
    /// Точки обрабатываются по одной: номер берётся до записи, и две точки не должны получить один номер.
    private var handling = false
    private var handlingWaiters: [CheckedContinuation<Void, Never>] = []

    private init(
        runId: UUID, league: League, startedAtMs: Int64, recorder: RunRecorder, rules: PhoneRules, newcomer: Bool
    ) {
        self.runId = runId
        self.league = league
        self.startedAtMs = startedAtMs
        self.recorder = recorder
        let judgeRules = rules.rules(for: league, newcomer: newcomer)
        self.maxFixAgeSeconds = judgeRules.maxFixAgeSeconds
        self.judge = SegmentJudge(league: league, rules: judgeRules)
        self.detector = LoopDetector(settings: rules.loopDetector)
        self.exploration = rules.exploration
    }

    /// Начать забег: он встаёт в очередь синхронизации, прерванные забеги игрока закрываются.
    /// - Parameters:
    ///   - rules: числа той версии конфига, что записана в забеге (`run.configVersion`): сервер судит забег ею же.
    ///     Предел длины забега берётся из них.
    ///   - newcomer: у игрока ещё нет засчитанных захватов — судья берёт порог точности новичка, как сервер.
    public static func start(
        _ run: LocalRun, store: any SyncStore, rules: PhoneRules, newcomer: Bool = false,
        policy: ChunkPolicy = ChunkPolicy()
    ) async throws -> RunSession {
        var run = run
        run.judgedAsNewcomer = newcomer
        let recorder = try await RunRecorder.begin(run, store: store, policy: policy.limited(by: rules))
        return RunSession(
            runId: run.id, league: run.league, startedAtMs: run.startedAtMs, recorder: recorder, rules: rules,
            newcomer: newcomer)
    }

    /// Новичок ли игрок — так, как видно с этого телефона: в очереди нет ни одной его засчитанной заявки. Если захваты
    /// были с другого телефона, телефон сочтёт игрока новичком и примет чуть больше точек, чем сервер, — лишняя заявка
    /// просто не пройдёт; наоборот (петли потерялись бы) не бывает.
    public static func isNewcomer(ownerId: String, store: any SyncStore) async throws -> Bool {
        for run in try await store.runs() where run.ownerId == ownerId {
            if try await store.claims(of: run.id).contains(where: { $0.outcome?.status == "applied" }) {
                return false
            }
        }
        return true
    }

    /// Продолжить забег после перезапуска приложения; `nil` — его нет, он завершён или отвергнут сервером.
    public static func resume(
        runId: UUID, store: any SyncStore, rules: PhoneRules, policy: ChunkPolicy = ChunkPolicy()
    ) async throws -> RunSession? {
        guard let run = try await store.runs().first(where: { $0.id == runId }),
            let recorder = try await RunRecorder.resume(runId: runId, store: store, policy: policy.limited(by: rules))
        else { return nil }
        let session = RunSession(
            runId: runId, league: run.league, startedAtMs: run.startedAtMs, recorder: recorder, rules: rules,
            newcomer: run.judgedAsNewcomer ?? false)
        // Время последней точки — у записи: она учитывает и куски, записанные до сбоя без отметки в забеге.
        await session.restore(lastPointMs: await recorder.lastPointMs)
        return session
    }

    private func restore(lastPointMs: Int64?) {
        self.lastPointMs = lastPointMs
        // Конец по пределу после продолжения — последняя записанная точка, а не время первой новой.
        lastTimestamp = lastPointMs.map { Double($0) / 1_000 }
    }

    /// Новая точка GPS.
    /// - Parameter now: «сейчас» по часам телефона, секунды Unix (судья отбрасывает устаревшие точки).
    /// - Returns: что стало с точкой; петля добавляет к вердикту `.loopClaimed`.
    public func handle(_ fix: LocationFix, now: Double) async throws -> [RunEvent] {
        try await exclusively { try await process(fix, now: now) }
    }

    /// Точки, таймер и конец забега — строго по одному: запись куска ждёт диск, и актор в это время принял бы
    /// следующую операцию (точку с тем же номером или конец забега посреди записи точки).
    private func exclusively<T: Sendable>(_ body: () async throws -> T) async throws -> T {
        while handling {
            await withCheckedContinuation { handlingWaiters.append($0) }
        }
        handling = true
        defer {
            handling = false
            if !handlingWaiters.isEmpty {
                handlingWaiters.removeFirst().resume()
            }
        }
        return try await body()
    }

    private func process(_ fix: LocationFix, now: Double) async throws -> [RunEvent] {
        guard !isFinished else { return [] }
        // Неверная точка (CoreLocation: точность −1 — координата неизвестна) не нумеруется: округление превратило бы
        // точность −1 в 0 — «идеальную», а координата вне диапазона дала бы отказ всего куска (docs/architecture/runs.md).
        guard fix.isValid else {
            stats.lastIssue = .poorAccuracy
            return [.dropped]
        }
        // Устаревшая точка (CoreLocation часто первой отдаёт запомненную) не нумеруется и не уходит на сервер.
        if now - fix.timestamp > maxFixAgeSeconds {
            stats.lastIssue = .staleFix
            return [.ignored(.staleFix)]
        }
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
            // Раньше начала больше чем на минуту — точка не этого забега; позже предела — забег окончен.
            if ms < startedAtMs {
                return [.dropped]
            }
            try await finishNow(endedAt: lastTimestamp ?? point.timestamp)
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
            fogPending.append((point, fogNextStartsSegment))
            fogNextStartsSegment = false
            settleFog(olderThan: StoragePrecision.milliseconds(point.timestamp) - Self.fogBreakLookbackMs)
        }
        return detector.add(point)
    }

    /// Разрыв следа: сама точка разрыва не открывает ничего, полоса начинается заново. Точки за 30 с до разрыва
    /// не открывают туман — кроме телепорта: путь до скачка честный. Но и эти точки ждут свои 30 с: если следом
    /// придёт другой разрыв (машина), сервер не засчитает и их — его окно 30 с не останавливается на телепорте.
    private func breakFog(at point: TrackPoint, issue: TrackIssue) {
        if issue != .teleport {
            // Как на сервере: не засчитываются точки не старше 30 с до разрыва.
            settleFog(olderThan: StoragePrecision.milliseconds(point.timestamp) - Self.fogBreakLookbackMs)
            fogPending = []
        }
        fogNextStartsSegment = true
    }

    /// Открыть туман точками старше `limitMs` (по возрастанию времени).
    private func settleFog(olderThan limitMs: Int64) {
        var settled = 0
        for (point, startsSegment) in fogPending where StoragePrecision.milliseconds(point.timestamp) < limitMs {
            if startsSegment {
                fogLast = nil
            }
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

    /// Вид движения от CoreMotion — в очередь и, если запись её приняла, судье: судья телефона должен видеть ровно то,
    /// что увидит сервер (запоздавшую или лишнюю запись сервер не получит).
    public func record(_ sample: MotionSample) async {
        let stored = sample.quantizedForStorage()
        if await recorder.record(stored) {
            judge.record(stored)
        }
    }

    /// Шаги от шагомера — так же: судье только принятое записью.
    public func record(_ sample: PedometerSample) async {
        let stored = sample.quantizedForStorage()
        if await recorder.record(stored) {
            judge.record(stored)
        }
    }

    /// Телефон уже получил все данные датчиков до этого момента (секунды Unix).
    public func sensorsComplete(through seconds: Double) async {
        await recorder.sensorsComplete(through: seconds)
    }

    /// Раз в несколько секунд: запечатать кусок, если он «созрел» по времени, и завершить забег по пределу длины, даже
    /// если GPS молчит (иначе предел сработал бы только на следующей точке).
    /// - Returns: `[.finishedAtLimit]`, если забег завершён по пределу.
    @discardableResult
    public func tick(now seconds: Double) async throws -> [RunEvent] {
        try await exclusively {
            guard !isFinished else { return [] }
            if StoragePrecision.milliseconds(seconds) > recorder.windowEndMs {
                try await finishNow(endedAt: lastTimestamp ?? Double(recorder.windowEndMs) / 1_000)
                return [.finishedAtLimit]
            }
            try await recorder.tick(now: seconds)
            return []
        }
    }

    /// С какого момента датчики ещё принимаются (секунды Unix): после перезапуска CoreMotion дозапрашивает историю
    /// отсюда.
    public var acceptsSensorsAfter: Double {
        get async { Double(await recorder.acceptsSensorsAfterMs) / 1_000 }
    }

    /// Сколько кусков запечатано с начала (или продолжения) забега — по нему видно, что пора звать синхронизацию.
    public var sealedChunks: Int {
        get async { await recorder.sealedChunks }
    }

    /// Конец забега (игрок нажал «Стоп» или вышел предел длины). Ждёт точку, которая сейчас записывается.
    public func finish(endedAt seconds: Double) async throws {
        try await exclusively { try await finishNow(endedAt: seconds) }
    }

    private func finishNow(endedAt seconds: Double) async throws {
        guard !isFinished else { return }
        try await recorder.finish(endedAt: seconds)
        settleFog(olderThan: .max)  // конец без разрыва: весь хвост засчитан
        isFinished = true
    }
}
