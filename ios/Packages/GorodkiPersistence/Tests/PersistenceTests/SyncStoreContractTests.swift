import Foundation
import GameCore
import Persistence
import Sync
import Testing

/// Какое хранилище проверяем: у очереди в памяти (тесты синхронизации) и в базе (приложение) одно поведение.
enum StoreKind: String, CaseIterable, CustomTestStringConvertible, Sendable {
    case memory
    case grdb

    var testDescription: String { rawValue }

    func make() throws -> any SyncStore {
        switch self {
        case .memory: InMemorySyncStore()
        case .grdb: GRDBSyncStore(try AppDatabase.inMemory())
        }
    }
}

/// Общие заготовки: забег, кусок, заявка.
enum Sample {
    static let device = UUID()

    static func run(_ id: UUID = UUID(), startedAtMs: Int64 = 1_790_000_000_000) -> LocalRun {
        LocalRun(
            id: id, ownerId: "player-1", league: .run, configVersion: 1, startedAtMs: startedAtMs,
            deviceId: device, appVersion: "1.0 (1)", motionAuthorized: true)
    }

    static func chunk(_ runId: UUID, firstSeq: Int, count: Int = 3) -> SealedChunk {
        let points = (firstSeq..<firstSeq + count).map { seq in
            TrackPoint(
                seq: seq, coordinate: Coordinate(latitude: 52.09 + Double(seq) * 1e-5, longitude: 23.68),
                timestamp: 1_790_000_000 + Double(seq), horizontalAccuracy: 5, speed: seq.isMultiple(of: 2) ? 3 : nil)
        }
        return SealedChunk(
            runId: runId, firstSeq: firstSeq, points: points,
            sources: points.map { $0.seq == firstSeq ? PointSource.simulated : PointSource() },
            motion: [MotionSample(timestamp: 1_790_000_000.5, activity: .running)],
            steps: [PedometerSample(start: 1_790_000_000, end: 1_790_000_005, steps: nil)],
            sensorsCompleteThroughMs: 1_790_000_000_000 + Int64(firstSeq + count) * 1_000)
    }

    static func claim(_ runId: UUID, _ claimNo: Int) -> PendingClaim {
        PendingClaim(
            runId: runId, claimNo: claimNo,
            loop: LoopClaim(startSeq: claimNo * 10, endSeq: claimNo * 10 + 9, closure: .crossing, estimatedArea: 5_000))
    }
}

/// Поведение очереди, на которое опираются запись забега и синхронизация (GorodkiSync, `SyncStore`).
@Suite("Хранилище очереди: одинаковое поведение в памяти и в базе")
struct SyncStoreContractTests {
    @Test("Забеги — по времени начала; повторная вставка заменяет", arguments: StoreKind.allCases)
    func runsAreOrderedByStart(kind: StoreKind) async throws {
        let store = try kind.make()
        let late = Sample.run(startedAtMs: 2_000)
        let early = Sample.run(startedAtMs: 1_000)
        try await store.insert(late)
        try await store.insert(early)
        var changed = late
        changed.appVersion = "1.1 (2)"
        try await store.insert(changed)

        let runs = try await store.runs()

        #expect(runs.map(\.id) == [early.id, late.id])
        #expect(runs.last?.appVersion == "1.1 (2)")
    }

    @Test("Изменение забега возвращает новое состояние и сохраняется; чужого забега нет", arguments: StoreKind.allCases)
    func updateRunChangesInPlace(kind: StoreKind) async throws {
        let store = try kind.make()
        let run = Sample.run()
        try await store.insert(run)

        let updated = try await store.updateRun(run.id) { run in
            run.serverState = .started
            run.recordedThroughSeq = 41
        }
        let missing = try await store.updateRun(UUID()) { $0.finishSent = true }

        #expect(updated?.serverState == .started)
        #expect(missing == nil)
        let stored = try #require(try await store.runs().first)
        #expect(stored.serverState == .started)
        #expect(stored.recordedThroughSeq == 41)
    }

