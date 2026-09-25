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

/// Сводка забега для экрана и Live Activity. После перезапуска приложения продолжается с сохранённой в забеге
/// (`RunSummary`): числа не «падают» до нуля.
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
    /// «≈+N га»: новые клетки тумана по сравнению с туманом игрока за всё время, м² (fog.md: «новое за всё время»).
    public var fogNewSquareMeters = 0.0
    /// Туман за всё время известен не для всех тайлов забега (нет сети, ещё не пришёл) — число выше нижняя граница, «≥».
    public var fogNewIsLowerBound = false
    /// Последняя причина, по которой точка не пошла в след или след порвался, — подсказка игроку.
    public var lastIssue: TrackIssue?
    /// Когда она случилась, мс: время точки (для точки без номера — время получения).
    public var lastIssueAtMs: Int64?
    /// Разрывы следа по причинам.
    public var breaks: [TrackIssue: Int] = [:]
    /// Последний разрыв следа и его время (мс) — для плашки «след прервался».
    public var lastBreak: TrackIssue?
    public var lastBreakAtMs: Int64?
    /// Первая принятая точка после последнего разрыва, мс (`nil` — принятых после него ещё нет).
    public var acceptedAfterBreakAtMs: Int64?
    /// Последняя принятая точка, мс.
    public var lastAcceptedAtMs: Int64?
    /// Последняя записанная точка, мс: часы демо-повтора (`RunHUD.elapsedSeconds`).
    public var lastPointAtMs: Int64?

    public init() {}
}

/// Кольцо заявленной петли — для первой фазы церемонии и временного контура на карте (docs/architecture/run-hud.md).
public struct LoopRing: Equatable, Sendable {
    public var claimNo: Int
    public var loop: LoopClaim
    /// Принятые точки от начала петли до точки замыкания (она — последняя).
    public var coordinates: [Coordinate]
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
    /// Разрешение «Движение и фитнес» при старте (`LocalRun.motionAuthorized`): без него сервер откажет каждой петле —
    /// после перезапуска приложение восстанавливает по нему плашку (`RunController.recover`).
    public nonisolated let motionAuthorized: Bool
    public nonisolated let source: LocalRun.Source
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
    /// Начало забега, мс Unix.
    public nonisolated let startedAtMs: Int64
    /// Точка старше этого (по часам телефона) — устаревшая: не нумеруется и не отправляется (`TrackJudging` на сервере
    /// считает «сейчас» временем самой точки, свежесть проверяет только телефон).
    private let maxFixAgeSeconds: Double
    /// После разрыва следа следующая точка начинает новую полосу тумана.
    private var fogNextStartsSegment = false
    /// Петли, заявку которых не удалось записать (ошибка диска). Детектор их уже не найдёт, а сервер петли сам не ищет —
    /// заявка повторяется на следующей точке, тике и перед концом забега.
    private var unsavedLoops: [LoopClaim] = []
    /// Точки обрабатываются по одной: номер берётся до записи, и две точки не должны получить один номер.
    private var handling = false
    private var handlingWaiters: [CheckedContinuation<Void, Never>] = []
    private let captureArea: CaptureAreaLimits
    /// Туман до перезапуска приложения (из сохранённой сводки): к нему прибавляется туман этого процесса.
    private var fogBefore = RunSummary()
    /// Туман игрока за всё время по тайлам — то, что уже дал поставщик (`RunTracker`, `AllTimeFogProvider`).
    private var allTimeFog: [FogTileKey: FogTileBits] = [:]
    /// Тайлы, которые уже попрошены у поставщика: каждый — один раз за забег в этом процессе.
    private var requestedFogTiles: Set<FogTileKey> = []
    private var latitude: Double?
    private var endedAtLimit = false
    /// Принятые точки этого процесса — след для карты и кольца петель.
    private var trail: [(seq: Int, coordinate: Coordinate, startsSegment: Bool)] = []
    /// Петли, заявленные в этом процессе, по номеру заявки.
    private var claimed: [Int: LoopClaim] = [:]

