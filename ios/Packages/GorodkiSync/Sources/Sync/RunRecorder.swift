import Foundation
import GameCore

/// Когда запечатывать кусок и какие пределы сервера соблюдать.
public struct ChunkPolicy: Sendable, Hashable {
    /// Точек в куске — не больше (сервер принимает до 1 200).
    public var maxPoints = 120
    /// Кусок запечатывается не реже, чем раз в это время, секунды.
    public var maxAgeSeconds = 60.0
    /// Предел длины забега — из игрового конфига (`capture.maxRunHours`).
    public var maxRunHours = 4.0
    /// Записей движения и шагомера в куске — не больше (сервер: 1 200 каждого вида).
    public var maxSamples = 1_200

    public init() {}

    /// Та же политика с пределом длины забега из правил версии конфига.
    public func limited(by rules: PhoneRules) -> ChunkPolicy {
        var policy = self
        policy.maxRunHours = rules.maxRunHours
        return policy
    }

    // Правила сервера (TrackChunkRules, RunLimits).
    /// Точки и датчики чуть раньше старта допустимы: GPS «догоняет» после нажатия «Старт».
    static let earlyToleranceMs: Int64 = 60_000
    /// Запас после предела длины забега.
    static let lateToleranceMs: Int64 = 600_000
    static let maxPointsPerSecond = 2.0
}

public enum RecorderError: Error, Equatable {
    /// Точки должны идти подряд с номерами без пропусков (их нумерует движок трекинга).
    case outOfOrder(expected: Int, got: Int)
    /// Время точки должно строго расти в целых миллисекундах. Номер не израсходован: движок трекинга отбрасывает точку.
    case timeNotIncreasing
    /// Точка вне окна забега: раньше начала больше чем на минуту или после предела длины — пора завершать забег.
    case outsideRunWindow
    /// Петля заявлена на точки, которых ещё нет.
    case loopBeyondRecorded
    /// Петля короче трёх отрезков — сервер такую не примет.
    case loopTooShort
    /// Петля с этим концом уже заявлена: сервер узнаёт заявку по концу петли.
    case loopAlreadyClaimed
    case alreadyFinished
}

