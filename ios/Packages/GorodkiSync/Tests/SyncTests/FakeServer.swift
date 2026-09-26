import Foundation
import GameCore
import GorodkiAPI
import OpenAPIRuntime

@testable import Sync

/// Сервер без сети — по правилам настоящего (RunEndpoints, CaptureEndpoints, TrackChunkRules, TrackJudging):
/// повтор старта и куска распознаётся (тот же кусок — 200, другой на тех же номерах — 409), пересечение номеров — 409
/// с `overlaps` (не больше 20), кусок проверяется до записи (400 со списком `problems`), заявка узнаётся по концу петли,
/// `missing` появляется только после завершения, запоздавшие данные датчиков считаются так же, как на сервере.
/// Умеет «ломаться»: нет сети до обработки запроса или ответ потерян после неё.
actor FakeServer: APIProtocol {
    struct Run {
        var request: Components.Schemas.StartRunRequest
        var chunks: [ClosedRange<Int>: Components.Schemas.UploadChunkRequest] = [:]
        var lastSeq: Int?
        var endedAtMs: Int64?
        /// Заявки по концу петли — как `CaptureIds.For(runId, endSeq)` на сервере.
        var captures: [Int: Components.Schemas.CaptureResponse] = [:]
        /// «+N га» забега (`RunResponse.fogNewCells`): `nil` — туман по забегу ещё не открыт.
        var fogNewCells: Int?
        /// Визиты забега (`RunResponse.visitedParcels`): `nil` — ещё не посчитаны.
        var visitedParcels: Int?
    }

    enum Fault: Sendable {
        /// Запрос не дошёл до сервера.
        case offline
        /// Сервер обработал запрос, но ответ потерялся.
        case lostResponse
    }

    struct NetworkDown: Error {}
    struct Unused: Error {}

    static let maxRunHours = 4.0

    private(set) var runs: [String: Run] = [:]
    /// Запросы по порядку: `start`, `chunk 0-9`, `claim 0`, `finish`, `get`, `captures`.
    private(set) var log: [String] = []
    private var faults: [String: Fault] = [:]
    private var startAnswers: [String: (status: Int, code: String)] = [:]
    private var answers: [String: (status: Int, code: String)] = [:]
    private var missingAlways: ClosedRange<Int>?
    private var beforeStart: (@Sendable () async -> Void)?

    // MARK: - Управление из теста

    /// Следующий запрос с таким именем (как в `log`) сломается.
    func fail(_ request: String, with fault: Fault) { faults[request] = fault }

    /// Старт этого забега получит отказ.
    func answerStart(of runId: UUID, status: Int, code: String) {
        startAnswers[SyncEngine.string(runId)] = (status, code)
    }

    /// Снять отказ старта этого забега.
    func clearStartAnswer(of runId: UUID) { startAnswers[SyncEngine.string(runId)] = nil }

    /// Запрос с таким именем (как в `log`) получает отказ, пока его не снимут (`code` пустой — снять).
    func answer(_ request: String, status: Int, code: String) {
        answers[request] = code.isEmpty ? nil : (status, code)
    }

    /// Выполнить это, пока сервер «думает» над стартом (телефон тем временем продолжает запись).
    func whileStarting(_ action: @escaping @Sendable () async -> Void) { beforeStart = action }

    /// Сервер «забыл» забег (как после удаления): дальше на всё — 404.
    func forget(_ runId: UUID) { runs[SyncEngine.string(runId)] = nil }

    /// Сервер «потерял» завершение: забег снова активен.
    func unfinish(_ runId: UUID) {
        runs[SyncEngine.string(runId)]?.lastSeq = nil
        runs[SyncEngine.string(runId)]?.endedAtMs = nil
    }

    /// Сервер потерял кусок с этими границами.
    func dropChunk(of runId: UUID, _ range: ClosedRange<Int>) {
        runs[SyncEngine.string(runId)]?.chunks[range] = nil
    }

    /// У сервера уже есть кусок с этими номерами (без датчиков).
    func preloadChunk(of runId: UUID, _ range: ClosedRange<Int>) {
        runs[SyncEngine.string(runId)]?.chunks[range] = .init(sentAtMs: 0, sensorsCompleteThroughMs: 0, points: [])
    }

    /// `GET` после завершения всегда называет эти номера недостающими (сервер, который «не видит» дошедшие точки).
    func alwaysReportMissing(_ range: ClosedRange<Int>) { missingAlways = range }

    func settleClaim(
        of runId: UUID, endSeq: Int, status: Components.Schemas.CaptureStatus, area: Double = 0,
        rejectCode: String? = nil, byOutcome: [String: Double]? = nil
    ) {
        runs[SyncEngine.string(runId)]?.captures[endSeq]?.status = status
        runs[SyncEngine.string(runId)]?.captures[endSeq]?.waitingFor = nil
        runs[SyncEngine.string(runId)]?.captures[endSeq]?.areaSquareMeters = area
        runs[SyncEngine.string(runId)]?.captures[endSeq]?.rejectCode = rejectCode
        publishBreakdown(of: runId, endSeq: endSeq, byOutcome)
    }

    /// Граница публичности дошла до применения захвата: сервер отдаёт разбивку по видам (`nil` — ещё нет).
    func publishBreakdown(of runId: UUID, endSeq: Int, _ byOutcome: [String: Double]?) {
        runs[SyncEngine.string(runId)]?.captures[endSeq]?.areaByOutcome =
            byOutcome.map { .init(additionalProperties: $0) }
    }

    /// Сервер открыл туман по забегу: «+N га» в `GET /runs/{id}`.
    func openFog(of runId: UUID, newCells: Int) { runs[SyncEngine.string(runId)]?.fogNewCells = newCells }
    func countVisits(of runId: UUID, parcels: Int) { runs[SyncEngine.string(runId)]?.visitedParcels = parcels }

    func run(_ runId: UUID) -> Run? { runs[SyncEngine.string(runId)] }

    /// Все номера точек, которые есть у сервера, — с повторами, если бы номер сохранился дважды.
    func receivedSeqs(of runId: UUID) -> [Int] {
        guard let run = run(runId) else { return [] }
        return run.chunks.keys.flatMap { Array($0) }.sorted()
    }

    /// Запоздавшие записи датчиков — как `TrackJudging.JudgeRun`: куски по порядку номеров, запись не позже отметки
    /// более раннего куска не засчитывается (и это признак недоверия).
    func lateSensorRecords(of runId: UUID) -> Int {
        guard let run = run(runId) else { return 0 }
        var completeThrough = Int64.min
        var late = 0
        for range in run.chunks.keys.sorted(by: { $0.lowerBound < $1.lowerBound }) {
            guard let chunk = run.chunks[range] else { continue }
            late += (chunk.motion ?? []).filter { $0.t <= completeThrough }.count
            late += (chunk.steps ?? []).filter { $0.end <= completeThrough }.count
            completeThrough = max(completeThrough, chunk.sensorsCompleteThroughMs)
        }
        return late
    }

    // MARK: - Забеги

    func startRun(_ input: Operations.startRun.Input) async throws -> Operations.startRun.Output {
        guard case .json(let request) = input.body else { throw Unused() }
        if let action = beforeStart {
            beforeStart = nil
            await action()
        }
        return try gate("start") {
            if let answer = startAnswers[request.id] {
                let problem = Self.problem(answer.code)
                switch answer.status {
                case 400: return .badRequest(.init(body: .application_problem_plus_json(problem)))
                case 403: return .forbidden(.init(body: .application_problem_plus_json(problem)))
                case 409: return .conflict(.init(body: .application_problem_plus_json(problem)))
                case 422: return .unprocessableContent(.init(body: .application_problem_plus_json(problem)))
                case 429: return .tooManyRequests(.init(body: .application_problem_plus_json(problem)))
                default: return .undocumented(statusCode: answer.status, .init())
                }
            }
            if let run = runs[request.id] {
                return .ok(.init(body: .json(Self.response(request.id, run))))
            }
            let run = Run(request: request)
            runs[request.id] = run
            return .created(.init(body: .json(Self.response(request.id, run))))
        }
    }

    func uploadChunk(_ input: Operations.uploadChunk.Input) async throws -> Operations.uploadChunk.Output {
        guard case .json(let request) = input.body else { throw Unused() }
        let first = Int(input.path.firstSeq)
        let count = request.points?.count ?? 0
        let name = "chunk \(first)-\(first + count - 1)"
        return try gate(name) {
            guard var run = runs[input.path.runId] else {
                return .notFound(.init(body: .application_problem_plus_json(Self.problem("run_not_found"))))
            }
            if let answer = answers[name] {
                let problem = Self.problem(answer.code)
                switch answer.status {
                case 400: return .badRequest(.init(body: .application_problem_plus_json(problem)))
                case 409: return .conflict(.init(body: .application_problem_plus_json(problem)))
                case 413: return .contentTooLarge(.init(body: .application_problem_plus_json(problem)))
                case 429: return .tooManyRequests(.init(body: .application_problem_plus_json(problem)))
                default: return .undocumented(statusCode: answer.status, .init())
                }
            }
            let problems = Self.check(request, firstSeq: first, startedAtMs: run.request.startedAtMs)
            if !problems.isEmpty {
                let container = try Self.container(problems.map { ["field": $0.field, "rule": $0.rule] })
                return .badRequest(
                    .init(body: .application_problem_plus_json(Self.problem("chunk_invalid", ["problems": container]))))
            }
            let range = first...(first + count - 1)
            let receipt = Components.Schemas.ChunkReceipt(
                firstSeq: Int32(range.lowerBound), lastSeq: Int32(range.upperBound), duplicate: false)
            if let stored = run.chunks[range], Self.sameContent(stored, request) {
                var duplicate = receipt
                duplicate.duplicate = true
                return .ok(.init(body: .json(duplicate)))
            }
            let overlaps = run.chunks.keys.filter { $0.overlaps(range) }.sorted { $0.lowerBound < $1.lowerBound }
            if !overlaps.isEmpty {
                let container = try Self.container(
                    overlaps.prefix(20).map { ["firstSeq": $0.lowerBound, "lastSeq": $0.upperBound] })
                return .conflict(
                    .init(body: .application_problem_plus_json(Self.problem("chunk_conflict", ["overlaps": container])))
                )
            }
            run.chunks[range] = request
            runs[input.path.runId] = run
            return .created(.init(body: .json(receipt)))
        }
    }

    func finishRun(_ input: Operations.finishRun.Input) async throws -> Operations.finishRun.Output {
        guard case .json(let request) = input.body else { throw Unused() }
        return try gate("finish") {
            guard var run = runs[input.path.runId] else {
                return .notFound(.init(body: .application_problem_plus_json(Self.problem("run_not_found"))))
            }
            if run.lastSeq != nil {
                return .ok(.init(body: .json(Self.response(input.path.runId, run))))  // повтор ничего не меняет
            }
            if let answer = answers["finish"] {
                let problem = Self.problem(answer.code)
                return answer.status == 409
                    ? .conflict(.init(body: .application_problem_plus_json(problem)))
                    : .badRequest(.init(body: .application_problem_plus_json(problem)))
            }
            if request.lastSeq < -1 {
                return .badRequest(.init(body: .application_problem_plus_json(Self.problem("finish_invalid"))))
            }
            if request.endedAtMs > request.sentAtMs + 120_000 {
                return .badRequest(.init(body: .application_problem_plus_json(Self.problem("ended_in_future"))))
            }
            if Int(request.lastSeq) < run.chunks.keys.map(\.upperBound).max() ?? -1 {
                return .conflict(.init(body: .application_problem_plus_json(Self.problem("last_seq_too_small"))))
            }
            run.lastSeq = Int(request.lastSeq)
            run.endedAtMs = max(request.endedAtMs, run.request.startedAtMs)
            runs[input.path.runId] = run
            return .ok(.init(body: .json(Self.response(input.path.runId, run))))
        }
    }

    func getRun(_ input: Operations.getRun.Input) async throws -> Operations.getRun.Output {
        try gate("get") {
            guard let run = runs[input.path.runId] else {
                return .notFound(.init(body: .application_problem_plus_json(Self.problem("run_not_found"))))
            }
            var response = Self.response(input.path.runId, run)
            if let missingAlways, run.lastSeq != nil {
                response.missing.append(
                    .init(firstSeq: Int32(missingAlways.lowerBound), lastSeq: Int32(missingAlways.upperBound)))
            }
            return .ok(.init(body: .json(response)))
        }
    }

    // MARK: - Заявки

    func claimLoop(_ input: Operations.claimLoop.Input) async throws -> Operations.claimLoop.Output {
        guard case .json(let request) = input.body else { throw Unused() }
        return try gate("claim \(request.claimNo)") {
            guard var run = runs[input.path.runId] else {
                return .notFound(.init(body: .application_problem_plus_json(Self.problem("run_not_found"))))
            }
            if let answer = answers["claim \(request.claimNo)"] {
                let problem = Self.problem(answer.code)
                switch answer.status {
                case 400: return .badRequest(.init(body: .application_problem_plus_json(problem)))
                case 403: return .forbidden(.init(body: .application_problem_plus_json(problem)))
                case 409: return .conflict(.init(body: .application_problem_plus_json(problem)))
                case 429: return .tooManyRequests(.init(body: .application_problem_plus_json(problem)))
                default: return .undocumented(statusCode: answer.status, .init())
                }
            }
            if request.claimNo < 0 || request.startSeq < 0 || request.endSeq - request.startSeq < 3
                || Int(request.endSeq) > run.lastSeq ?? Int.max
            {
                return .badRequest(.init(body: .application_problem_plus_json(Self.problem("claim_invalid"))))
            }
            let endSeq = Int(request.endSeq)
            if let existing = run.captures[endSeq] {
                guard existing.claimNo == request.claimNo, existing.startSeq == request.startSeq else {
                    return .conflict(.init(body: .application_problem_plus_json(Self.problem("claim_conflict"))))
                }
                return .ok(.init(body: .json(existing)))
            }
            let capture = Components.Schemas.CaptureResponse(
                id: UUID().uuidString.lowercased(), claimNo: request.claimNo, startSeq: request.startSeq,
                endSeq: request.endSeq, status: .pending, waitingFor: "queue", areaSquareMeters: 0)
            run.captures[endSeq] = capture
            runs[input.path.runId] = run
            return .accepted(.init(body: .json(capture)))
        }
    }

    func listCaptures(_ input: Operations.listCaptures.Input) async throws -> Operations.listCaptures.Output {
        try gate("captures") {
            guard let run = runs[input.path.runId] else {
                return .notFound(.init(body: .application_problem_plus_json(Self.problem("run_not_found"))))
            }
            return .ok(.init(body: .json(run.captures.values.sorted { $0.claimNo < $1.claimNo })))
        }
    }

    // MARK: - Не нужны синхронизации

    func getHealth(_ input: Operations.getHealth.Input) async throws -> Operations.getHealth.Output { throw Unused() }
    func getMe(_ input: Operations.getMe.Input) async throws -> Operations.getMe.Output { throw Unused() }
    func getMyStats(_ input: Operations.getMyStats.Input) async throws -> Operations.getMyStats.Output {
        throw Unused()
    }
    func getPlayer(_ input: Operations.getPlayer.Input) async throws -> Operations.getPlayer.Output { throw Unused() }
    func setPublicProfile(_ input: Operations.setPublicProfile.Input) async throws
        -> Operations.setPublicProfile.Output
    { throw Unused() }
    func deleteMe(_ input: Operations.deleteMe.Input) async throws -> Operations.deleteMe.Output { throw Unused() }
    func exportMyData(_ input: Operations.exportMyData.Input) async throws -> Operations.exportMyData.Output {
        throw Unused()
    }
    func listPrivacyZones(_ input: Operations.listPrivacyZones.Input) async throws
        -> Operations.listPrivacyZones.Output
    { throw Unused() }
    func createPrivacyZone(_ input: Operations.createPrivacyZone.Input) async throws
        -> Operations.createPrivacyZone.Output
    { throw Unused() }
    func deletePrivacyZone(_ input: Operations.deletePrivacyZone.Input) async throws
        -> Operations.deletePrivacyZone.Output
    { throw Unused() }
    func getExplorationLeaderboard(_ input: Operations.getExplorationLeaderboard.Input) async throws
        -> Operations.getExplorationLeaderboard.Output
    { throw Unused() }
    func getConfig(_ input: Operations.getConfig.Input) async throws -> Operations.getConfig.Output { throw Unused() }
    func getTerritory(_ input: Operations.getTerritory.Input) async throws -> Operations.getTerritory.Output {
        throw Unused()
    }
    func signInWithGoogle(_ input: Operations.signInWithGoogle.Input) async throws
        -> Operations.signInWithGoogle.Output
    { throw Unused() }
    func refreshSession(_ input: Operations.refreshSession.Input) async throws -> Operations.refreshSession.Output {
        throw Unused()
    }
    func logout(_ input: Operations.logout.Input) async throws -> Operations.logout.Output { throw Unused() }
    func getFog(_ input: Operations.getFog.Input) async throws -> Operations.getFog.Output { throw Unused() }
    func getFogSummary(_ input: Operations.getFogSummary.Input) async throws -> Operations.getFogSummary.Output {
        throw Unused()
    }
    func clearFog(_ input: Operations.clearFog.Input) async throws -> Operations.clearFog.Output { throw Unused() }
    func getSeasons(_ input: Operations.getSeasons.Input) async throws -> Operations.getSeasons.Output {
        throw Unused()
    }

    // MARK: - Заготовки задач Егора (C15): синхронизации не нужны

    func getTerritoryLeaderboard(_ input: Operations.getTerritoryLeaderboard.Input) async throws
        -> Operations.getTerritoryLeaderboard.Output
    { throw Unused() }
    func getWeeklyCard(_ input: Operations.getWeeklyCard.Input) async throws -> Operations.getWeeklyCard.Output {
        throw Unused()
    }
    func getStreak(_ input: Operations.getStreak.Input) async throws -> Operations.getStreak.Output { throw Unused() }
    func getHallOfFame(_ input: Operations.getHallOfFame.Input) async throws -> Operations.getHallOfFame.Output {
        throw Unused()
    }
    func getCollection(_ input: Operations.getCollection.Input) async throws -> Operations.getCollection.Output {
        throw Unused()
    }
    func createClan(_ input: Operations.createClan.Input) async throws -> Operations.createClan.Output {
        throw Unused()
    }
    func getMyClan(_ input: Operations.getMyClan.Input) async throws -> Operations.getMyClan.Output { throw Unused() }
    func getClanHues(_ input: Operations.getClanHues.Input) async throws -> Operations.getClanHues.Output {
        throw Unused()
    }
    func getClan(_ input: Operations.getClan.Input) async throws -> Operations.getClan.Output { throw Unused() }
    func joinClan(_ input: Operations.joinClan.Input) async throws -> Operations.joinClan.Output { throw Unused() }
    func leaveClan(_ input: Operations.leaveClan.Input) async throws -> Operations.leaveClan.Output { throw Unused() }
    func removeClanMember(_ input: Operations.removeClanMember.Input) async throws -> Operations.removeClanMember.Output
    { throw Unused() }
    func setClanMemberRole(_ input: Operations.setClanMemberRole.Input) async throws
        -> Operations.setClanMemberRole.Output
    { throw Unused() }
    func renameClan(_ input: Operations.renameClan.Input) async throws -> Operations.renameClan.Output {
        throw Unused()
    }
    func newClanCode(_ input: Operations.newClanCode.Input) async throws -> Operations.newClanCode.Output {
        throw Unused()
    }
    func getInbox(_ input: Operations.getInbox.Input) async throws -> Operations.getInbox.Output { throw Unused() }
    func markInboxRead(_ input: Operations.markInboxRead.Input) async throws -> Operations.markInboxRead.Output {
        throw Unused()
    }
    func getInventory(_ input: Operations.getInventory.Input) async throws -> Operations.getInventory.Output {
        throw Unused()
    }
    func activateItem(_ input: Operations.activateItem.Input) async throws -> Operations.activateItem.Output {
        throw Unused()
    }
    func listFriends(_ input: Operations.listFriends.Input) async throws -> Operations.listFriends.Output {
        throw Unused()
    }
    func addFriend(_ input: Operations.addFriend.Input) async throws -> Operations.addFriend.Output { throw Unused() }
    func acceptFriend(_ input: Operations.acceptFriend.Input) async throws -> Operations.acceptFriend.Output {
        throw Unused()
    }
    func removeFriend(_ input: Operations.removeFriend.Input) async throws -> Operations.removeFriend.Output {
        throw Unused()
    }
    func getFeed(_ input: Operations.getFeed.Input) async throws -> Operations.getFeed.Output { throw Unused() }
    func respectPost(_ input: Operations.respectPost.Input) async throws -> Operations.respectPost.Output {
        throw Unused()
    }
    func reportPost(_ input: Operations.reportPost.Input) async throws -> Operations.reportPost.Output {
        throw Unused()
    }
    func listBlocks(_ input: Operations.listBlocks.Input) async throws -> Operations.listBlocks.Output {
        throw Unused()
    }
    func blockPlayer(_ input: Operations.blockPlayer.Input) async throws -> Operations.blockPlayer.Output {
        throw Unused()
    }
    func unblockPlayer(_ input: Operations.unblockPlayer.Input) async throws -> Operations.unblockPlayer.Output {
        throw Unused()
    }
    func listSegments(_ input: Operations.listSegments.Input) async throws -> Operations.listSegments.Output {
        throw Unused()
    }
    func getSegment(_ input: Operations.getSegment.Input) async throws -> Operations.getSegment.Output {
        throw Unused()
    }
    func getSegmentLeaderboard(_ input: Operations.getSegmentLeaderboard.Input) async throws
        -> Operations.getSegmentLeaderboard.Output
    { throw Unused() }
    func listDuels(_ input: Operations.listDuels.Input) async throws -> Operations.listDuels.Output { throw Unused() }
    func createDuel(_ input: Operations.createDuel.Input) async throws -> Operations.createDuel.Output {
        throw Unused()
    }
    func getDuel(_ input: Operations.getDuel.Input) async throws -> Operations.getDuel.Output { throw Unused() }
    func acceptDuel(_ input: Operations.acceptDuel.Input) async throws -> Operations.acceptDuel.Output {
        throw Unused()
    }
    func declineDuel(_ input: Operations.declineDuel.Input) async throws -> Operations.declineDuel.Output {
        throw Unused()
    }

    // MARK: - Общее

    private func gate<T>(_ name: String, _ handle: () throws -> T) throws -> T {
        log.append(name)
        switch faults.removeValue(forKey: name) {
        case .offline:
            throw NetworkDown()
        case .lostResponse:
            _ = try handle()
            throw NetworkDown()
        case nil:
            return try handle()
        }
    }

    /// Проверка куска — как `TrackChunkRules.Check`.
    static func check(
        _ chunk: Components.Schemas.UploadChunkRequest, firstSeq: Int, startedAtMs: Int64
    ) -> [SyncEngine.ChunkProblem] {
        let points = chunk.points ?? []
        let motion = chunk.motion ?? []
        let steps = chunk.steps ?? []
        let maxSeq = Int(maxRunHours * 3_600 * 2)
        if firstSeq < 0 || firstSeq > maxSeq { return [.init(field: "firstSeq", rule: "seq_limit")] }
        if points.isEmpty || points.count > 1_200 { return [.init(field: "points", rule: "count")] }
        var problems: [SyncEngine.ChunkProblem] = []
        if firstSeq + points.count - 1 > maxSeq { problems.append(.init(field: "points", rule: "seq_limit")) }
        if motion.count > 1_200 { problems.append(.init(field: "motion", rule: "count")) }
        if steps.count > 1_200 { problems.append(.init(field: "steps", rule: "count")) }
        let windowStart = startedAtMs - 60_000
        let runEnd = startedAtMs + Int64(maxRunHours * 3_600_000) + 600_000
        let future = chunk.sentAtMs + 120_000
        func checkTime(_ field: String, _ ms: Int64) {
            if ms < windowStart || ms > runEnd {
                problems.append(.init(field: field, rule: "time_window"))
            } else if ms > future {
                problems.append(.init(field: field, rule: "time_future"))
            }
        }
        for (index, point) in points.enumerated() {
            if Int(point.seq) != firstSeq + index {
                problems.append(.init(field: "points[\(index)]", rule: "seq_order"))
            }
            if index > 0, point.t <= points[index - 1].t {
                problems.append(.init(field: "points[\(index)]", rule: "time_order"))
            }
            checkTime("points[\(index)]", point.t)
        }
        for (index, sample) in motion.enumerated() { checkTime("motion[\(index)]", sample.t) }
        for (index, sample) in steps.enumerated() {
            if sample.end < sample.start || (sample.steps ?? 0) < 0 {
                problems.append(.init(field: "steps[\(index)]", rule: "steps_invalid"))
            }
            checkTime("steps[\(index)]", sample.start)
            checkTime("steps[\(index)]", sample.end)
        }
        if chunk.sensorsCompleteThroughMs < windowStart || chunk.sensorsCompleteThroughMs > future {
            problems.append(.init(field: "sensorsCompleteThroughMs", rule: "time_window"))
        }
        return problems
    }

    /// Тот же кусок, что уже сохранён (время отправки не в счёт) — как сравнение хеша на сервере.
    private static func sameContent(
        _ a: Components.Schemas.UploadChunkRequest, _ b: Components.Schemas.UploadChunkRequest
    ) -> Bool {
        a.points == b.points && a.motion == b.motion && a.steps == b.steps
            && a.sensorsCompleteThroughMs == b.sensorsCompleteThroughMs
    }

    private static func container(_ value: Any) throws -> OpenAPIValueContainer {
        try JSONDecoder().decode(OpenAPIValueContainer.self, from: JSONSerialization.data(withJSONObject: value))
    }

    static func problem(
        _ code: String, _ extra: [String: OpenAPIValueContainer] = [:]
    ) -> Components.Schemas.ProblemDetails {
        .init(code: code, additionalProperties: extra)
    }

    static func response(_ id: String, _ run: Run) -> Components.Schemas.RunResponse {
        let have = Set(run.chunks.keys.flatMap { Array($0) })
        // Как на сервере: `missing` считается только от известной последней точки, то есть после завершения.
        let missing = run.lastSeq.map { last in last < 0 ? [] : ranges((0...last).filter { !have.contains($0) }) } ?? []
        var response = Components.Schemas.RunResponse(
            id: id, league: run.request.league, source: run.request.source, configVersion: run.request.configVersion,
            startedAtMs: run.request.startedAtMs, endedAtMs: run.endedAtMs,
            status: run.lastSeq == nil ? .active : .finished,
            lastSeq: run.lastSeq.map { Int32($0) }, processedSeq: -1, received: ranges(have.sorted()), missing: missing,
            newcomer: false)
        response.fogNewCells = run.fogNewCells.map { Int32($0) }
        response.visitedParcels = run.visitedParcels.map { Int32($0) }
        return response
    }

    private static func ranges(_ seqs: [Int]) -> [Components.Schemas.SeqRange] {
        var result: [Components.Schemas.SeqRange] = []
        for seq in seqs {
            if let last = result.last, Int(last.lastSeq) + 1 == seq {
                result[result.count - 1].lastSeq = Int32(seq)
            } else {
                result.append(.init(firstSeq: Int32(seq), lastSeq: Int32(seq)))
            }
        }
        return result
    }
}