    private init(run: LocalRun, recorder: RunRecorder, rules: PhoneRules, newcomer: Bool) {
        self.runId = run.id
        self.league = run.league
        self.motionAuthorized = run.motionAuthorized
        self.source = run.source
        self.startedAtMs = run.startedAtMs
        self.recorder = recorder
        let judgeRules = rules.rules(for: run.league, newcomer: newcomer)
        self.maxFixAgeSeconds = judgeRules.maxFixAgeSeconds
        self.judge = SegmentJudge(league: run.league, rules: judgeRules)
        self.detector = LoopDetector(settings: rules.loopDetector)
        self.exploration = rules.exploration
        self.captureArea = rules.captureArea
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
        return RunSession(run: run, recorder: recorder, rules: rules, newcomer: newcomer)
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
    /// - Parameter continuingSummary: сводка продолжается с сохранённой в забеге (`LocalRun.summary`) и заявок очереди —
    ///   дистанция, петли и туман на экране не падают до нуля. `false` — с нуля: пробный забег «Лаборатории» складывает
    ///   счёт до перезапуска сам (`ProbeCounters`).
    public static func resume(
        runId: UUID, store: any SyncStore, rules: PhoneRules, policy: ChunkPolicy = ChunkPolicy(),
        continuingSummary: Bool = true
    ) async throws -> RunSession? {
        guard let run = try await store.runs().first(where: { $0.id == runId }),
            let recorder = try await RunRecorder.resume(runId: runId, store: store, policy: policy.limited(by: rules))
        else { return nil }
        let session = RunSession(run: run, recorder: recorder, rules: rules, newcomer: run.judgedAsNewcomer ?? false)
        // Время последней точки — у записи: она учитывает и куски, записанные до сбоя без отметки в забеге.
        await session.restore(lastPointMs: await recorder.lastPointMs)
        if continuingSummary {
            await session.restore(run.summary ?? RunSummary(), claims: try await store.claims(of: runId))
        }
        return session
    }

    private func restore(lastPointMs: Int64?) {
        self.lastPointMs = lastPointMs
        // Конец по пределу после продолжения — последняя записанная точка, а не время первой новой.
        lastTimestamp = lastPointMs.map { Double($0) / 1_000 }
        stats.lastPointAtMs = lastPointMs
    }

    /// Сводка до перезапуска: петли — по заявкам очереди (заявка пишется раньше сводки), остальное — из забега.
    private func restore(_ saved: RunSummary, claims: [PendingClaim]) async {
        stats.points = saved.points
        stats.acceptedPoints = saved.acceptedPoints
        stats.distanceMeters = saved.distanceMeters
        stats.loops = claims.count
        stats.loopAreaSquareMeters = claims.reduce(0) { $0 + $1.loop.estimatedArea }
        for (name, count) in saved.breaks {
            if let issue = TrackIssue(rawValue: name) {
                stats.breaks[issue] = count
            }
        }
        fogBefore = saved
        latitude = saved.latitude
        updateFogStats()
        await recorder.note(summary)
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
            note(.poorAccuracy, atMs: StoragePrecision.milliseconds(now))
            return [.dropped]
        }
        // Устаревшая точка (CoreLocation часто первой отдаёт запомненную) не нумеруется и не уходит на сервер.
        if now - fix.timestamp > maxFixAgeSeconds {
            note(.staleFix, atMs: StoragePrecision.milliseconds(now))
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
            try await finishNow(endedAt: lastTimestamp ?? point.timestamp, atLimit: true)
            return [.finishedAtLimit]
        }
        lastPointMs = ms
        lastTimestamp = point.timestamp
        stats.points += 1
        stats.lastPointAtMs = ms

        var events: [RunEvent]
        var breaksSegment = false
        switch judge.judge(point, now: now) {
        case .accepted:
            events = [.accepted]
        case .ignored(let issue):
            note(issue, atMs: ms)
            await recorder.note(summary)
            return [.ignored(issue)]
        case .segmentBroken(let issue):
            note(issue, atMs: ms)
            stats.breaks[issue, default: 0] += 1
            stats.lastBreak = issue
            stats.lastBreakAtMs = ms
            stats.acceptedAfterBreakAtMs = nil
            detector.reset()
            lastAccepted = nil
            breakFog(at: point, issue: issue)
            breaksSegment = true
            events = [.broken(issue)]  // с этой точки начинается новый отрезок
        }
        if let loop = accept(point, breaksSegment: breaksSegment) {
            unsavedLoops.append(loop)
        }
        events += try await claimUnsaved()
        await recorder.note(summary)
        return events
    }