    @Test("Одновременные изменения разных полей не затирают друг друга", arguments: StoreKind.allCases)
    func concurrentUpdatesKeepBothFields(kind: StoreKind) async throws {
        let store = try kind.make()
        let run = Sample.run()
        try await store.insert(run)

        // Как в приложении: запись забега двигает свои поля, синхронизация — свои, одновременно. Каждый шаг —
        // «прочитать и прибавить»: если хранилище где-то запишет забег, прочитанный до чужого изменения, счёт не сойдётся.
        let steps = 200
        try await withThrowingTaskGroup(of: Void.self) { group in
            group.addTask {
                for _ in 0..<steps {
                    try await store.updateRun(run.id) { $0.recordedThroughSeq += 1 }
                }
            }
            group.addTask {
                for _ in 0..<steps {
                    try await store.updateRun(run.id) { $0.resendRounds += 1 }
                }
            }
            try await group.waitForAll()
        }

        let stored = try #require(try await store.runs().first)
        #expect(stored.recordedThroughSeq == steps - 1)
        #expect(stored.resendRounds == steps)
    }

    @Test("Куски — по номеру первой точки, своего забега; тот же ключ заменяет", arguments: StoreKind.allCases)
    func chunksAreOrderedAndKeyed(kind: StoreKind) async throws {
        let store = try kind.make()
        let run = UUID()
        let other = UUID()
        try await store.save(Sample.chunk(run, firstSeq: 10))
        try await store.save(Sample.chunk(run, firstSeq: 0))
        try await store.save(Sample.chunk(other, firstSeq: 0))
        var sent = Sample.chunk(run, firstSeq: 10)
        sent.sent = true
        try await store.save(sent)

        let chunks = try await store.chunks(of: run)

        #expect(chunks.map(\.firstSeq) == [0, 10])
        #expect(chunks.last?.sent == true)
        #expect(try await store.chunks(of: other).count == 1)
    }

    @Test("Разрезание заменяет кусок частями, удаление убирает", arguments: StoreKind.allCases)
    func replaceAndDeleteChunks(kind: StoreKind) async throws {
        let store = try kind.make()
        let run = UUID()
        try await store.save(Sample.chunk(run, firstSeq: 0, count: 6))
        try await store.save(Sample.chunk(run, firstSeq: 6))

        try await store.replaceChunk(
            of: run, firstSeq: 0, with: [Sample.chunk(run, firstSeq: 0), Sample.chunk(run, firstSeq: 3)])
        #expect(try await store.chunks(of: run).map(\.firstSeq) == [0, 3, 6])
        #expect(try await store.chunks(of: run).first?.points.count == 3)

        try await store.deleteChunk(of: run, firstSeq: 3)
        try await store.deleteChunk(of: run, firstSeq: 99)  // нет такого — не ошибка
        #expect(try await store.chunks(of: run).map(\.firstSeq) == [0, 6])
    }

    @Test("Стирание кусков забега убирает все его куски, чужие остаются", arguments: StoreKind.allCases)
    func deleteAllChunksOfRun(kind: StoreKind) async throws {
        let store = try kind.make()
        let run = UUID()
        let other = UUID()
        try await store.save(Sample.chunk(run, firstSeq: 0))
        try await store.save(Sample.chunk(run, firstSeq: 3))
        try await store.save(Sample.chunk(other, firstSeq: 0))

        try await store.deleteChunks(of: run)
        try await store.deleteChunks(of: UUID())  // нет такого забега — не ошибка

        #expect(try await store.chunks(of: run).isEmpty)
        #expect(try await store.chunks(of: other).count == 1)
    }

    @Test(
        "Запечатывание: кусок сохранён, прогресс забега изменён; без забега кусок всё равно сохранён",
        arguments: StoreKind.allCases)
    func sealSavesChunkAndProgress(kind: StoreKind) async throws {
        let store = try kind.make()
        let run = Sample.run()
        try await store.insert(run)

        let orphan = UUID()
        try await store.seal(Sample.chunk(run.id, firstSeq: 0)) { $0.recordedThroughSeq = 2 }
        try await store.seal(Sample.chunk(orphan, firstSeq: 0)) { $0.recordedThroughSeq = 99 }

        #expect(try await store.chunks(of: run.id) == [Sample.chunk(run.id, firstSeq: 0)])
        #expect(try await store.chunks(of: orphan).count == 1)
        #expect(try await store.runs().map(\.recordedThroughSeq) == [2])
    }

    @Test("Стирание очереди убирает забеги, куски и заявки", arguments: StoreKind.allCases)
    func removeAllWipes(kind: StoreKind) async throws {
        let store = try kind.make()
        let run = Sample.run()
        try await store.insert(run)
        try await store.save(Sample.chunk(run.id, firstSeq: 0))
        try await store.save(Sample.claim(run.id, 0))

        try await store.removeAll()

        #expect(try await store.runs().isEmpty)
        #expect(try await store.chunks(of: run.id).isEmpty)
        #expect(try await store.claims(of: run.id).isEmpty)
        #expect(try await store.lastClaimNo(of: run.id) == nil)
    }

