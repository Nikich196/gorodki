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
    /// Судился ли забег на телефоне как забег новичка (порог точности `capture.newcomerMaxAccuracyMeters`): тем же
    /// порогом его судят после перезапуска приложения. Необязательное — старые записи очереди без него читаются.
    public var judgedAsNewcomer: Bool?

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
    /// Сводка для экрана, переживающая перезапуск (`RunSummary`): пишется при каждом запечатывании куска и на «Финише».
    /// Необязательное — старые записи очереди без неё читаются.
    public var summary: RunSummary?

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
    /// «+N га» забега от сервера — новые клетки тумана за всё время (`RunResponse.fogNewCells`); `nil` — сервер ещё
    /// не открыл туман по забегу или телефон ещё не спросил. Число меняется один раз (fog.md), после него не спрашиваем.
    public var fogNewCells: Int?

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

/// Сводка забега, которая переживает перезапуск приложения (docs/architecture/run-hud.md, пробел 1): `RunRecorder`
/// пишет её в забег при каждом запечатывании куска и на «Финише», `RunSession.resume` продолжает с неё. Итог и история
/// берут её же, без пересчёта. Петли в ней не хранятся: они — в заявках очереди. Незапечатанный хвост (до минуты)
/// теряется так же, как его точки.
public struct RunSummary: Codable, Sendable, Hashable {
    /// Записано точек (и отброшенных судьёй тоже).
    public var points = 0
    public var acceptedPoints = 0
    /// Путь по принятым точкам, м.
    public var distanceMeters = 0.0
    /// Разрывы следа по причинам: `TrackIssue.rawValue` → сколько раз.
    public var breaks: [String: Int] = [:]
    /// Весь туман забега на телефоне (не только новый). После перезапуска части до и после складываются — «≈».
    public var fogCells = 0
    public var fogAreaSquareMeters = 0.0
    /// Оценка «≈+N га»: новые клетки по сравнению с туманом игрока за всё время, м². Замораживается здесь: после «Финиша»
    /// сервер откроет туман забега, и пересчёт по кэшу дал бы почти ноль.
    public var fogNewSquareMeters = 0.0
    /// Были тайлы, туман за всё время которых неизвестен (нет сети): оценка — нижняя граница, «≥».
    public var fogNewIsLowerBound = false
    /// Широта первой принятой точки, округлённая до 0,01°: площадь клетки для «+N га» сервера (`fogNewCells`).
    public var latitude: Double?
    /// Забег завершился сам — вышел предел длины.
    public var endedAtLimit = false

    public init() {}

    private enum CodingKeys: String, CodingKey {
        case points, acceptedPoints, distanceMeters, breaks, fogCells, fogAreaSquareMeters, fogNewSquareMeters
        case fogNewIsLowerBound, latitude, endedAtLimit
    }

    /// Каждое поле необязательное: сводку, записанную прошлой версией приложения, новое поле не делает нечитаемой.
    public init(from decoder: any Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        points = try c.decodeIfPresent(Int.self, forKey: .points) ?? 0
        acceptedPoints = try c.decodeIfPresent(Int.self, forKey: .acceptedPoints) ?? 0
        distanceMeters = try c.decodeIfPresent(Double.self, forKey: .distanceMeters) ?? 0
        breaks = try c.decodeIfPresent([String: Int].self, forKey: .breaks) ?? [:]
        fogCells = try c.decodeIfPresent(Int.self, forKey: .fogCells) ?? 0
        fogAreaSquareMeters = try c.decodeIfPresent(Double.self, forKey: .fogAreaSquareMeters) ?? 0
        fogNewSquareMeters = try c.decodeIfPresent(Double.self, forKey: .fogNewSquareMeters) ?? 0
        fogNewIsLowerBound = try c.decodeIfPresent(Bool.self, forKey: .fogNewIsLowerBound) ?? false
        latitude = try c.decodeIfPresent(Double.self, forKey: .latitude)
        endedAtLimit = try c.decodeIfPresent(Bool.self, forKey: .endedAtLimit) ?? false
    }
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
    /// «Взятое»: ничья земля и перешедшие L1 (`claimedNeutral + transferred`), м² — показывать можно сразу.
    public var areaSquareMeters: Double
    /// Площадь по видам, м²: ключи — имена `PieceOutcome` в camelCase (`claimedNeutral`, `transferred`, `cracked`,
    /// `refreshed`…). Словарь строк: незнакомый ключ новой версии сервера разбор не роняет. `nil` — сервер её ещё
    /// не прислал: она приходит только после границы публичности (captures.md) — до этого в итоге «позже».
    /// Необязательное — старые записи очереди без неё читаются.
    public var areaByOutcome: [String: Double]?

    public init(
        status: String, waitingFor: String? = nil, rejectCode: String? = nil, areaSquareMeters: Double = 0,
        areaByOutcome: [String: Double]? = nil
    ) {
        self.status = status
        self.waitingFor = waitingFor
        self.rejectCode = rejectCode
        self.areaSquareMeters = areaSquareMeters
        self.areaByOutcome = areaByOutcome
    }

    public var isFinal: Bool { status != "pending" }
}

/// Заявка петли в очереди.
public struct PendingClaim: Codable, Sendable, Hashable {
    /// Код решения заявок забега, отвергнутого сервером (`LocalRun.rejectCode`): их больше не отправят, и итог забега
    /// не должен ждать их вечно. Код телефона, а не сервера: причина — в забеге.
    public static let runRejectedCode = "run_rejected"

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
