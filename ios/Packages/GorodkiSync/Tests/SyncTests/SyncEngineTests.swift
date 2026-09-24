import Foundation
import GameCore
import GorodkiAPI
import Testing

@testable import Sync

@Suite("Синхронизация: доставка очереди по контракту сервера")
struct SyncEngineTests {
    private static let now = Fixture.start + 7_200
    private let store = InMemorySyncStore()
    private let server = FakeServer()

    private func engine(now: Double = Self.now) -> SyncEngine {
        SyncEngine(store: store, api: server, ownerId: Fixture.owner, now: { now })
    }

    private func stored(_ run: LocalRun) async throws -> LocalRun {
        try #require(await store.runs().first { $0.id == run.id })
    }

    // MARK: - Обычный путь

    @Test("Обычный забег: старт, куски, петля сразу за своими точками, завершение, проверка — куски стёрты")
    func happyPath() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store, claims: [Fixture.loop(2, 14)])

        let report = await engine().syncOnce()

        #expect(report.stop == nil)
        #expect(
            report.startedRuns == 1 && report.uploadedChunks == 3 && report.sentClaims == 1 && report.finishedRuns == 1)
        #expect(
            await server.log == [
                "start", "chunk 0-9", "chunk 10-14", "claim 0", "chunk 15-24", "finish", "get", "captures",
            ])
        #expect(await server.receivedSeqs(of: run.id) == Array(0...24))
        #expect(await server.lateSensorRecords(of: run.id) == 0)
        let local = try await stored(run)
        #expect(local.serverState == .started && local.finishSent && local.confirmedComplete)
        #expect(await store.chunks(of: run.id).isEmpty)

        let serverRun = try #require(await server.run(run.id))
        #expect(serverRun.request.id == run.id.uuidString.lowercased())
        #expect(serverRun.request.startedAtMs == run.startedAtMs)
        #expect(serverRun.request.sentAtMs == StoragePrecision.milliseconds(Self.now))
        #expect(serverRun.lastSeq == 24)
        let first = try #require(serverRun.chunks[0...9])
        #expect(first.motion?.map(\.t) == [run.startedAtMs + 500])
        #expect(first.sensorsCompleteThroughMs == run.startedAtMs - 60_000)
    }

    @Test("Итог заявки обновляется, пока она не решена; потом запросов нет")
    func claimOutcome() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store, claims: [Fixture.loop(2, 14)])
        _ = await engine().syncOnce()
        #expect(await store.claims(of: run.id).first?.outcome?.status == "pending")

        await server.settleClaim(of: run.id, endSeq: 14, status: .applied)
        _ = await engine().syncOnce()
        let claim = try #require(await store.claims(of: run.id).first)
        #expect(claim.outcome?.status == "applied" && claim.isSettled)

        let before = await server.log.count
        let idle = await engine().syncOnce()
        #expect(await server.log.count == before)
        #expect(idle == SyncReport())
    }

    @Test("Забег без точек: старт и завершение с последней точкой −1")
    func emptyRun() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 0, into: store)
        _ = await engine().syncOnce()
        #expect(await server.log == ["start", "finish", "get"])
        #expect(await server.run(run.id)?.lastSeq == -1)
        #expect(try await stored(run).confirmedComplete)
    }

    @Test("Забеги другого игрока (до смены аккаунта) не отправляются")
    func ownRunsOnly() async throws {
        let other = Fixture.run(owner: "player-2")
        try await Fixture.record(other, points: 5, into: store)
        let own = Fixture.run(startedAt: Fixture.start + 3_600)
        try await Fixture.record(own, points: 5, into: store)

        _ = await engine().syncOnce()

        #expect(await server.run(other.id) == nil)
        #expect(try await stored(other).serverState == .unknown)
        #expect(try await stored(own).confirmedComplete)
    }

    // MARK: - Сеть и повторы

    @Test("Нет сети: ничего не потеряно, следующий проход доделывает")
    func offline() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store)
        await server.fail("start", with: .offline)

        #expect(await engine().syncOnce().stop == .offline)
        #expect(try await stored(run).serverState == .unknown)
        #expect(await server.run(run.id) == nil)

        #expect(await engine().syncOnce().stop == nil)
        #expect(try await stored(run).confirmedComplete)
        #expect(await server.receivedSeqs(of: run.id) == Array(0...24))
    }

    @Test("Потерянный ответ: повтор старта и куска сервер узнаёт, точки не задваиваются")
    func lostResponses() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store)
        await server.fail("start", with: .lostResponse)

        #expect(await engine().syncOnce().stop == .offline)
        await server.fail("chunk 10-19", with: .lostResponse)
        let second = await engine().syncOnce()
        #expect(second.stop == .offline && second.startedRuns == 1 && second.uploadedChunks == 1)

        let third = await engine().syncOnce()
        #expect(third.stop == nil && third.duplicateChunks == 1 && third.uploadedChunks == 1)
        #expect(await server.receivedSeqs(of: run.id) == Array(0...24))
        #expect(try await stored(run).confirmedComplete)
    }

    @Test("Два прохода сразу (таймер и возврат сети): второй ждёт первый, запросы не задваиваются")
    func singlePass() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store, claims: [Fixture.loop(2, 14)])
        let engine = engine()

        async let first = engine.syncOnce()
        async let second = engine.syncOnce()
        let reports = await [first, second]

        #expect(reports[0] == reports[1])
        let log = await server.log
        #expect(log == ["start", "chunk 0-9", "chunk 10-14", "claim 0", "chunk 15-24", "finish", "get", "captures"])
    }

    @Test("Старт принят, ответ потерян, а повтор уже «слишком старый» — сервер всё равно знает забег")
    func tooOldButAccepted() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 5, into: store)
        await server.fail("start", with: .lostResponse)
        _ = await engine().syncOnce()
        await server.answerStart(of: run.id, status: 400, code: "run_too_old")

        let report = await engine().syncOnce()

        #expect(report.startedRuns == 1 && report.uploadedChunks == 1)
        #expect(try await stored(run).confirmedComplete)
    }

    // MARK: - 409, 404, missing

    @Test("409: занятые номера не шлются, остаток уходит новыми кусками, датчики — с первым и не запаздывают")
    func conflictSplits() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store)
        await server.fail("chunk 0-9", with: .offline)
        _ = await engine().syncOnce()
        await server.preloadChunk(of: run.id, 3...5)

        let report = await engine().syncOnce()

        #expect(report.stop == nil && report.splitChunks == 1 && report.uploadedChunks == 4)
        #expect(
            await server.log.suffix(7) == [
                "chunk 0-9", "chunk 0-2", "chunk 6-9", "chunk 10-19", "chunk 20-24", "finish", "get",
            ])
        #expect(await server.receivedSeqs(of: run.id) == Array(0...24))
        #expect(await server.lateSensorRecords(of: run.id) == 0)
        let serverRun = try #require(await server.run(run.id))
        #expect(serverRun.chunks[0...2]?.motion?.count == 1 && serverRun.chunks[6...9]?.motion?.isEmpty == true)
        #expect(try await stored(run).confirmedComplete)
    }

    @Test("409 со списком больше 20 кусков: разрезание сходится за несколько кругов")
    func conflictManyOverlaps() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 60, into: store, policy: Fixture.policy(maxPoints: 60))
        await server.fail("chunk 0-59", with: .offline)
        _ = await engine().syncOnce()
        for seq in stride(from: 1, through: 49, by: 2) {
            await server.preloadChunk(of: run.id, seq...seq)  // 25 кусков по одной точке
        }

        let report = await engine().syncOnce()

        #expect(report.stop == nil && report.splitChunks == 2)
        #expect(await server.receivedSeqs(of: run.id) == Array(0...59))
        #expect(try await stored(run).confirmedComplete)
    }

    @Test("409, а все номера уже у сервера: кусок доставлен без отправки")
    func conflictAllTaken() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 10, into: store)
        await server.fail("chunk 0-9", with: .offline)
        _ = await engine().syncOnce()
        await server.preloadChunk(of: run.id, 0...4)
        await server.preloadChunk(of: run.id, 5...9)

        let report = await engine().syncOnce()

        #expect(report.splitChunks == 1 && report.uploadedChunks == 0)
        #expect(await server.receivedSeqs(of: run.id) == Array(0...9))
        #expect(try await stored(run).confirmedComplete)
    }

    @Test("404: сервер не знает забега — старт заново, куски и заявки заново")
    func runNotFound() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store, claims: [Fixture.loop(2, 14)])
        await server.fail("chunk 15-24", with: .offline)
        _ = await engine().syncOnce()
        await server.forget(run.id)

        let second = await engine().syncOnce()
        #expect(second.stop == nil && second.forgottenRuns == 1)
        #expect(try await stored(run).serverState == .unknown)
        #expect(await store.chunks(of: run.id).allSatisfy { !$0.sent })
        #expect(await store.claims(of: run.id).allSatisfy { !$0.sent })

        let third = await engine().syncOnce()
        #expect(third.startedRuns == 1 && third.uploadedChunks == 3 && third.sentClaims == 1 && third.finishedRuns == 1)
        #expect(await server.receivedSeqs(of: run.id) == Array(0...24))
        #expect(await server.run(run.id)?.captures[14] != nil)
        #expect(await server.lateSensorRecords(of: run.id) == 0)
        #expect(try await stored(run).confirmedComplete)
    }

    @Test("После завершения сервер недосчитался точек — дослать только недостающие")
    func resendsMissing() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store)
        await server.fail("finish", with: .offline)
        _ = await engine().syncOnce()
        await server.dropChunk(of: run.id, 10...19)
        await server.preloadChunk(of: run.id, 10...11)

        let second = await engine().syncOnce()
        #expect(second.finishedRuns == 1 && second.requeuedChunks == 1)
        #expect(try await !stored(run).confirmedComplete)

        let before = await server.log.count
        let third = await engine().syncOnce()
        #expect(third.splitChunks == 1 && third.uploadedChunks == 1 && third.finishedRuns == 0)
        #expect(Array(await server.log.dropFirst(before)) == ["chunk 10-19", "chunk 12-19", "get"])
        #expect(await server.receivedSeqs(of: run.id) == Array(0...24))
        #expect(try await stored(run).confirmedComplete)
        #expect(await store.chunks(of: run.id).isEmpty)
    }

    @Test("Досылка по missing ограничена: сервер, который «не видит» точки, не держит забег вечно")
    func resendRoundsAreBounded() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 5, into: store)
        await server.alwaysReportMissing(0...0)

        for _ in 0..<SyncEngine.maxResendRounds {
            #expect(await engine().syncOnce().requeuedChunks == 1)
        }
        #expect(try await !stored(run).confirmedComplete)
        _ = await engine().syncOnce()
        #expect(try await stored(run).confirmedComplete)
        #expect(await server.receivedSeqs(of: run.id) == Array(0...4))
    }

    // MARK: - Завершение

    @Test("Сервер потерял завершение: пустой missing незавершённого забега не подтверждает его")
    func finishLostByServer() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store)
        await server.fail("get", with: .offline)
        _ = await engine().syncOnce()
        await server.unfinish(run.id)

        #expect(await engine().syncOnce().unconfirmedFinishes == 1)
        #expect(try await !stored(run).confirmedComplete)
        #expect(await store.chunks(of: run.id).count == 3)  // куски на телефоне, пока сервер не подтвердит

        let third = await engine().syncOnce()
        #expect(third.finishedRuns == 1)
        #expect(await server.run(run.id)?.lastSeq == 24)
        #expect(try await stored(run).confirmedComplete)
    }

    @Test("Окончательный отказ в завершении записан, забег закрыт на телефоне")
    func finishRejected() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 5, into: store)
        await server.answer("finish", status: 409, code: "last_seq_too_small")

        _ = await engine().syncOnce()

        let local = try await stored(run)
        #expect(local.finishRejectCode == "last_seq_too_small" && local.confirmedComplete)
        #expect(await server.log == ["start", "chunk 0-4", "finish", "get"])
    }

    @Test("Конец «в будущем» по переведённым назад часам: уходит не позже «сейчас»")
    func finishClampedToNow() async throws {
        let run = Fixture.run()
        let recorder = try await Fixture.record(run, points: 5, into: store, finish: false)
        try await recorder.finish(endedAt: Fixture.start + 900)

        _ = await engine(now: Fixture.start + 100).syncOnce()

        #expect(await server.run(run.id)?.endedAtMs == StoragePrecision.milliseconds(Fixture.start + 100))
        #expect(try await stored(run).confirmedComplete)
    }

    @Test("Забег закончился, пока шла синхронизация: конец не затирается и уходит в том же проходе")
    func finishDuringSync() async throws {
        let run = Fixture.run()
        let recorder = try await Fixture.record(run, points: 15, into: store, finish: false)
        await server.whileStarting { try? await recorder.finish(endedAt: Fixture.start + 20) }

        let report = await engine().syncOnce()

        #expect(report.uploadedChunks == 2 && report.finishedRuns == 1)
        let local = try await stored(run)
        #expect(local.serverState == .started && local.confirmedComplete)
        #expect(local.endedAtMs == StoragePrecision.milliseconds(Fixture.start + 20) && local.lastSeq == 14)
        #expect(await server.run(run.id)?.lastSeq == 14)
        #expect(await server.receivedSeqs(of: run.id) == Array(0...14))
    }

    @Test("Во время прохода вошёл другой игрок — прежний движок больше ничего не шлёт с его входом")
    func ownerChangeStopsThePass() async throws {
        try await Fixture.record(Fixture.run(), points: 25, into: store)
        let signedIn = Player(Fixture.owner)
        await server.whileStarting { await signedIn.set("0199ffff-0000-7000-8000-000000000000") }
        let engine = SyncEngine(
            store: store, api: server, ownerId: Fixture.owner, now: { Self.now },
            signedInPlayer: { await signedIn.id })

        let report = await engine.syncOnce()

        #expect(report.stop == .unauthorized)
        #expect(await server.log == ["start"])  // старт ушёл до смены, куски — уже нет
    }

    @Test("Никто не вошёл — ни одного запроса")
    func signedOutSendsNothing() async throws {
        try await Fixture.record(Fixture.run(), points: 5, into: store)
        let engine = SyncEngine(
            store: store, api: server, ownerId: Fixture.owner, now: { Self.now }, signedInPlayer: { nil })

        #expect(await engine.syncOnce().stop == .unauthorized)
        #expect(await server.log.isEmpty)
    }

    actor Player {
        private(set) var id: String?
        init(_ id: String) { self.id = id }
        func set(_ id: String) { self.id = id }
    }

    // MARK: - Отказы

    struct StartRefusal: Sendable, CustomTestStringConvertible {
        let status: Int
        let code: String
        let stop: SyncStop?
        let rejected: Bool
        var testDescription: String { "\(status) \(code)" }
    }

    @Test(
        "Отказ в старте: окончательный — забег больше не шлётся; временный — проход останавливается или откладывает",
        arguments: [
            StartRefusal(status: 409, code: "run_conflict", stop: nil, rejected: true),
            StartRefusal(status: 400, code: "run_too_old", stop: nil, rejected: true),
            StartRefusal(status: 400, code: "run_invalid", stop: nil, rejected: true),
            StartRefusal(status: 403, code: "replay_forbidden", stop: nil, rejected: true),
            StartRefusal(status: 422, code: "config_invalid", stop: nil, rejected: true),
            StartRefusal(status: 400, code: "start_in_future", stop: nil, rejected: false),
            StartRefusal(status: 400, code: "device_clock_invalid", stop: .clockInvalid, rejected: false),
            StartRefusal(status: 403, code: "account_deleting", stop: .accountDeleting, rejected: false),
            StartRefusal(status: 429, code: "too_many", stop: .rateLimited, rejected: false),
            StartRefusal(status: 401, code: "", stop: .unauthorized, rejected: false),
            StartRefusal(status: 503, code: "", stop: .offline, rejected: false),
        ])
    func startRefused(_ refusal: StartRefusal) async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store)
        await server.answerStart(of: run.id, status: refusal.status, code: refusal.code)

        #expect(await engine().syncOnce().stop == refusal.stop)
        let local = try await stored(run)
        #expect(local.serverState == (refusal.rejected ? .rejected : .unknown))
        if refusal.rejected {
            #expect(local.rejectCode == refusal.code)
            #expect(await store.chunks(of: run.id).isEmpty)  // очередь — не история
            let before = await server.log.count
            _ = await engine().syncOnce()
            #expect(await server.log.count == before)  // отвергнутый забег больше не отправляется
        } else {
            #expect(await store.chunks(of: run.id).count == 3)
        }
    }

    @Test("Лимит забегов в сутки откладывает только этот забег")
    func dailyRunLimit() async throws {
        let first = Fixture.run(startedAt: Fixture.start)
        let second = Fixture.run(startedAt: Fixture.start + 3_600)
        try await Fixture.record(first, points: 5, into: store)
        try await Fixture.record(second, points: 5, into: store)
        await server.answerStart(of: first.id, status: 429, code: "daily_run_limit")

        let report = await engine().syncOnce()

        #expect(report.stop == nil && report.deferredRuns == 1 && report.startedRuns == 1)
        #expect(try await stored(first).serverState == .unknown)
        #expect(try await stored(second).confirmedComplete)
    }

    @Test("Кусок с неверными точками выбрасывается, остальные идут; дыру дослать нечем — забег закрывается")
    func invalidChunkDropped() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store)
        await server.answer("chunk 10-19", status: 400, code: "chunk_invalid")

        let report = await engine().syncOnce()

        #expect(report.stop == nil && report.droppedChunks == 1 && report.uploadedChunks == 2)
        #expect(await server.receivedSeqs(of: run.id) == Array(0...9) + Array(20...24))
        #expect(try await stored(run).confirmedComplete)
    }

    @Test("Испорчены только датчики: точки уходят без них, территория забега не теряется")
    func invalidSensorsStripped() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 5, into: store)
        var chunk = try #require(await store.chunks(of: run.id).first)
        chunk.motion.append(MotionSample(timestamp: Fixture.start - 3_600, activity: .automotive))  // вне окна
        await store.save(chunk)

        let report = await engine().syncOnce()

        #expect(report.strippedChunks == 1 && report.uploadedChunks == 1 && report.droppedChunks == 0)
        #expect(await server.run(run.id)?.chunks[0...4]?.motion?.isEmpty == true)
        #expect(try await stored(run).confirmedComplete)
    }

    @Test("Точки «из будущего» по переведённым назад часам: кусок ждёт, а не выбрасывается")
    func timeFutureWaits() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store)

        let early = await engine(now: Fixture.start - 100).syncOnce()  // «сейчас» + 2 мин = 20-я секунда забега
        #expect(early.uploadedChunks == 2 && early.droppedChunks == 0 && early.finishedRuns == 0)
        #expect(early.deferredRuns == 1)  // расписание повторит через час, а не через 15 с
        #expect(await store.chunks(of: run.id).filter { !$0.sent }.map(\.firstSeq) == [20])

        _ = await engine(now: Fixture.start + 100).syncOnce()
        #expect(await server.receivedSeqs(of: run.id) == Array(0...24))
        #expect(try await stored(run).confirmedComplete)
    }

    @Test("Окно приёма закрыто: неотправленные куски выбрасываются")
    func uploadWindowClosed() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store)
        await server.answer("chunk 10-19", status: 409, code: "upload_window_closed")

        let report = await engine().syncOnce()

        #expect(report.droppedChunks == 2 && report.finishedRuns == 1)
        #expect(await server.receivedSeqs(of: run.id) == Array(0...9))
        #expect(try await stored(run).confirmedComplete)
    }

    @Test("Лимит объёма забега (413): неотправленные куски выбрасываются")
    func runStorageLimit() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store)
        await server.answer("chunk 20-24", status: 413, code: "run_storage_limit")

        let report = await engine().syncOnce()

        #expect(report.droppedChunks == 1 && report.uploadedChunks == 2 && report.finishedRuns == 1)
        #expect(try await stored(run).confirmedComplete)
    }

    @Test("Суточный лимит объёма: куски ждут завтра, а заявка на уже доставленные точки уходит сейчас")
    func dailyStorageLimit() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store, claims: [Fixture.loop(2, 14)])
        await server.answer("chunk 15-24", status: 429, code: "daily_storage_limit")

        let report = await engine().syncOnce()

        #expect(report.storageLimitReached && report.stop == nil && report.sentClaims == 1 && report.finishedRuns == 0)
        #expect(await store.chunks(of: run.id).filter { !$0.sent }.map(\.firstSeq) == [15])

        await server.answer("chunk 15-24", status: 429, code: "")
        _ = await engine().syncOnce()
        #expect(try await stored(run).confirmedComplete)
    }

    // MARK: - Заявки

    @Test("Окончательный отказ в заявке не повторяется, остальные заявки идут")
    func claimRefused() async throws {
        let run = Fixture.run()
        try await Fixture.record(
            run, points: 25, into: store, claims: [Fixture.loop(0, 9), Fixture.loop(12, 20)], finish: false)
        await server.answer("claim 0", status: 409, code: "claim_conflict")

        let report = await engine().syncOnce()

        #expect(report.sentClaims == 1)
        #expect(await store.claims(of: run.id).map(\.refusedCode) == ["claim_conflict", nil])
        _ = await engine().syncOnce()  // забег не закончен — проход снова проходит по его заявкам
        #expect(await server.log.filter { $0.hasPrefix("claim") } == ["claim 0", "claim 1"])
    }

    @Test("Частые заявки (429 без claim_limit) — остановка, заявка ждёт; claim_limit — отказ навсегда")
    func claimLimits() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store, claims: [Fixture.loop(2, 14)])
        await server.answer("claim 0", status: 429, code: "too_many")
        #expect(await engine().syncOnce().stop == .rateLimited)
        #expect(await store.claims(of: run.id).first.map { !$0.sent && $0.refusedCode == nil } == true)

        await server.answer("claim 0", status: 429, code: "claim_limit")
        _ = await engine().syncOnce()
        #expect(await store.claims(of: run.id).first?.refusedCode == "claim_limit")
        #expect(try await stored(run).confirmedComplete)
    }

    @Test("Сервер забыл уже закрытый на телефоне забег: заявки помечаются неудачными, опрос прекращается")
    func capturesOfForgottenRun() async throws {
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store, claims: [Fixture.loop(2, 14)])
        _ = await engine().syncOnce()
        await server.forget(run.id)

        _ = await engine().syncOnce()
        let claim = try #require(await store.claims(of: run.id).first)
        #expect(claim.outcome?.status == "failed" && claim.outcome?.rejectCode == "run_not_found" && claim.isSettled)

        let before = await server.log.count
        _ = await engine().syncOnce()
        #expect(await server.log.count == before)
    }

    // MARK: - Нечитаемые куски

    @Test("Нечитаемый кусок (повреждённая строка в базе) стирается вместе с остальными, когда забег подтверждён")
    func unreadableChunkWipedOnConfirm() async throws {
        let store = UnreadableChunkStore()
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store)
        await store.makeUnreadable(run.id, firstSeq: 10)

        let report = await SyncEngine(store: store, api: server, ownerId: Fixture.owner, now: { Self.now }).syncOnce()

        // Точек нечитаемого куска сервер недосчитается, дослать их нечем — забег подтверждается без них.
        #expect(report.stop == nil)
        #expect(await server.receivedSeqs(of: run.id) == Array(0...9) + Array(20...24))
        #expect(await store.runs().first?.confirmedComplete == true)
        #expect(await store.storedChunks(of: run.id).isEmpty)
    }

    @Test("Нечитаемый кусок стирается и у отвергнутого забега")
    func unreadableChunkWipedOnReject() async throws {
        let store = UnreadableChunkStore()
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store)
        await store.makeUnreadable(run.id, firstSeq: 10)
        await server.answerStart(of: run.id, status: 422, code: "config_invalid")

        _ = await SyncEngine(store: store, api: server, ownerId: Fixture.owner, now: { Self.now }).syncOnce()

        #expect(await store.runs().first?.serverState == .rejected)
        #expect(await store.storedChunks(of: run.id).isEmpty)
    }

    // MARK: - Запрос

    @Test("Кусок в запросе: миллисекунды, точность хранения, источник координат, датчики")
    func chunkRequest() throws {
        let chunk = SealedChunk(
            runId: UUID(), firstSeq: 7,
            points: [
                TrackPoint(
                    seq: 7, coordinate: Coordinate(latitude: 52.1, longitude: 23.7), timestamp: Fixture.start + 1.25,
                    horizontalAccuracy: 4.5, speed: nil)
            ],
            sources: [.simulated],
            motion: [MotionSample(timestamp: Fixture.start + 1, activity: .cycling)],
            steps: [PedometerSample(start: Fixture.start, end: Fixture.start + 1, steps: nil)],
            sensorsCompleteThroughMs: 1_790_000_000_900)

        let request = SyncEngine.request(for: chunk, sentAtMs: 1_790_000_002_000)

        let point = try #require(request.points?.first)
        #expect(point.seq == 7 && point.t == 1_790_000_001_250 && point.lat == 52.1 && point.lon == 23.7)
        #expect(point.acc == 4.5 && point.speed == nil && point.flags == 1)
        #expect(request.motion?.first?.activity == .cycling && request.motion?.first?.t == 1_790_000_001_000)
        #expect(request.steps?.first?.steps == nil && request.steps?.first?.end == 1_790_000_001_000)
        #expect(request.sensorsCompleteThroughMs == 1_790_000_000_900 && request.sentAtMs == 1_790_000_002_000)
    }
}

