import Foundation
import GameCore

/// Забег в очереди синхронизации. Идентификатор создаёт телефон, поэтому забег можно начать без сети.
///
/// Очередь — не история: после подтверждения сервером куски стираются, а след для экранов приложение хранит отдельно.
public struct LocalRun: Codable, Sendable, Hashable, Identifiable {
    public enum Source: String, Codable, Sendable { case live, replay }

    /// Что с забегом на сервере.
    public enum ServerState: Codable, Sendable, Hashable {
        /// Сервер о забеге ещё не знает (или «забыл» — ответил 404): нужен `POST /runs`.
        case unknown
        case started
        /// Сервер отказал окончательно (код в `rejectCode`): отправлять больше нечего.
        case rejected
    }

    public var id: UUID
    /// Чей забег (идентификатор игрока): после смены аккаунта забеги прежнего не уходят под новым.
    public var ownerId: String
    public var league: League
    public var source: Source
    public var configVersion: Int
    /// Начало забега по часам телефона, мс.
    public var startedAtMs: Int64
    public var deviceId: UUID
    public var appVersion: String
    public var motionAuthorized: Bool

    // Поля записи (меняет только `RunRecorder`).

    /// Номер последней точки в запечатанных кусках (−1 — ещё ни одного).
    public var recordedThroughSeq = -1
    /// Время последней записанной точки, мс.
    public var lastPointMs: Int64?
    /// Отметка полноты датчиков последнего запечатанного куска, мс.
    public var sealedSensorsMarkMs: Int64?
    /// Забег завершён на телефоне: известны конец и номер последней точки.
    public var endedAtMs: Int64?
    public var lastSeq: Int?

    // Поля синхронизации (меняет только `SyncEngine`).

    public var serverState: ServerState = .unknown
    public var rejectCode: String?
    public var finishSent = false
    /// Сервер окончательно отказал в завершении (код): дожидаться `missing` бессмысленно.
    public var finishRejectCode: String?
    /// Сервер подтвердил, что у него все точки (`missing` пуст) или дослать больше нечего: куски стёрты с телефона.
    public var confirmedComplete = false
    /// Сколько раз досылались точки или завершение (предел — `SyncEngine.maxResendRounds`).
    public var resendRounds = 0

    public init(
        id: UUID, ownerId: String, league: League, source: Source = .live, configVersion: Int, startedAtMs: Int64,
        deviceId: UUID, appVersion: String, motionAuthorized: Bool
    ) {
        self.id = id
        self.ownerId = ownerId
        self.league = league
        self.source = source
        self.configVersion = configVersion
        self.startedAtMs = startedAtMs
        self.deviceId = deviceId
        self.appVersion = appVersion
        self.motionAuthorized = motionAuthorized
    }

    public var isFinishedLocally: Bool { endedAtMs != nil && lastSeq != nil }
}

/// Откуда координаты точки: сервер хранит это как признак для доверия (runs.md, поле `flags`).
public struct PointSource: OptionSet, Codable, Sendable, Hashable {
    public let rawValue: Int

    public init(rawValue: Int) { self.rawValue = rawValue }

    /// Подставлены программой (`CLLocationSourceInformation.isSimulatedBySoftware`).
    public static let simulated = PointSource(rawValue: 1)
    /// От внешнего устройства (`isProducedByAccessory`).
    public static let accessory = PointSource(rawValue: 2)
}

/// Кусок забега. Границы фиксируются при запечатывании — повтор отправляет ровно то же.
public struct SealedChunk: Codable, Sendable, Hashable {
    public var runId: UUID
    public var firstSeq: Int
    /// Точки подряд с номера `firstSeq`, уже в точности хранения сервера.
    public var points: [TrackPoint]
    /// Источник каждой точки (тот же порядок, что у `points`).
    public var sources: [PointSource]
    public var motion: [MotionSample]
    public var steps: [PedometerSample]
    /// До какого момента (мс, часы телефона) переданы все данные датчиков.
    public var sensorsCompleteThroughMs: Int64
    public var sent = false

    public var lastSeq: Int { firstSeq + points.count - 1 }

    public init(
        runId: UUID, firstSeq: Int, points: [TrackPoint], sources: [PointSource]? = nil, motion: [MotionSample],
        steps: [PedometerSample], sensorsCompleteThroughMs: Int64
    ) {
        self.runId = runId
        self.firstSeq = firstSeq
        self.points = points
        self.sources = sources ?? Array(repeating: [], count: points.count)
        self.motion = motion
        self.steps = steps
        self.sensorsCompleteThroughMs = sensorsCompleteThroughMs
    }
}

/// Итог заявки петли, как его сообщил сервер.
public struct ClaimOutcome: Codable, Sendable, Hashable {
    /// `pending`, `applied`, `rejected`, `stale`, `failed`.
    public var status: String
    /// Чего ждёт ожидающая: `points`, `sensors`, `previous_claim`, `queue`.
    public var waitingFor: String?
    public var rejectCode: String?
    public var areaSquareMeters: Double

    public var isFinal: Bool { status != "pending" }
}

/// Заявка петли в очереди.
public struct PendingClaim: Codable, Sendable, Hashable {
    public var runId: UUID
    public var claimNo: Int
    public var loop: LoopClaim
    public var sent = false
    /// Сервер отказал в самой заявке (400, 409, лимит) — повторять бессмысленно.
    public var refusedCode: String?
    public var outcome: ClaimOutcome?

    public init(runId: UUID, claimNo: Int, loop: LoopClaim) {
        self.runId = runId
        self.claimNo = claimNo
        self.loop = loop
    }

    /// Больше ничего не ждём от сервера.
    public var isSettled: Bool { refusedCode != nil || outcome?.isFinal == true }
}
