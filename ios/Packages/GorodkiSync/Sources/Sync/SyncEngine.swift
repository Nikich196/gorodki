import Foundation
import GameCore
import GorodkiAPI
import HTTPTypes
import OpenAPIRuntime

/// Почему проход синхронизации остановился раньше времени.
public enum SyncStop: Equatable, Sendable {
    /// Нет сети или сервер не ответил — повторить позже.
    case offline
    /// Нужно войти заново (истёк вход).
    case unauthorized
    /// Часы телефона сбиты больше чем на 12 часов — попросить включить автоматическое время.
    case clockInvalid
    /// Аккаунт удаляется — данные больше не отправляются.
    case accountDeleting
    /// Слишком частые запросы — повторить позже.
    case rateLimited
}

/// Заявка, решённая в проходе синхронизации (docs/architecture/run-hud.md, пробел 9): вторая фаза церемонии и итог
/// забега узнают о решении отсюда, а не из молчаливой записи в хранилище.
public struct SettledClaim: Equatable, Sendable {
    public var runId: UUID
    public var claimNo: Int
    /// Окончательный итог сервера; `nil` — отказ в самой заявке.
    public var outcome: ClaimOutcome?
    /// Отказ в самой заявке (`claim_invalid`, `claim_conflict`, `upload_window_closed`, `claim_limit`) или
    /// `PendingClaim.runRejectedCode` — забег отвергнут сервером, заявку больше не отправят.
    public var refusedCode: String?

    public init(_ claim: PendingClaim) {
        runId = claim.runId
        claimNo = claim.claimNo
        outcome = claim.outcome
        refusedCode = claim.refusedCode
    }
}

/// Что сделал проход синхронизации.
public struct SyncReport: Equatable, Sendable {
    public var startedRuns = 0
    /// Забеги, отложенные до следующего прохода (лимит забегов в сутки, сбитые назад часы — начало или точки «из будущего»).
    public var deferredRuns = 0
    public var uploadedChunks = 0
    /// Куски, которые у сервера уже были (повтор после потерянного ответа).
    public var duplicateChunks = 0
    /// Куски, перерезанные после 409: дошлются только номера, которых у сервера нет.
    public var splitChunks = 0
    /// Куски, из которых убраны отвергнутые сервером данные датчиков (точки ушли без них).
    public var strippedChunks = 0
    /// Куски, которые сервер не примет никогда (неверные точки, лимит забега, окно закрыто).
    public var droppedChunks = 0
    public var sentClaims = 0
    public var finishedRuns = 0
    /// Куски, снова поставленные в очередь по списку `missing`.
    public var requeuedChunks = 0
    /// Забеги, которых сервер не знает (404): следующий проход начнёт их заново.
    public var forgottenRuns = 0
    /// Завершения, которых сервер ещё не видит (забег у него не завершён): следующий проход повторит их.
    public var unconfirmedFinishes = 0
    /// Исчерпан суточный объём: куски ждут завтрашнего прохода, заявки на уже доставленные точки ушли.
    public var storageLimitReached = false
    /// Заявки, решённые в этом проходе, — каждая ровно в одном проходе: окончательный итог (из ответа на заявку или из
    /// `GET …/captures`, в том числе `failed` у забега, которого сервер не знает), отказ в самой заявке, заявки забега,
    /// отвергнутого в этом проходе.
    public var settledClaims: [SettledClaim] = []
    public var stop: SyncStop?

    public init() {}
}