/// Хранилище, в котором часть кусков не читается, — как повреждённые строки в базе: `GRDBSyncStore` их пропускает,
/// `chunks(of:)` их не возвращает, но в хранилище они есть.
actor UnreadableChunkStore: SyncStore {
    let inner = InMemorySyncStore()
    private var unreadable: [UUID: Set<Int>] = [:]

    func makeUnreadable(_ runId: UUID, firstSeq: Int) { unreadable[runId, default: []].insert(firstSeq) }

    /// Все куски забега, какие есть в хранилище, — и нечитаемые.
    func storedChunks(of runId: UUID) async -> [SealedChunk] { await inner.chunks(of: runId) }

    func runs() async -> [LocalRun] { await inner.runs() }
    func insert(_ run: LocalRun) async { await inner.insert(run) }
    func updateRun(_ id: UUID, _ change: @Sendable (inout LocalRun) -> Void) async -> LocalRun? {
        await inner.updateRun(id, change)
    }
    func chunks(of runId: UUID) async -> [SealedChunk] {
        let hidden = unreadable[runId] ?? []
        return await inner.chunks(of: runId).filter { !hidden.contains($0.firstSeq) }
    }
    func save(_ chunk: SealedChunk) async { await inner.save(chunk) }
    func seal(_ chunk: SealedChunk, progress: @Sendable (inout LocalRun) -> Void) async {
        await inner.seal(chunk, progress: progress)
    }
    func replaceChunk(of runId: UUID, firstSeq: Int, with pieces: [SealedChunk]) async {
        await inner.replaceChunk(of: runId, firstSeq: firstSeq, with: pieces)
    }
    func deleteChunk(of runId: UUID, firstSeq: Int) async { await inner.deleteChunk(of: runId, firstSeq: firstSeq) }
    func deleteChunks(of runId: UUID) async { await inner.deleteChunks(of: runId) }
    func claims(of runId: UUID) async -> [PendingClaim] { await inner.claims(of: runId) }
    func lastClaimNo(of runId: UUID) async -> Int? { await inner.lastClaimNo(of: runId) }
    func save(_ claim: PendingClaim) async { await inner.save(claim) }
}