    private func note(_ issue: TrackIssue, atMs ms: Int64) {
        stats.lastIssue = issue
        stats.lastIssueAtMs = ms
    }

    /// Заявить петли, ждущие записи, по порядку. Ошибка хранилища — петля остаётся ждать, а ошибка уходит трекеру
    /// (`storageFailed`): проглоченная, она потеряла бы захват молча.
    private func claimUnsaved() async throws -> [RunEvent] {
        var events: [RunEvent] = []
        while let loop = unsavedLoops.first {
            do {
                let claimNo = try await recorder.claim(loop)
                stats.loops += 1
                stats.loopAreaSquareMeters += loop.estimatedArea
                claimed[claimNo] = loop
                events.append(.loopClaimed(claimNo: claimNo, loop))
            } catch is RecorderError {
                // Петлю, которую очередь не примет (слишком короткая, уже заявлена), не заявляем: сервер её тоже не принял бы.
            }
            unsavedLoops.removeFirst()
        }
        return events
    }

    private func accept(_ point: TrackPoint, breaksSegment: Bool) -> LoopClaim? {
        if let last = lastAccepted {
            stats.distanceMeters += Geodesy.distance(from: last.coordinate, to: point.coordinate)
        }
        lastAccepted = point
        stats.acceptedPoints += 1
        let ms = StoragePrecision.milliseconds(point.timestamp)
        stats.lastAcceptedAtMs = ms
        if !breaksSegment, stats.lastBreakAtMs != nil, stats.acceptedAfterBreakAtMs == nil {
            stats.acceptedAfterBreakAtMs = ms
        }
        if latitude == nil {
            latitude = (point.coordinate.latitude * 100).rounded() / 100
        }
        trail.append((point.seq, point.coordinate, breaksSegment || trail.isEmpty))
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
        updateFogStats()
    }

    // MARK: - «≈+N га» тумана

    /// Туман забега в сводку: до перезапуска + этот процесс; новое — по сравнению с туманом за всё время. Сравнение
    /// идёт по тайлам (1 024 слова на тайл) и считается при каждом изменении тумана или приходе тайла — это дёшево.
    private func updateFogStats() {
        let fresh = fog.newArea(comparedTo: allTimeFog)
        stats.fogCells = fogBefore.fogCells + fog.cellCount
        stats.fogAreaSquareMeters = fogBefore.fogAreaSquareMeters + fog.areaSquareMeters
        stats.fogNewSquareMeters = fogBefore.fogNewSquareMeters + fresh.squareMeters
        stats.fogNewIsLowerBound = fogBefore.fogNewIsLowerBound || !fresh.unknownTiles.isEmpty
    }

    /// Тайлы тумана забега, которые ещё не просили у поставщика тумана за всё время, — каждый отдаётся один раз.
    public func fogTilesToRequest() -> [FogTileKey] {
        let fresh = fog.tiles.keys.filter { !requestedFogTiles.contains($0) }.sorted()
        requestedFogTiles.formUnion(fresh)
        return fresh
    }

    /// Пришёл тайл тумана игрока за всё время (`nil` — неизвестен: клетки тайла остаются ни новыми, ни старыми, число —
    /// нижняя граница). Пустой тайл сервера (версия 0) — `FogTileBits(words: [])`: все клетки в нём новые.
    public func allTimeFogArrived(_ key: FogTileKey, _ bits: FogTileBits?) async {
        guard let bits else { return }
        allTimeFog[key] = bits
        updateFogStats()
        await recorder.note(summary)
    }

    // MARK: - Для экрана

    /// Подсказка «до замыкания» для последней принятой точки (цель — начало открытой петли).
    public var closureHint: ClosureHint { detector.closureHint(limits: captureArea) }