/// Доставка очереди на сервер по контракту (docs/architecture/runs.md, captures.md): старт забега → куски по порядку,
/// заявка петли — сразу, как доставлены её точки → завершение → проверка, что у сервера все точки. Любой запрос безопасно
/// повторить, поэтому проход можно прервать где угодно (нет сети, приложение выгружено) и начать заново: состояние после
/// каждого ответа уже в хранилище. Одновременно идёт только один проход.
public actor SyncEngine {
    /// Сколько раз досылать точки по списку `missing` (или завершение), прежде чем признать дыру неустранимой.
    public static let maxResendRounds = 3
    /// Сколько дней после начала забега спрашивать «+N га» (`fogNewCells`): точки забега сервер хранит 14 дней
    /// (runs.md, PLAN.md §3.16) — без точек туман по забегу уже не откроется.
    public static let fogQueryDays = 14.0

    private let store: any SyncStore
    private let api: any APIProtocol
    private let ownerId: String
    private let now: @Sendable () -> Double
    private let signedInPlayer: (@Sendable () async -> String?)?
    private var current: Task<SyncReport, Never>?

    /// - Parameters:
    ///   - ownerId: вошедший игрок — синхронизируются только его забеги.
    ///   - now: «сейчас» по часам телефона, секунды Unix (уходит в каждом запросе как `sentAtMs`).
    ///   - signedInPlayer: кто вошёл сейчас. Клиент API подставляет токен текущего входа, поэтому после смены
    ///     аккаунта прежний движок слал бы забеги прежнего игрока с чужим токеном — перед каждым запросом он
    ///     сверяется и, если вошёл другой, останавливается (`unauthorized`).
    public init(
        store: any SyncStore, api: any APIProtocol, ownerId: String, now: @escaping @Sendable () -> Double,
        signedInPlayer: (@Sendable () async -> String?)? = nil
    ) {
        self.store = store
        self.api = api
        self.ownerId = ownerId
        self.now = now
        self.signedInPlayer = signedInPlayer
    }

    /// Проход по всей очереди. Если проход уже идёт (таймер и возврат сети сработали вместе), второй вызов дожидается
    /// его и получает тот же отчёт — два прохода вперемешку задвоили бы запросы и данные датчиков.
    public func syncOnce() async -> SyncReport {
        if let current {
            return await current.value
        }
        let task = Task { await self.pass() }
        current = task
        let report = await task.value
        current = nil
        return report
    }

    private func pass() async -> SyncReport {
        var report = SyncReport()
        do {
            // От раннего забега к позднему: сервер закрывает более ранний активный забег, когда начинается следующий.
            for run in try await ownRuns() where run.serverState != .rejected && !run.confirmedComplete {
                try await sync(run, &report)
            }
            for run in try await ownRuns() where run.serverState == .started {
                try await refreshClaims(of: run, &report)
            }
        } catch let stop as Stop {
            report.stop = stop.reason
        } catch {
            report.stop = .offline  // хранилище не ответило — попробовать в следующий раз
        }
        return report
    }

    private func ownRuns() async throws -> [LocalRun] {
        try await store.runs().filter { $0.ownerId == ownerId }.sorted { $0.startedAtMs < $1.startedAtMs }
    }

    // MARK: - Забег

    /// Что дальше с забегом в этом проходе.
    private enum Step {
        case next
        /// Сервер не знает забега (404): следующий проход начнёт его заново.
        case forgotten
        /// Отложить забег до следующего прохода.
        case later
    }

    private func sync(_ run: LocalRun, _ report: inout SyncReport) async throws {
        var run = run
        if run.serverState == .unknown {
            guard try await start(&run, &report) == .next else { return }
        }
        guard try await uploadChunks(&run, &report) == .next else { return }
        guard run.isFinishedLocally, try await store.chunks(of: run.id).allSatisfy(\.sent) else { return }
        if !run.finishSent {
            guard try await finish(&run, &report) == .next else { return }
        }
        try await confirmComplete(&run, &report)
    }

    /// `POST /runs`.
    private func start(_ run: inout LocalRun, _ report: inout SyncReport) async throws -> Step {
        let body = Components.Schemas.StartRunRequest(
            id: Self.string(run.id),
            league: Components.Schemas.League(rawValue: run.league.rawValue) ?? .run,
            source: run.source == .replay ? .replay : .live,
            configVersion: Int32(clamping: run.configVersion),
            startedAtMs: run.startedAtMs,
            sentAtMs: nowMs(),
            deviceId: Self.string(run.deviceId),
            appVersion: run.appVersion,
            motionAuthorized: run.motionAuthorized)
        let output = try await call { try await api.startRun(body: .json(body)) }
        let code: String?
        switch output {
        case .created, .ok:
            try await update(&run) { $0.serverState = .started }
            report.startedRuns += 1
            return .next
        case .badRequest(let response):
            code = try response.body.application_problem_plus_json.code
            switch code {
            case "device_clock_invalid":
                throw Stop(.clockInvalid)
            case "start_in_future":
                // Часы переведены назад после начала забега: со временем начало перестанет быть «в будущем».
                report.deferredRuns += 1
                return .later
            case "run_too_old":
                // Проверка возраста идёт на сервере раньше поиска повтора: забег мог быть принят, а ответ — потеряться.
                return try await startedEarlier(&run, code, &report)
            default:
                break
            }
        case .conflict(let response):
            code = try response.body.application_problem_plus_json.code
            return try await startedEarlier(&run, code, &report)
        case .forbidden(let response):
            let problem = try response.body.application_problem_plus_json
            try stopIfAccountDeleting(problem)
            code = problem.code
        case .unprocessableContent(let response):
            code = try response.body.application_problem_plus_json.code
        case .tooManyRequests(let response):
            guard try response.body.application_problem_plus_json.code == "daily_run_limit" else {
                throw Stop(.rateLimited)
            }
            report.deferredRuns += 1  // остальные забеги очереди это не задерживает
            return .later
        case .undocumented(let status, _):
            throw Self.stop(status)
        }
        try await reject(&run, code, &report)
        return .later
    }

    /// Отказ в старте, который бывает и у уже принятого забега: `GET /runs/{id}` покажет, есть ли он у сервера.
    private func startedEarlier(_ run: inout LocalRun, _ code: String?, _ report: inout SyncReport) async throws -> Step
    {
        let output = try await call { try await api.getRun(path: .init(runId: Self.string(run.id))) }
        switch output {
        case .ok:
            try await update(&run) { $0.serverState = .started }
            report.startedRuns += 1
            return .next
        case .notFound:
            try await reject(&run, code, &report)
            return .later
        case .undocumented(let status, _):
            throw Self.stop(status)
        }
    }

    /// Отказ окончательный: забег больше не отправляется, его куски стираются (очередь — не история). Его заявки больше
    /// не уйдут — они решены (`PendingClaim.runRejectedCode`): иначе итог забега ждал бы их вечно.
    private func reject(_ run: inout LocalRun, _ code: String?, _ report: inout SyncReport) async throws {
        try await update(&run) {
            $0.serverState = .rejected
            $0.rejectCode = code ?? "rejected"
        }
        try await store.deleteChunks(of: run.id)  // все, с нечитаемыми: по прочитанному они остались бы навсегда
        for var claim in try await store.claims(of: run.id) where !claim.isSettled {
            claim.refusedCode = PendingClaim.runRejectedCode
            try await store.save(claim)
            report.settledClaims.append(SettledClaim(claim))
        }
    }

    // MARK: - Куски

    /// Шлёт неотправленные куски по порядку, включая появившиеся после разрезания; после каждого — заявки, чьи точки
    /// уже доставлены.
    private func uploadChunks(_ run: inout LocalRun, _ report: inout SyncReport) async throws -> Step {
        guard try await sendReadyClaims(&run, &report) == .next else { return .forgotten }
        // Каждый ответ либо закрывает кусок, либо уменьшает число неотправленных точек — цикл конечен;
        // предел — страховка от ошибки в этой логике.
        for _ in 0..<10_000 where !report.storageLimitReached {
            guard let chunk = try await store.chunks(of: run.id).first(where: { !$0.sent }) else { break }
            switch try await upload(chunk, &report) {
            case .next:
                guard try await sendReadyClaims(&run, &report) == .next else { return .forgotten }
            case .forgotten:
                try await forget(&run, &report)
                return .forgotten
            case .later:
                return .later
            }
        }
        return .next
    }

    /// `PUT /runs/{id}/chunks/{firstSeq}`.
    private func upload(_ chunk: SealedChunk, _ report: inout SyncReport) async throws -> Step {
        let body = Self.request(for: chunk, sentAtMs: nowMs())
        let output = try await call {
            try await api.uploadChunk(
                path: .init(runId: Self.string(chunk.runId), firstSeq: Int32(clamping: chunk.firstSeq)),
                body: .json(body))
        }
        switch output {
        case .created:
            try await markSent(chunk)
            report.uploadedChunks += 1
        case .ok:
            try await markSent(chunk)
            report.duplicateChunks += 1
        case .conflict(let response):
            let problem = try response.body.application_problem_plus_json
            if problem.code == "chunk_conflict" {
                try await split(chunk, around: Self.ranges(problem.additionalProperties["overlaps"]))
                report.splitChunks += 1
            } else {
                // upload_window_closed: сервер больше не примет ни одного куска этого забега.
                report.droppedChunks += try await dropUnsent(of: chunk.runId)
            }
        case .notFound:
            return .forgotten
        case .badRequest(let response):
            let problems = Self.problems(
                try response.body.application_problem_plus_json.additionalProperties["problems"])
            if problems.contains(where: { $0.rule == "time_future" }) {
                report.deferredRuns += 1  // часы переведены назад: те же точки станут «не из будущего» позже
                return .later
            }
            if !problems.isEmpty, problems.allSatisfy({ $0.field.hasPrefix("motion") || $0.field.hasPrefix("steps") }) {
                // Испорчены только датчики: точки уходят без них — иначе пропала бы вся дальнейшая территория забега.
                var stripped = chunk
                stripped.motion = []
                stripped.steps = []
                try await store.replaceChunk(of: chunk.runId, firstSeq: chunk.firstSeq, with: [stripped])
                report.strippedChunks += 1
            } else {
                try await store.deleteChunk(of: chunk.runId, firstSeq: chunk.firstSeq)
                report.droppedChunks += 1
            }
        case .contentTooLarge:
            report.droppedChunks += try await dropUnsent(of: chunk.runId)  // run_storage_limit: забег исчерпал лимит
        case .forbidden(let response):
            try stopIfAccountDeleting(response.body.application_problem_plus_json)
            throw Stop(.offline)
        case .tooManyRequests(let response):
            guard try response.body.application_problem_plus_json.code == "daily_storage_limit" else {
                throw Stop(.rateLimited)
            }
            report.storageLimitReached = true  // куски — завтра; заявки на доставленные точки уйдут и сейчас
            return .later
        case .undocumented(let status, _):
            throw Self.stop(status)
        }
        return .next
    }

    private func markSent(_ chunk: SealedChunk) async throws {
        var sent = chunk
        sent.sent = true
        try await store.save(sent)
    }

    /// После 409: номера, которые у сервера уже есть, не шлём; непокрытые — новыми кусками из тех же точек (одной
    /// транзакцией). Данные датчиков уходят с первым из новых кусков.
    private func split(_ chunk: SealedChunk, around taken: [ClosedRange<Int>]) async throws {
        let free = zip(chunk.points, chunk.sources).filter { point, _ in !taken.contains { $0.contains(point.seq) } }
        guard !free.isEmpty, free.count < chunk.points.count else {
            // Всё уже у сервера — кусок доставлен. (Если сервер не назвал ни одного занятого номера, это тоже
            // считается доставкой: дыру найдёт проверка `missing` после завершения, число её повторов ограничено.)
            try await markSent(chunk)
            return
        }
        var pieces: [[(TrackPoint, PointSource)]] = []
        for item in free {
            if let last = pieces.last?.last, last.0.seq + 1 == item.0.seq {
                pieces[pieces.count - 1].append(item)
            } else {
                pieces.append([item])
            }
        }
        let chunks = pieces.enumerated().map { index, piece in
            SealedChunk(
                runId: chunk.runId, firstSeq: piece[0].0.seq, points: piece.map(\.0), sources: piece.map(\.1),
                motion: index == 0 ? chunk.motion : [], steps: index == 0 ? chunk.steps : [],
                sensorsCompleteThroughMs: chunk.sensorsCompleteThroughMs)
        }
        try await store.replaceChunk(of: chunk.runId, firstSeq: chunk.firstSeq, with: chunks)
    }

    private func dropUnsent(of runId: UUID) async throws -> Int {
        var dropped = 0
        for chunk in try await store.chunks(of: runId) where !chunk.sent {
            try await store.deleteChunk(of: runId, firstSeq: chunk.firstSeq)
            dropped += 1
        }
        return dropped
    }

    // MARK: - Заявки петель

    /// Шлёт заявки, чьи точки уже доставлены (все куски до конца петли отправлены): чем раньше заявка у сервера,
    /// тем раньше её обработают, а опоздавшую на 3 часа сервер не засчитает.
    private func sendReadyClaims(_ run: inout LocalRun, _ report: inout SyncReport) async throws -> Step {
        let chunks = try await store.chunks(of: run.id)
        for claim in try await store.claims(of: run.id) where !claim.sent && claim.refusedCode == nil {
            guard chunks.allSatisfy({ $0.sent || $0.firstSeq > claim.loop.endSeq }) else { continue }
            guard try await send(claim, &report) == .next else {
                try await forget(&run, &report)
                return .forgotten
            }
        }
        return .next
    }

    /// `POST /runs/{id}/loops`.
    private func send(_ claim: PendingClaim, _ report: inout SyncReport) async throws -> Step {
        let body = Components.Schemas.LoopClaimRequest(
            claimNo: Int32(clamping: claim.claimNo),
            startSeq: Int32(clamping: claim.loop.startSeq),
            endSeq: Int32(clamping: claim.loop.endSeq),
            closure: claim.loop.closure == .crossing ? .crossing : .proximity,
            estimatedArea: claim.loop.estimatedArea,
            sentAtMs: nowMs())
        let output = try await call {
            try await api.claimLoop(path: .init(runId: Self.string(claim.runId)), body: .json(body))
        }
        var updated = claim
        switch output {
        case .accepted(let response):
            updated.sent = true
            updated.outcome = Self.outcome(try response.body.json)
            report.sentClaims += 1
        case .ok(let response):
            updated.sent = true
            updated.outcome = Self.outcome(try response.body.json)
            report.sentClaims += 1
        case .badRequest(let response):
            updated.refusedCode = try response.body.application_problem_plus_json.code ?? "claim_invalid"
        case .conflict(let response):
            updated.refusedCode = try response.body.application_problem_plus_json.code ?? "claim_conflict"
        case .notFound:
            return .forgotten
        case .forbidden(let response):
            try stopIfAccountDeleting(response.body.application_problem_plus_json)
            throw Stop(.offline)
        case .tooManyRequests(let response):
            // claim_limit — лимит заявок забега (навсегда) или суток (к завтра заявка всё равно устареет).
            let code = try response.body.application_problem_plus_json.code
            guard code == "claim_limit" else { throw Stop(.rateLimited) }
            updated.refusedCode = code
        case .undocumented(let status, _):
            throw Self.stop(status)
        }
        try await store.save(updated)
        if updated.isSettled {
            report.settledClaims.append(SettledClaim(updated))
        }
        return .next
    }

    /// Итоги отправленных, но ещё не решённых заявок (`GET /runs/{id}/captures`).
    private func refreshClaims(of run: LocalRun, _ report: inout SyncReport) async throws {
        let waiting = try await store.claims(of: run.id).filter { $0.sent && !$0.isSettled }
        guard !waiting.isEmpty else { return }
        let output = try await call { try await api.listCaptures(path: .init(runId: Self.string(run.id))) }
        switch output {
        case .ok(let response):
            let captures = try response.body.json
            for var claim in waiting {
                guard let capture = captures.first(where: { Int($0.claimNo) == claim.claimNo }) else { continue }
                claim.outcome = Self.outcome(capture)
                try await store.save(claim)
                if claim.isSettled {
                    report.settledClaims.append(SettledClaim(claim))
                }
            }
        case .notFound:
            // Незавершённый забег начнёт заново следующий проход. У подтверждённого на телефоне ничего не осталось —
            // итога не будет, и спрашивать о нём бесконечно незачем.
            guard run.confirmedComplete else { return }
            for var claim in waiting {
                claim.outcome = ClaimOutcome(
                    status: "failed", waitingFor: nil, rejectCode: "run_not_found", areaSquareMeters: 0)
                try await store.save(claim)
                report.settledClaims.append(SettledClaim(claim))
            }
        case .undocumented(let status, _):
            throw Self.stop(status)
        }
    }

    // MARK: - Завершение

    /// `POST /runs/{id}/finish`.
    private func finish(_ run: inout LocalRun, _ report: inout SyncReport) async throws -> Step {
        guard let endedAtMs = run.endedAtMs, let lastSeq = run.lastSeq else { return .later }
        // Конец не позже «сейчас» по тем же часам: если их перевели назад, сервер отверг бы конец «из будущего»
        // (а раньше начала он сам зажмёт конец до начала).
        let body = Components.Schemas.FinishRunRequest(
            endedAtMs: min(endedAtMs, nowMs()), lastSeq: Int32(clamping: lastSeq), sentAtMs: nowMs())
        let output = try await call {
            try await api.finishRun(path: .init(runId: Self.string(run.id)), body: .json(body))
        }
        switch output {
        case .ok:
            try await update(&run) { $0.finishSent = true }
            report.finishedRuns += 1
            return .next
        case .badRequest(let response):
            let code = try response.body.application_problem_plus_json.code
            try await update(&run) {
                $0.finishSent = true
                $0.finishRejectCode = code ?? "finish_invalid"
            }
            return .next
        case .conflict(let response):
            let code = try response.body.application_problem_plus_json.code
            try await update(&run) {
                $0.finishSent = true
                $0.finishRejectCode = code ?? "finish_conflict"
            }
            return .next
        case .notFound:
            try await forget(&run, &report)
            return .forgotten
        case .undocumented(let status, _):
            throw Self.stop(status)
        }
    }

    /// После завершения: `GET /runs/{id}`. Пока сервер не считает забег завершённым, его `missing` пуст и ничего не
    /// значит — завершение повторяется. Потом: пусто — куски стираются с телефона; иначе куски с недостающими номерами
    /// снова встают в очередь (409 при повторе дорежет их до недостающих).
    private func confirmComplete(_ run: inout LocalRun, _ report: inout SyncReport) async throws {
        let output = try await call { try await api.getRun(path: .init(runId: Self.string(run.id))) }
        let serverRun: Components.Schemas.RunResponse
        switch output {
        case .ok(let response):
            serverRun = try response.body.json
        case .notFound:
            try await forget(&run, &report)
            return
        case .undocumented(let status, _):
            throw Self.stop(status)
        }

        if let cells = serverRun.fogNewCells, run.fogNewCells == nil {
            try await update(&run) { $0.fogNewCells = Int(cells) }
        }
        let gaveUp = run.resendRounds >= Self.maxResendRounds
        guard serverRun.status == .finished || run.finishRejectCode != nil || gaveUp else {
            try await update(&run) {
                $0.finishSent = false
                $0.resendRounds += 1
            }
            report.unconfirmedFinishes += 1
            return
        }
        let missing = serverRun.missing.map { Int($0.firstSeq)...Int($0.lastSeq) }
        let chunks = try await store.chunks(of: run.id)
        let requeue = chunks.filter { chunk in missing.contains { $0.overlaps(chunk.firstSeq...chunk.lastSeq) } }
        if requeue.isEmpty || gaveUp {
            // Всё дошло — или недостающих точек на телефоне нет (кусок отвергнут, окно закрыто) и дослать нечего.
            // Сначала куски, потом отметка: если приложение выгрузят посередине, проверка просто повторится.
            // Стираются все куски забега, а не прочитанные: нечитаемый иначе остался бы в базе навсегда.
            try await store.deleteChunks(of: run.id)
            try await update(&run) { $0.confirmedComplete = true }
        } else {
            try await update(&run) { $0.resendRounds += 1 }
            for var chunk in requeue {
                chunk.sent = false
                try await store.save(chunk)
            }
            report.requeuedChunks += requeue.count
        }
    }

    // MARK: - Итог забега: перезапросы

    /// Итог забега по запросу экрана — открыт итог или детали (docs/architecture/run-hud.md, «Итог забега»): «+N га»
    /// (`fogNewCells`), пока его нет у завершённого забега, и разбивка `areaByOutcome` применённых заявок, пока её нет
    /// (сервер отдаёт её только после границы публичности). Что уже известно, больше не спрашивается.
    /// - Returns: почему остановились (нет сети и т. п.); `nil` — спрошено всё, что можно было спросить.
    @discardableResult
    public func refreshResults(of runId: UUID) async -> SyncStop? {
        do {
            guard let run = try await ownRuns().first(where: { $0.id == runId }), run.serverState == .started else {
                return nil
            }
            try await refreshFogNewCells(of: run)
            let missing = try await store.claims(of: run.id).filter {
                $0.outcome?.status == "applied" && $0.outcome?.areaByOutcome == nil
            }
            guard !missing.isEmpty else { return nil }
            let output = try await call { try await api.listCaptures(path: .init(runId: Self.string(run.id))) }
            switch output {
            case .ok(let response):
                let captures = try response.body.json
                for var claim in missing {
                    guard let capture = captures.first(where: { Int($0.claimNo) == claim.claimNo }),
                        let byOutcome = capture.areaByOutcome
                    else { continue }
                    claim.outcome?.areaByOutcome = byOutcome.additionalProperties
                    try await store.save(claim)
                }
            case .notFound:
                break
            case .undocumented(let status, _):
                throw Self.stop(status)
            }
            return nil
        } catch let stop as Stop {
            return stop.reason
        } catch {
            return .offline
        }
    }

    /// Подсказка `FogChanged` (номера забега в ней нет): «+N га» своих завершённых забегов, у которых числа ещё нет.
    /// Не пришла (забег ничего нового не открыл) — число спросит `refreshResults` при открытии итога.
    @discardableResult
    public func refreshFog() async -> SyncStop? {
        do {
            for run in try await ownRuns() where run.serverState == .started {
                try await refreshFogNewCells(of: run)
            }
            return nil
        } catch let stop as Stop {
            return stop.reason
        } catch {
            return .offline
        }
    }

    /// `GET /runs/{id}` за «+N га»: только у завершённого забега (завершение отправлено), пока числа нет и точки забега
    /// ещё хранятся (`fogQueryDays`).
    private func refreshFogNewCells(of run: LocalRun) async throws {
        let limitMs = run.startedAtMs + Int64(Self.fogQueryDays * 86_400_000)
        guard run.finishSent, run.fogNewCells == nil, nowMs() <= limitMs else { return }
        let output = try await call { try await api.getRun(path: .init(runId: Self.string(run.id))) }
        switch output {
        case .ok(let response):
            guard let cells = try response.body.json.fogNewCells else { return }  // туман ещё не открыт
            var run = run
            try await update(&run) { $0.fogNewCells = Int(cells) }
        case .notFound:
            return
        case .undocumented(let status, _):
            throw Self.stop(status)
        }
    }

    // MARK: - Общее

    private struct Stop: Error {
        let reason: SyncStop
        init(_ reason: SyncStop) { self.reason = reason }
    }

    /// Сервер ответил 404: он не знает забега (удалён или данные потеряны). Всё, что ещё хранится, отправляется заново
    /// после нового `POST /runs` — повторы сервер распознает сам.
    private func forget(_ run: inout LocalRun, _ report: inout SyncReport) async throws {
        report.forgottenRuns += 1
        try await update(&run) {
            $0.serverState = .unknown
            $0.finishSent = false
            $0.finishRejectCode = nil
        }
        for var chunk in try await store.chunks(of: run.id) where chunk.sent {
            chunk.sent = false
            try await store.save(chunk)
        }
        for var claim in try await store.claims(of: run.id) where claim.sent && !claim.isSettled {
            claim.sent = false
            try await store.save(claim)
        }
    }

    /// Меняет поля синхронизации — и в хранилище (атомарно), и в локальной копии; поля записи забега не трогает.
    private func update(_ run: inout LocalRun, _ change: @Sendable (inout LocalRun) -> Void) async throws {
        change(&run)
        if let stored = try await store.updateRun(run.id, change) {
            run = stored
        }
    }

    private func stopIfAccountDeleting(_ problem: Components.Schemas.ProblemDetails) throws {
        if problem.code == "account_deleting" {
            throw Stop(.accountDeleting)
        }
    }

    private func nowMs() -> Int64 { StoragePrecision.milliseconds(now()) }

    /// Вызов API. Ошибка транспорта (нет сети, тайм-аут) — остановить проход и повторить позже. Ответ, который клиент
    /// не смог разобрать, — по коду: 401 и 429 могут прийти без тела (проверка входа, ограничитель частоты).
    private func call<T>(_ body: () async throws -> T) async throws -> T {
        if let signedInPlayer, await signedInPlayer() != ownerId {
            throw Stop(.unauthorized)  // вошёл другой игрок (или никто): его токеном чужие забеги не шлются
        }
        do {
            return try await body()
        } catch let error as ClientError {
            throw Self.stop(error.response.map { $0.status.code } ?? 0)
        } catch {
            throw Stop(.offline)
        }
    }

    private static func stop(_ status: Int) -> Stop {
        switch status {
        case 401: Stop(.unauthorized)
        case 429: Stop(.rateLimited)
        default: Stop(.offline)
        }
    }

    static func string(_ id: UUID) -> String { id.uuidString.lowercased() }

    static func request(for chunk: SealedChunk, sentAtMs: Int64) -> Components.Schemas.UploadChunkRequest {
        .init(
            sentAtMs: sentAtMs,
            sensorsCompleteThroughMs: chunk.sensorsCompleteThroughMs,
            points: zip(chunk.points, chunk.sources).map { point, source in
                .init(
                    seq: Int32(clamping: point.seq),
                    t: StoragePrecision.milliseconds(point.timestamp),
                    lat: point.coordinate.latitude,
                    lon: point.coordinate.longitude,
                    acc: point.horizontalAccuracy,
                    speed: point.speed,
                    flags: Int32(clamping: source.rawValue))
            },
            motion: chunk.motion.map { sample in
                .init(
                    t: StoragePrecision.milliseconds(sample.timestamp),
                    activity: Components.Schemas.MotionActivity(rawValue: sample.activity.rawValue) ?? .unknown)
            },
            steps: chunk.steps.map { sample in
                .init(
                    start: StoragePrecision.milliseconds(sample.start),
                    end: StoragePrecision.milliseconds(sample.end),
                    steps: sample.steps.map { Int32(clamping: $0) })
            })
    }

    static func outcome(_ capture: Components.Schemas.CaptureResponse) -> ClaimOutcome {
        ClaimOutcome(
            status: capture.status.rawValue, waitingFor: capture.waitingFor, rejectCode: capture.rejectCode,
            areaSquareMeters: capture.areaSquareMeters, areaByOutcome: capture.areaByOutcome?.additionalProperties)
    }

    /// Диапазоны `[{firstSeq, lastSeq}]` из дополнительного поля ошибки (`overlaps`).
    static func ranges(_ container: OpenAPIValueContainer?) -> [ClosedRange<Int>] {
        guard let items = decode([Components.Schemas.SeqRange].self, container) else { return [] }
        return items.filter { $0.firstSeq <= $0.lastSeq }.map { Int($0.firstSeq)...Int($0.lastSeq) }
    }

    /// Что не так с куском: `[{field, rule}]` из дополнительного поля ошибки (`problems`).
    struct ChunkProblem: Decodable, Equatable {
        let field: String
        let rule: String
    }

    static func problems(_ container: OpenAPIValueContainer?) -> [ChunkProblem] {
        decode([ChunkProblem].self, container) ?? []
    }

    private static func decode<T: Decodable>(_ type: T.Type, _ container: OpenAPIValueContainer?) -> T? {
        guard let container, let data = try? JSONEncoder().encode(container) else { return nil }
        return try? JSONDecoder().decode(type, from: data)
    }
}
