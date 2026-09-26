import Foundation
import GameCore
import GorodkiAPI
import Synchronization
import Testing

@testable import Sync

@Suite("Итог забега: решённые заявки, разбивка, «+N га» сервера, сборка итога (docs/architecture/run-hud.md)")
struct RunResultTests {
    private static let now = Fixture.start + 7_200
    private let store = InMemorySyncStore()
    private let server = FakeServer()

    private func engine(now: Double = Self.now) -> SyncEngine {
        SyncEngine(store: store, api: server, ownerId: Fixture.owner, now: { now })
    }

    private func stored(_ run: LocalRun) async throws -> LocalRun {
        try #require(await store.runs().first { $0.id == run.id })
    }

    private func result(_ run: LocalRun, now: Double = Self.now) async throws -> RunResult {
        RunResult(run: try await stored(run), claims: await store.claims(of: run.id), now: now)
    }

    // MARK: - Разбивка `areaByOutcome`

    @Test("ClaimOutcome: запись версии 1 без разбивки читается; разбивка — словарь, незнакомый ключ не роняет разбор")
    func outcomeDecoding() throws {
        let v1 = #"{"status":"pending","waitingFor":"points","areaSquareMeters":0}"#
        let withBreakdown = """
            {"status":"applied","areaSquareMeters":900,\
            "areaByOutcome":{"claimedNeutral":600,"transferred":300,"fromTheFuture":1.5}}
            """
        let old = try JSONDecoder().decode(ClaimOutcome.self, from: Data(v1.utf8))
        let fresh = try JSONDecoder().decode(ClaimOutcome.self, from: Data(withBreakdown.utf8))

        #expect(old.areaByOutcome == nil && old.waitingFor == "points")
        #expect(fresh.areaByOutcome == ["claimedNeutral": 600, "transferred": 300, "fromTheFuture": 1.5])
    }