    /// След этого процесса для карты: принятые точки по отрезкам (после разрыва — новый). Прежний след (до перезапуска
    /// приложения) — в очереди и в истории забегов, не здесь.
    public func trailSegments() -> [[Coordinate]] {
        var segments: [[Coordinate]] = []
        for point in trail {
            if point.startsSegment || segments.isEmpty {
                segments.append([])
            }
            segments[segments.count - 1].append(point.coordinate)
        }
        return segments
    }

    /// Кольцо заявленной петли: принятые точки от `startSeq` до `endSeq`. `nil` — петля заявлена не в этом процессе.
    public func ring(claimNo: Int) -> LoopRing? {
        guard let loop = claimed[claimNo] else { return nil }
        let coordinates = trail.filter { (loop.startSeq...loop.endSeq).contains($0.seq) }.map(\.coordinate)
        return LoopRing(claimNo: claimNo, loop: loop, coordinates: coordinates)
    }

    /// Сводка для записи в забег (переживает перезапуск).
    private var summary: RunSummary {
        var summary = RunSummary()
        summary.points = stats.points
        summary.acceptedPoints = stats.acceptedPoints
        summary.distanceMeters = stats.distanceMeters
        summary.breaks = Dictionary(uniqueKeysWithValues: stats.breaks.map { ($0.key.rawValue, $0.value) })
        summary.fogCells = stats.fogCells
        summary.fogAreaSquareMeters = stats.fogAreaSquareMeters
        summary.fogNewSquareMeters = stats.fogNewSquareMeters
        summary.fogNewIsLowerBound = stats.fogNewIsLowerBound
        summary.latitude = latitude
        summary.endedAtLimit = endedAtLimit
        return summary
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
    /// если GPS молчит (иначе предел сработал бы только на следующей точке). Заявки петель, не записанные раньше, —
    /// ещё раз.
    /// - Returns: `[.finishedAtLimit]`, если забег завершён по пределу; `.loopClaimed` — заявка записана со второй попытки.
    @discardableResult
    public func tick(now seconds: Double) async throws -> [RunEvent] {
        try await exclusively {
            guard !isFinished else { return [] }
            if StoragePrecision.milliseconds(seconds) > recorder.windowEndMs {
                try await finishNow(endedAt: lastTimestamp ?? Double(recorder.windowEndMs) / 1_000, atLimit: true)
                return [.finishedAtLimit]
            }
            try await recorder.tick(now: seconds)
            return try await claimUnsaved()
        }
    }

    /// С какого момента дозапрашивать датчики после перезапуска (секунды Unix): после уже отправленных, но не раньше окна
    /// забега — интервал шагомера, начатый раньше окна, запись отбросила бы целиком.
    public var sensorsResumeFrom: Double {
        get async {
            Double(max(await recorder.acceptsSensorsAfterMs + 1, await recorder.windowStartMs)) / 1_000
        }
    }

    /// Сколько кусков запечатано с начала (или продолжения) забега — по нему видно, что пора звать синхронизацию.
    public var sealedChunks: Int {
        get async { await recorder.sealedChunks }
    }

    /// Конец забега (игрок нажал «Стоп» или вышел предел длины). Ждёт точку, которая сейчас записывается.
    public func finish(endedAt seconds: Double) async throws {
        try await exclusively { try await finishNow(endedAt: seconds) }
    }

    private func finishNow(endedAt seconds: Double, atLimit: Bool = false) async throws {
        guard !isFinished else { return }
        _ = try await claimUnsaved()  // петля, замкнутая в забеге, заявляется до его конца — или «Финиш» не удаётся
        // Конец без разрыва: весь хвост тумана засчитан — до записи конца, чтобы последняя сводка в забеге была итоговой.
        // «Финиш» не записался — забег продолжается, и хвост снова ждёт своих 30 с.
        let before = (fog, fogPending, fogLast, stats, endedAtLimit)
        settleFog(olderThan: .max)
        endedAtLimit = atLimit
        await recorder.note(summary)
        do {
            try await recorder.finish(endedAt: seconds)
        } catch {
            (fog, fogPending, fogLast, stats, endedAtLimit) = before
            await recorder.note(summary)
            throw error
        }
        isFinished = true
    }
}