    @Test(
        "Стирание забега убирает его куски и заявки, чужие остаются; нет забега — не ошибка",
        arguments: StoreKind.allCases)
    func removeRunWipesOnlyIt(kind: StoreKind) async throws {
        let store = try kind.make()
        let run = Sample.run()
        let other = Sample.run()
        for id in [run.id, other.id] {
            try await store.insert(Sample.run(id))
            try await store.save(Sample.chunk(id, firstSeq: 0))
            try await store.save(Sample.claim(id, 0))
        }

        try await store.removeRun(run.id)
        try await store.removeRun(UUID())

        #expect(try await store.runs().map(\.id) == [other.id])
        #expect(try await store.chunks(of: run.id).isEmpty)
        #expect(try await store.claims(of: run.id).isEmpty)
        #expect(try await store.lastClaimNo(of: run.id) == nil)
        #expect(try await store.chunks(of: other.id).count == 1)
        #expect(try await store.claims(of: other.id).count == 1)
    }

    @Test("Заявки — по номеру; тот же номер заменяет", arguments: StoreKind.allCases)
    func claimsAreOrderedAndKeyed(kind: StoreKind) async throws {
        let store = try kind.make()
        let run = UUID()
        try await store.save(Sample.claim(run, 1))
        try await store.save(Sample.claim(run, 0))
        var settled = Sample.claim(run, 1)
        settled.outcome = try JSONDecoder().decode(
            ClaimOutcome.self, from: Data(#"{"status":"applied","areaSquareMeters":4321}"#.utf8))
        try await store.save(settled)

        let claims = try await store.claims(of: run)

        #expect(claims.map(\.claimNo) == [0, 1])
        #expect(claims.last?.isSettled == true)
        #expect(try await store.claims(of: UUID()).isEmpty)
    }

    @Test("Последний номер заявки — наибольший в забеге; заявок нет — nil", arguments: StoreKind.allCases)
    func lastClaimNoIsLargest(kind: StoreKind) async throws {
        let store = try kind.make()
        let run = UUID()
        try await store.save(Sample.claim(run, 2))
        try await store.save(Sample.claim(run, 0))
        try await store.save(Sample.claim(UUID(), 7))

        #expect(try await store.lastClaimNo(of: run) == 2)
        #expect(try await store.lastClaimNo(of: UUID()) == nil)
    }

    @Test("Всё записанное читается без потерь", arguments: StoreKind.allCases)
    func valuesSurviveRoundTrip(kind: StoreKind) async throws {
        let store = try kind.make()
        var run = Sample.run()
        run.source = .replay
        run.recordedThroughSeq = 9
        run.lastPointMs = 1_790_000_009_000
        run.sealedSensorsMarkMs = 1_790_000_009_500
        run.endedAtMs = 1_790_000_010_000
        run.lastSeq = 9
        run.serverState = .rejected
        run.rejectCode = "config_unknown"
        run.finishRejectCode = "finish_invalid"
        run.confirmedComplete = true
        run.resendRounds = 2
        run.fogNewCells = 42
        var summary = RunSummary()
        summary.points = 10
        summary.acceptedPoints = 9
        summary.distanceMeters = 12.5
        summary.breaks = ["vehicle": 1, "someFutureIssue": 2]
        summary.fogCells = 300
        summary.fogAreaSquareMeters = 10_300
        summary.fogNewSquareMeters = 4_100
        summary.fogNewIsLowerBound = true
        summary.latitude = 52.1
        summary.endedAtLimit = true
        run.summary = summary
        let chunk = Sample.chunk(run.id, firstSeq: 0, count: 10)
        var claim = Sample.claim(run.id, 0)
        claim.sent = true
        claim.refusedCode = "daily_limit"
        var settled = Sample.claim(run.id, 1)
        settled.sent = true
        settled.outcome = ClaimOutcome(
            status: "applied", areaSquareMeters: 900, areaByOutcome: ["claimedNeutral": 600, "futureOutcome": 300])

        try await store.insert(run)
        try await store.save(chunk)
        try await store.save(claim)
        try await store.save(settled)

        #expect(try await store.runs() == [run])
        #expect(try await store.chunks(of: run.id) == [chunk])
        #expect(try await store.claims(of: run.id) == [claim, settled])
    }
}