    @Test("Разбивка из ответа сервера сохраняется целиком — с незнакомым ключом новой версии")
    func breakdownFromServer() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store, claims: [Fixture.loop(2, 14)])
        _ = await engine().syncOnce()
        let breakdown = ["claimedNeutral": 2_000.0, "refreshed": 500, "someNewOutcome": 7]
        await server.settleClaim(of: run.id, endSeq: 14, status: .applied, area: 2_000, byOutcome: breakdown)

        _ = await engine().syncOnce()

        let outcome = try #require(await store.claims(of: run.id).first?.outcome)
        #expect(outcome.status == "applied" && outcome.areaSquareMeters == 2_000)
        #expect(outcome.areaByOutcome == breakdown)
    }

    // MARK: - Заявки, решённые в проходе

    @Test("Отчёт прохода называет решённую заявку ровно один раз; повторный проход — ни разу")
    func settledOnce() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store, claims: [Fixture.loop(2, 14)])
        #expect(await engine().syncOnce().settledClaims.isEmpty)  // заявка у сервера, итог ждёт

        await server.settleClaim(of: run.id, endSeq: 14, status: .applied, area: 3_000)
        let settled = await engine().syncOnce().settledClaims

        #expect(settled.map(\.claimNo) == [0] && settled.first?.outcome?.areaSquareMeters == 3_000)
        #expect(settled.first?.runId == run.id && settled.first?.refusedCode == nil)
        #expect(await engine().syncOnce().settledClaims.isEmpty)
    }

    @Test("Отказ в самой заявке (claim_limit при отправке) — в отчёте один раз, итог «готов»")
    func refusedClaimIsSettled() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store, claims: [Fixture.loop(2, 14)])
        await server.answer("claim 0", status: 429, code: "claim_limit")

        let settled = await engine().syncOnce().settledClaims

        #expect(settled.map(\.refusedCode) == ["claim_limit"] && settled.first?.outcome == nil)
        #expect(await engine().syncOnce().settledClaims.isEmpty)
        await server.openFog(of: run.id, newCells: 0)
        await server.countVisits(of: run.id, parcels: 0)
        #expect(await engine().refreshFog() == nil)
        let summary = try await result(run)
        #expect(summary.readiness == .ready)
        #expect(summary.claims.first?.refusedCode == "claim_limit" && summary.takenSquareMeters == 0)
    }

    @Test("Сервер забыл закрытый забег: путь 404 → failed — заявка решена и названа в отчёте")
    func forgottenRunSettlesClaims() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store, claims: [Fixture.loop(2, 14)])
        _ = await engine().syncOnce()
        await server.forget(run.id)

        let settled = await engine().syncOnce().settledClaims

        #expect(settled.map(\.outcome?.rejectCode) == ["run_not_found"])
        #expect(await engine().syncOnce().settledClaims.isEmpty)
    }

    @Test("Забег отвергнут в этом проходе: его заявки решены (их не отправят), итог «готов» с кодом отказа")
    func rejectedRunSettlesClaims() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store, claims: [Fixture.loop(2, 14), Fixture.loop(15, 22)])
        await server.answerStart(of: run.id, status: 403, code: "replay_forbidden")

        let settled = await engine().syncOnce().settledClaims

        #expect(settled.map(\.claimNo) == [0, 1])
        #expect(settled.allSatisfy { $0.refusedCode == PendingClaim.runRejectedCode })
        #expect(await store.claims(of: run.id).allSatisfy(\.isSettled))
        #expect(try await SyncBacklog.of(store, ownerId: Fixture.owner) == SyncBacklog())
        #expect(await engine().syncOnce().settledClaims.isEmpty)
        let summary = try await result(run)
        #expect(summary.readiness == .ready && summary.rejectCode == "replay_forbidden")
    }

    @Test("Расписание отдаёт отчёт каждого прохода подписчику")
    func schedulerReportsPasses() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store, claims: [Fixture.loop(2, 14)])
        await server.answer("claim 0", status: 409, code: "upload_window_closed")
        let reports = Mutex<[SyncReport]>([])
        let store = self.store
        let scheduler = SyncScheduler(
            engine: engine(), backlog: { (try? await SyncBacklog.of(store, ownerId: Fixture.owner)) ?? SyncBacklog() },
            appActive: { false }, sleep: { _ in throw CancellationError() },
            onReport: { report in reports.withLock { $0.append(report) } })

        await scheduler.trigger(.recorded)

        let received = reports.withLock { $0 }
        #expect(received.count == 1)
        #expect(received.first?.settledClaims.map(\.refusedCode) == ["upload_window_closed"])
    }

    // MARK: - «+N га» сервера и перезапрос разбивки

    @Test(
        "fogNewCells и визиты: null у сервера остаётся null; после FogChanged — число; после обоих чисел запросов нет")
    func fogNewCells() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store)
        _ = await engine().syncOnce()
        #expect(try await stored(run).fogNewCells == nil)

        await engine().refreshFog()
        #expect(try await stored(run).fogNewCells == nil)  // туман по забегу ещё не открыт

        await server.openFog(of: run.id, newCells: 120)
        await engine().refreshFog()
        #expect(try await stored(run).fogNewCells == 120)
        #expect(try await stored(run).visitedParcels == nil)  // визиты ещё не посчитаны — «позже»
        #expect(try await result(run).readiness == .computing)

        await server.countVisits(of: run.id, parcels: 3)
        await engine().refreshResults(of: run.id)
        let counted = try await stored(run)
        #expect(counted.visitedParcels == 3 && counted.fogNewCells == 120)
        #expect(try await result(run).visitedParcels == 3)
        #expect(try await result(run).readiness == .ready)

        let before = await server.log.count
        await engine().refreshFog()
        await engine().refreshResults(of: run.id)
        #expect(await server.log.count == before)
    }

    @Test("fogNewCells не спрашивается у незавершённого забега и через 14 дней после начала")
    func fogNewCellsOnlyWhenItCanCome() async throws {
        let open = Fixture.run()
        try await Fixture.record(open, points: 25, into: store, finish: false)
        _ = await engine().syncOnce()
        await server.openFog(of: open.id, newCells: 3)
        var before = await server.log.count
        await engine().refreshFog()
        #expect(await server.log.count == before)  // забег не завершён — туман по нему не откроется

        let old = Fixture.run(startedAt: Fixture.start + 60)
        try await Fixture.record(old, points: 5, into: store)
        _ = await engine().syncOnce()
        await server.openFog(of: old.id, newCells: 7)
        before = await server.log.count
        await engine(now: Fixture.start + 15 * 86_400).refreshFog()
        #expect(await server.log.count == before)
        #expect(try await stored(old).fogNewCells == nil)
        #expect(try await result(old, now: Fixture.start + 15 * 86_400).readiness == .ready)
    }

    @Test(
        "Разбивка: у применённой заявки её нет — итог «готов», строка «позже»; при открытии итога — запрос; пришла — есть"
    )
    func breakdownIsRequestedOnOpen() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store, claims: [Fixture.loop(2, 14)])
        _ = await engine().syncOnce()
        await server.settleClaim(of: run.id, endSeq: 14, status: .applied, area: 4_000)
        await server.openFog(of: run.id, newCells: 10)
        await server.countVisits(of: run.id, parcels: 1)
        _ = await engine().syncOnce()
        await engine().refreshFog()

        var summary = try await result(run)
        #expect(summary.readiness == .ready && summary.takenSquareMeters == 4_000)
        #expect(summary.areaByOutcome == nil && summary.areaByOutcomePending)

        await server.publishBreakdown(of: run.id, endSeq: 14, ["claimedNeutral": 3_000, "transferred": 1_000])
        let before = await server.log.filter { $0 == "captures" }.count
        #expect(await engine().refreshResults(of: run.id) == nil)
        #expect(await server.log.filter { $0 == "captures" }.count == before + 1)

        summary = try await result(run)
        #expect(summary.areaByOutcome == ["claimedNeutral": 3_000, "transferred": 1_000])
        #expect(!summary.areaByOutcomePending)
        let after = await server.log.count
        await engine().refreshResults(of: run.id)
        #expect(await server.log.count == after)  // спрашивать больше нечего
    }

    // MARK: - Сборка итога

    private static func finishedRun(_ change: (inout LocalRun) -> Void = { _ in }) -> LocalRun {
        var run = Fixture.run()
        run.endedAtMs = run.startedAtMs + 1_800_000
        run.lastSeq = 1_799
        run.serverState = .started
        run.finishSent = true
        run.confirmedComplete = true
        var summary = RunSummary()
        summary.distanceMeters = 5_200
        summary.breaks = ["vehicle": 1]
        summary.fogNewSquareMeters = 8_000
        summary.fogNewIsLowerBound = true
        summary.latitude = 52.1
        run.summary = summary
        change(&run)
        return run
    }

    private static func claim(_ no: Int, _ change: (inout PendingClaim) -> Void) -> PendingClaim {
        var claim = PendingClaim(runId: UUID(), claimNo: no, loop: Fixture.loop(no * 10, no * 10 + 9))
        claim.sent = true
        change(&claim)
        return claim
    }

    @Test("Итог офлайн: только оценки телефона — «ждёт сети»")
    func offlineResult() {
        let run = Self.finishedRun {
            $0.serverState = .unknown
            $0.finishSent = false
            $0.confirmedComplete = false
        }
        let claims = [Self.claim(0) { $0.sent = false }]

        let result = RunResult(run: run, claims: claims, now: Self.now)

        #expect(result.readiness == .waitingForNetwork)
        #expect(result.durationSeconds == 1_800 && result.distanceMeters == 5_200)
        #expect(result.estimatedLoopSquareMeters == 5_000 && result.takenSquareMeters == 0)
        #expect(result.fogEstimateSquareMeters == 8_000 && result.fogEstimateIsLowerBound)
        #expect(result.fogNewCells == nil && result.breaks == ["vehicle": 1])
    }

    @Test(
        "Смешанные итоги заявок и туман ещё не открыт — «считается»; туман пришёл — «готов», площадь на широте забега")
    func mixedResult() {
        let claims = [
            Self.claim(0) {
                $0.outcome = ClaimOutcome(
                    status: "applied", areaSquareMeters: 3_000, areaByOutcome: ["claimedNeutral": 3_000])
            },
            Self.claim(1) { $0.outcome = ClaimOutcome(status: "rejected", rejectCode: "too_small") },
            Self.claim(2) { $0.refusedCode = "claim_limit" },
            Self.claim(3) {
                $0.outcome = ClaimOutcome(
                    status: "applied", areaSquareMeters: 1_000,
                    areaByOutcome: ["claimedNeutral": 500, "refreshed": 500])
            },
        ]

        let waiting = RunResult(run: Self.finishedRun(), claims: claims, now: Self.now)
        #expect(waiting.readiness == .computing)
        #expect(waiting.takenSquareMeters == 4_000)
        #expect(waiting.areaByOutcome == ["claimedNeutral": 3_500, "refreshed": 500] && !waiting.areaByOutcomePending)

        let pending = claims + [Self.claim(4) { $0.outcome = ClaimOutcome(status: "pending", waitingFor: "sensors") }]
        let opened = Self.finishedRun {
            $0.fogNewCells = 100
            $0.visitedParcels = 2
        }
        #expect(RunResult(run: opened, claims: pending, now: Self.now).readiness == .computing)

        let ready = RunResult(run: opened, claims: claims, now: Self.now)
        #expect(ready.readiness == .ready)
        let cell = FogGrid.cellSizeMeters(atLatitude: 52.1)
        #expect(abs((ready.fogNewSquareMeters ?? 0) - 100 * cell * cell) < 1e-9)
    }

    @Test("Визитов ещё нет — «считается», хотя заявки решены и туман открыт; через 14 дней ждать нечего")
    func visitsPending() {
        let opened = Self.finishedRun { $0.fogNewCells = 100 }
        let waiting = RunResult(run: opened, claims: [], now: Self.now)
        #expect(waiting.readiness == .computing && waiting.visitedParcels == nil)

        let counted = Self.finishedRun {
            $0.fogNewCells = 100
            $0.visitedParcels = 0
        }
        #expect(RunResult(run: counted, claims: [], now: Self.now).readiness == .ready)
        let late = Fixture.start + (SyncEngine.fogQueryDays + 1) * 86_400
        #expect(RunResult(run: opened, claims: [], now: late).readiness == .ready)
    }

    @Test("Повтор и конец по пределу длины — видны в итоге")
    func replayAndLimit() {
        let run = Self.finishedRun {
            $0.source = .replay
            $0.summary?.endedAtLimit = true
        }
        let result = RunResult(run: run, claims: [], now: Self.now)
        #expect(result.isReplay && result.endedAtLimit)
    }
}