/// Запись забега на телефоне: точки подряд, данные датчиков, заявки петель → запечатанные куски в очереди.
///
/// Куски запечатываются раз в минуту, по 120 точек или сразу при петле (PLAN.md, §7.2: «петля → немедленная отправка
/// чанка и /loops»). Всё, что сервер отверг бы целым куском (TrackChunkRules), отсекается здесь по одной записи:
/// точки вне окна забега и с неубывающим временем — ошибкой, датчики вне окна и сверх предела — молча, со счётчиком.
/// Датчики только дописываются: запись не позже отметки уже запечатанного куска сервер отбросит как запоздавшую.
public actor RunRecorder {
    public let runId: UUID
    private let store: any SyncStore
    private let policy: ChunkPolicy
    /// Начало окна забега (мс): старт минус минута.
    public let windowStartMs: Int64
    /// Конец окна забега (мс): точки позже — не этого забега, пора завершать.
    public let windowEndMs: Int64
    private let maxSeq: Int

    private var buffer: [TrackPoint] = []
    private var sources: [PointSource] = []
    private var motion: [MotionSample] = []
    private var steps: [PedometerSample] = []
    /// «Текущий» вид движения, начавшийся до окна забега (CoreMotion сообщает его с давним временем начала):
    /// уходит с первым куском со временем начала окна.
    private var motionBeforeWindow: MotionSample?
    private var bufferStartedAt: Double?
    /// Номер, который должна иметь следующая точка.
    public private(set) var nextSeq = 0
    /// Время последней записанной точки, мс.
    public private(set) var lastPointMs: Int64?
    private var nextClaimNo = 0
    private var claimedEnds: Set<Int> = []
    private var sensorsMarkMs: Int64
    /// Датчики принимаются только позже этого момента (мс).
    public private(set) var acceptsSensorsAfterMs: Int64
    private var finished = false
    /// Конец забега начался: пока запечатывается остаток, новые точки и датчики не принимаются — иначе точка вошла бы
    /// в `lastSeq`, но не в кусок, и у сервера навсегда не хватало бы её.
    private var finishing = false
    /// Сколько кусков запечатано с начала (или продолжения) записи.
    public private(set) var sealedChunks = 0
    /// Сводка для экрана (`RunSummary`), которую следующее запечатывание запишет в забег вместе с прогрессом.
    private var summary: RunSummary?

    /// Записи датчиков, пришедшие не позже отметки уже запечатанного куска.
    public private(set) var lateSensorRecords = 0
    /// Записи датчиков вне окна забега, неверные или сверх предела куска.
    public private(set) var droppedSensorRecords = 0

    private init(run: LocalRun, store: any SyncStore, policy: ChunkPolicy) {
        self.runId = run.id
        self.store = store
        self.policy = policy
        windowStartMs = run.startedAtMs - ChunkPolicy.earlyToleranceMs
        windowEndMs = run.startedAtMs + Int64(policy.maxRunHours * 3_600_000) + ChunkPolicy.lateToleranceMs
        maxSeq = Int(policy.maxRunHours * 3_600 * ChunkPolicy.maxPointsPerSecond)
        // До первого куска сервер принимает датчики с начала окна; отметка «ничего не обещаю» — начало окна.
        acceptsSensorsAfterMs = windowStartMs - 1
        sensorsMarkMs = windowStartMs
    }

    /// Начать запись: забег попадает в очередь (сервер узнает о нём при первой синхронизации). Прерванные забеги
    /// (приложение выгрузили посреди записи) перед этим закрываются — и чужие: на телефоне идёт один забег, а петли
    /// незакрытого ждали бы датчиков вечно.
    public static func begin(
        _ run: LocalRun, store: any SyncStore, policy: ChunkPolicy = ChunkPolicy()
    ) async throws -> RunRecorder {
        try await closeInterrupted(except: nil, store: store)
        try await store.insert(run)
        return RunRecorder(run: run, store: store, policy: policy)
    }

    /// Продолжить запись после перезапуска приложения — с номера после последней запечатанной точки. Незапечатанный
    /// хвост (до минуты) теряется, но дыры в номерах нет. `nil` — забега нет, он завершён или отвергнут сервером.
    public static func resume(
        runId: UUID, store: any SyncStore, policy: ChunkPolicy = ChunkPolicy()
    ) async throws -> RunRecorder? {
        guard let run = try await store.runs().first(where: { $0.id == runId }), !run.isFinishedLocally,
            run.serverState != .rejected
        else { return nil }
        let chunks = try await store.chunks(of: runId)
        let claims = try await store.claims(of: runId)
        // Номер новой заявки — не по прочитанным: нечитаемая заявка тоже заняла свой (`SyncStore.lastClaimNo`).
        let lastClaimNo = try await store.lastClaimNo(of: runId)
        let recorder = RunRecorder(run: run, store: store, policy: policy)
        let last = lastRecorded(run, chunks)
        await recorder.restore(
            recordedThroughSeq: last.seq, lastPointMs: last.pointMs,
            sealedMarkMs: ([run.sealedSensorsMarkMs] + chunks.map(\.sensorsCompleteThroughMs)).compactMap { $0 }.max(),
            claims: claims, lastClaimNo: lastClaimNo)
        return recorder
    }

    /// Закрыть незавершённые забеги (любого игрока), кроме `except`: конец — последняя записанная точка, последний
    /// номер — последний запечатанный. Вызывается при старте нового забега и при запуске приложения (`RunTracker.recover`).
    /// - Parameter matching: какие забеги закрывать — при запуске только своего вида (пробные «Лаборатории» или игрока).
    public static func closeInterrupted(
        except kept: UUID?, store: any SyncStore, matching: (LocalRun) -> Bool = { _ in true }
    ) async throws {
        for run in try await store.runs()
        where run.id != kept && !run.isFinishedLocally && run.serverState != .rejected && matching(run) {
            let last = lastRecorded(run, try await store.chunks(of: run.id))
            try await store.updateRun(run.id) {
                $0.recordedThroughSeq = last.seq
                $0.lastPointMs = last.pointMs
                $0.endedAtMs = last.pointMs ?? $0.startedAtMs
                $0.lastSeq = last.seq
            }
        }
    }

    /// Номер и время последней запечатанной точки — по прогрессу в забеге и по последнему куску. Прогресс нужен: куски
    /// могли уже уйти и стереться (или быть отвергнуты). Кусок тоже: версии до `SyncStore.seal` писали кусок и прогресс
    /// двумя записями, и выгрузка между ними оставляла кусок без прогресса — по одному прогрессу последний номер вышел бы
    /// меньше, чем в куске, и сервер отказал бы в завершении.
    private static func lastRecorded(_ run: LocalRun, _ chunks: [SealedChunk]) -> (seq: Int, pointMs: Int64?) {
        let last = chunks.last
        let pointMs = [run.lastPointMs, last?.points.last.map { StoragePrecision.milliseconds($0.timestamp) }]
        return (max(run.recordedThroughSeq, last?.lastSeq ?? -1), pointMs.compactMap { $0 }.max())
    }

    private func restore(
        recordedThroughSeq: Int, lastPointMs: Int64?, sealedMarkMs: Int64?, claims: [PendingClaim], lastClaimNo: Int?
    ) {
        nextSeq = recordedThroughSeq + 1
        self.lastPointMs = lastPointMs
        nextClaimNo = (lastClaimNo ?? -1) + 1
        claimedEnds = Set(claims.map(\.loop.endSeq))
        if let sealedMarkMs {
            acceptsSensorsAfterMs = max(acceptsSensorsAfterMs, sealedMarkMs)
            sensorsMarkMs = max(sensorsMarkMs, sealedMarkMs)
        }
    }

    // MARK: - Запись

    /// Точка следа — уже пронумерованная движком трекинга (подряд с нуля); сохраняется в точности хранения сервера.
    public func record(_ point: TrackPoint, source: PointSource = []) async throws {
        guard !finished, !finishing else { throw RecorderError.alreadyFinished }
        guard point.seq == nextSeq else { throw RecorderError.outOfOrder(expected: nextSeq, got: point.seq) }
        let stored = point.quantizedForStorage()
        let ms = StoragePrecision.milliseconds(stored.timestamp)
        guard ms >= windowStartMs, ms <= windowEndMs, point.seq <= maxSeq else {
            throw RecorderError.outsideRunWindow
        }
        if let lastPointMs, ms <= lastPointMs {
            throw RecorderError.timeNotIncreasing
        }
        buffer.append(stored)
        sources.append(source)
        nextSeq += 1
        lastPointMs = ms
        bufferStartedAt = bufferStartedAt ?? stored.timestamp
        let aged = stored.timestamp - (bufferStartedAt ?? stored.timestamp) >= policy.maxAgeSeconds
        let full =
            buffer.count >= policy.maxPoints || motion.count >= policy.maxSamples || steps.count >= policy.maxSamples
        if aged || full {
            try await seal()
        }
    }

    /// - Returns: запись уйдёт на сервер (судье телефона — только такие).
    @discardableResult
    public func record(_ sample: MotionSample) -> Bool {
        guard !finished, !finishing else { return drop() }
        let stored = sample.quantizedForStorage()
        let ms = StoragePrecision.milliseconds(stored.timestamp)
        if ms < windowStartMs, acceptsSensorsAfterMs < windowStartMs {
            // Вид движения, начавшийся до окна, ещё действует в его начале — сохраняется самый поздний такой.
            if motionBeforeWindow.map({ $0.timestamp <= stored.timestamp }) ?? true {
                motionBeforeWindow = stored
            }
            return true
        }
        guard ms <= windowEndMs else { return drop() }
        guard ms > acceptsSensorsAfterMs else { return late() }
        guard motion.count < policy.maxSamples else { return drop() }
        motion.append(stored)
        return true
    }

    /// - Returns: запись уйдёт на сервер (судье телефона — только такие).
    @discardableResult
    public func record(_ sample: PedometerSample) -> Bool {
        guard !finished, !finishing else { return drop() }
        let stored = sample.quantizedForStorage()
        let start = StoragePrecision.milliseconds(stored.start)
        let end = StoragePrecision.milliseconds(stored.end)
        // Интервал, начатый до окна, не обрезается: те же шаги за меньшее время исказили бы темп.
        guard end >= start, (stored.steps ?? 0) >= 0, start >= windowStartMs, end <= windowEndMs else { return drop() }
        guard end > acceptsSensorsAfterMs else { return late() }
        guard steps.count < policy.maxSamples else { return drop() }
        steps.append(stored)
        return true
    }

    private func late() -> Bool {
        lateSensorRecords += 1
        return false
    }

    private func drop() -> Bool {
        droppedSensorRecords += 1
        return false
    }

    /// Сводка забега на сейчас (её ведёт `RunSession`): уйдёт в забег со следующим запечатанным куском и с концом забега —
    /// так она переживает перезапуск приложения (docs/architecture/run-hud.md, пробел 1).
    public func note(_ summary: RunSummary) {
        self.summary = summary
    }

    /// Телефон уже получил все данные датчиков до этого момента (секунды Unix): CoreMotion и шагомер отдают их с задержкой.
    public func sensorsComplete(through seconds: Double) {
        sensorsMarkMs = max(sensorsMarkMs, StoragePrecision.milliseconds(seconds))
    }

    /// Запечатать кусок, если он «созрел» по времени (вызывается таймером).
    public func tick(now seconds: Double) async throws {
        if let started = bufferStartedAt, seconds - started >= policy.maxAgeSeconds {
            try await seal()
        }
    }

    /// Заявка петли: кусок с концом петли запечатывается сразу, заявка встаёт в очередь. Возвращает номер заявки.
    @discardableResult
    public func claim(_ loop: LoopClaim) async throws -> Int {
        guard loop.startSeq >= 0, loop.endSeq < nextSeq else { throw RecorderError.loopBeyondRecorded }
        guard loop.endSeq - loop.startSeq >= 3 else { throw RecorderError.loopTooShort }
        guard !claimedEnds.contains(loop.endSeq) else { throw RecorderError.loopAlreadyClaimed }
        try await seal()
        let claim = PendingClaim(runId: runId, claimNo: nextClaimNo, loop: loop)
        try await store.save(claim)
        nextClaimNo += 1
        claimedEnds.insert(loop.endSeq)
        return claim.claimNo
    }

    /// Конец забега: остаток запечатывается, забег получает время конца и номер последней точки.
    public func finish(endedAt seconds: Double) async throws {
        guard !finished else { return }
        finishing = true  // до первого ожидания: точка, пришедшая во время записи остатка, не войдёт в `lastSeq`
        let endedAtMs = StoragePrecision.milliseconds(seconds)
        do {
            try await seal()  // сначала все куски, потом отметка конца: увидев конец, синхронизация видит и все куски
            let lastSeq = nextSeq - 1
            let summary = self.summary
            try await store.updateRun(runId) {
                $0.endedAtMs = endedAtMs
                $0.lastSeq = lastSeq
                if let summary {
                    $0.summary = summary  // последняя сводка — итоговая: итог и история берут её без пересчёта
                }
            }
        } catch {
            // «Финиш» не удался (не записан остаток или сам конец) — забег продолжается, точки снова принимаются.
            finishing = false
            throw error
        }
        finished = true
    }

    /// Запечатать буфер. Буфер забирается и очищается **до** записи в хранилище: пока запись идёт, актор принимает
    /// следующие точки и датчики (реентерабельность), и они должны попасть в следующий кусок, а не пропасть при очистке.
    /// Запись не удалась — забранное возвращается в начало буфера.
    private func seal() async throws {
        guard let first = buffer.first else { return }
        var chunkMotion = motion
        if let early = motionBeforeWindow {
            chunkMotion.insert(
                MotionSample(timestamp: Double(windowStartMs) / 1_000, activity: early.activity), at: 0)
        }
        let mark = max(sensorsMarkMs, acceptsSensorsAfterMs)
        let chunk = SealedChunk(
            runId: runId, firstSeq: first.seq, points: buffer, sources: sources, motion: chunkMotion, steps: steps,
            sensorsCompleteThroughMs: mark)
        let taken = (
            buffer: buffer, sources: sources, motion: motion, steps: steps, early: motionBeforeWindow,
            startedAt: bufferStartedAt, accepts: acceptsSensorsAfterMs
        )
        buffer = []
        sources = []
        motion = []
        steps = []
        motionBeforeWindow = nil
        bufferStartedAt = nil
        acceptsSensorsAfterMs = mark  // датчики не позже отметки уже обещаны этим куском
        let lastSeq = chunk.lastSeq
        let lastPointMs = taken.buffer.last.map { StoragePrecision.milliseconds($0.timestamp) }
        let summary = self.summary
        do {
            try await store.seal(chunk) {
                $0.recordedThroughSeq = max($0.recordedThroughSeq, lastSeq)
                $0.lastPointMs = max($0.lastPointMs ?? .min, lastPointMs ?? .min)
                $0.sealedSensorsMarkMs = max($0.sealedSensorsMarkMs ?? .min, mark)
                if let summary {
                    $0.summary = summary
                }
            }
            sealedChunks += 1
        } catch {
            buffer = taken.buffer + buffer
            sources = taken.sources + sources
            motion = taken.motion + motion
            steps = taken.steps + steps
            motionBeforeWindow = taken.early ?? motionBeforeWindow
            bufferStartedAt = taken.startedAt ?? bufferStartedAt
            acceptsSensorsAfterMs = taken.accepts
            throw error
        }
    }
}
