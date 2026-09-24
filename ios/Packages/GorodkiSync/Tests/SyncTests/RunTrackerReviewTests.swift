import Foundation
import GameCore
import Testing

@testable import Sync

/// Хранилище, которое по команде один раз не записывает кусок, забег или заявку — как кончившееся место на диске.
/// Запечатывание (`seal`) — одна транзакция, как в GRDB: не записался кусок или забег — не записано ничего.
actor FailingOnceStore: SyncStore {
    struct DiskFull: Error {}

    let inner = InMemorySyncStore()
    private var failNextChunk = false
    private var failNextRun = false
    private var failNextClaim = false

    func failNextChunkSave() { failNextChunk = true }
    /// Следующая запись забега (прогресс вместе с куском или конец забега) не пройдёт.
    func failNextRunWrite() { failNextRun = true }
    func failNextClaimSave() { failNextClaim = true }

    func runs() async -> [LocalRun] { await inner.runs() }
    func insert(_ run: LocalRun) async { await inner.insert(run) }
    func updateRun(_ id: UUID, _ change: @Sendable (inout LocalRun) -> Void) async throws -> LocalRun? {
        try fail(&failNextRun)
        return await inner.updateRun(id, change)
    }
    func chunks(of runId: UUID) async -> [SealedChunk] { await inner.chunks(of: runId) }
    func save(_ chunk: SealedChunk) async throws {
        try fail(&failNextChunk)
        await inner.save(chunk)
    }
    func seal(_ chunk: SealedChunk, progress: @Sendable (inout LocalRun) -> Void) async throws {
        try fail(&failNextChunk)
        try fail(&failNextRun)
        await inner.seal(chunk, progress: progress)
    }
    private func fail(_ armed: inout Bool) throws {
        if armed {
            armed = false
            throw DiskFull()
        }
    }
    func replaceChunk(of runId: UUID, firstSeq: Int, with pieces: [SealedChunk]) async {
        await inner.replaceChunk(of: runId, firstSeq: firstSeq, with: pieces)
    }
    func deleteChunk(of runId: UUID, firstSeq: Int) async { await inner.deleteChunk(of: runId, firstSeq: firstSeq) }
    func claims(of runId: UUID) async -> [PendingClaim] { await inner.claims(of: runId) }
    func save(_ claim: PendingClaim) async throws {
        try fail(&failNextClaim)
        await inner.save(claim)
    }
}

@Suite("Идущий забег: исправления по ревью")
struct RunTrackerReviewTests {
    typealias Walk = RunSessionTests.Walk

    @Test("Второй забег: поступившее, пока он записывается, копится и уходит в него по порядку — не теряется")
    func inputsDuringSecondStartAreKept() async throws {
        let store = InMemorySyncStore()
        let tracker = RunTracker()
        try await tracker.start { try await RunSession.start(Fixture.run(), store: store, rules: .version1) }
        try await tracker.finish(at: Fixture.start + 10)  // цикл очереди уже работает

        let second = Fixture.run(startedAt: Fixture.start + 100)
        var walk = Walk()
        walk.time = Fixture.start + 100
        let fixes = walk.straight(seconds: 5)
        let starting = Task {
            try await tracker.start {
                try await Task.sleep(for: .milliseconds(200))  // запись забега в базу не мгновенна
                return try await RunSession.start(second, store: store, rules: .version1)
            }
        }
        try await Task.sleep(for: .milliseconds(50))
        tracker.send(.motion(MotionSample(timestamp: Fixture.start + 90, activity: .automotive)))  // «уже в машине»
        for fix in fixes {
            tracker.send(.fix(fix, receivedAt: fix.timestamp + 1))
        }
        try await starting.value
        try await tracker.finish(at: walk.time)

        let chunks = await store.chunks(of: second.id)
        #expect(chunks.flatMap(\.points).map(\.seq) == Array(0..<fixes.count))
        #expect(chunks.flatMap(\.motion).map(\.activity) == [.automotive])
    }

    @Test("«Финиш» не записался (нет места) — забег продолжается: точки снова принимаются, повторный «Финиш» проходит")
    func failedFinishKeepsTheRunOpen() async throws {
        let store = FailingOnceStore()
        let tracker = RunTracker()
        try await tracker.start { try await RunSession.start(Fixture.run(), store: store, rules: .version1) }
        var walk = Walk()
        let first = walk.straight(seconds: 3)
        for fix in first { tracker.send(.fix(fix, receivedAt: fix.timestamp + 1)) }
        await tracker.flush()

        await store.failNextChunkSave()
        await #expect(throws: FailingOnceStore.DiskFull.self) { try await tracker.finish(at: walk.time) }
        let afterFailure = await tracker.state
        #expect(afterFailure.isRunning && afterFailure.storageFailed)

        let more = walk.fix(east: 10, north: 0)
        tracker.send(.fix(more, receivedAt: more.timestamp + 1))
        await tracker.flush()
        #expect(await !tracker.state.storageFailed)
        try await tracker.finish(at: walk.time)

        let run = try #require(await store.runs().first)
        #expect(run.lastSeq == 3)
        #expect(await store.chunks(of: run.id).flatMap(\.points).map(\.seq) == [0, 1, 2, 3])
    }

    @Test("Остаток записан, а сам конец забега — нет (нет места): забег продолжается, точки снова принимаются")
    func failedEndRecordKeepsTheRunOpen() async throws {
        let store = FailingOnceStore()
        let tracker = RunTracker()
        try await tracker.start {
            // Кусок на каждую точку: к «Финишу» остатка нет, и не записывается именно конец забега.
            try await RunSession.start(
                Fixture.run(), store: store, rules: .version1, policy: Fixture.policy(maxPoints: 1))
        }
        var walk = Walk()
        for fix in walk.straight(seconds: 3) { tracker.send(.fix(fix, receivedAt: fix.timestamp + 1)) }
        await tracker.flush()

        await store.failNextRunWrite()
        await #expect(throws: FailingOnceStore.DiskFull.self) { try await tracker.finish(at: walk.time) }
        #expect(await tracker.state.isRunning)
        #expect(try #require(await store.runs().first).isFinishedLocally == false)

        let more = walk.fix(east: 10, north: 0)
        tracker.send(.fix(more, receivedAt: more.timestamp + 1))
        await tracker.flush()
        #expect(await !tracker.state.storageFailed)  // точка записана — запись не «застряла» в конце
        try await tracker.finish(at: walk.time)

        let run = try #require(await store.runs().first)
        #expect(run.lastSeq == 3)
        #expect(await store.chunks(of: run.id).flatMap(\.points).map(\.seq) == [0, 1, 2, 3])
    }

    @Test("Ошибка записи не «забывается» от записи датчика: предупреждение гаснет только после записанной точки")
    func storageFailureIsNotClearedBySensors() async throws {
        let store = FailingOnceStore()
        let tracker = RunTracker()
        try await tracker.start {
            try await RunSession.start(
                Fixture.run(), store: store, rules: .version1, policy: Fixture.policy(maxPoints: 1))
        }
        var walk = Walk()
        await store.failNextChunkSave()
        let failed = walk.fix(east: 0, north: 0)
        tracker.send(.fix(failed, receivedAt: failed.timestamp + 1))
        tracker.send(.motion(MotionSample(timestamp: failed.timestamp, activity: .walking)))
        await tracker.flush()

        #expect(await tracker.state.storageFailed)
    }

    @Test(
        "Демо-повтор после перезапуска не продолжается — настоящая геопозиция в нём была бы подделкой; он закрывается")
    func replayIsNotResumed() async throws {
        let store = InMemorySyncStore()
        let now = Fixture.start + 600
        var run = Fixture.run(startedAt: now - 300)
        run.source = .replay
        await store.insert(run)
        _ = await store.updateRun(run.id) { $0.lastPointMs = StoragePrecision.milliseconds(now - 20) }

        let session = try await RunTracker.recover(
            store: store, deviceId: Fixture.device, signedIn: Fixture.owner, now: now, rules: { _ in .version1 })

        #expect(session == nil)
        #expect(try #require(await store.runs().first).isFinishedLocally)
    }

    @Test("После перезапуска без запечатанного куска датчики дозапрашиваются с начала окна — не раньше")
    func sensorsResumeNotBeforeWindow() async throws {
        let store = InMemorySyncStore()
        let run = Fixture.run()
        let session = try await RunSession.start(run, store: store, rules: .version1)
        #expect(await session.sensorsResumeFrom == Fixture.start - 60)  // окно — за минуту до старта
    }

    @Test("Запись: поступившее, пока «Старт» пишет забег в базу, — в записи, как и в забеге (первая запись CoreMotion)")
    func recordingKeepsInputsDuringStart() async throws {
        let store = InMemorySyncStore()
        let tracker = RunTracker()
        let entered = Gate()
        let written = Gate()
        let starting = Task {
            try await tracker.start(recording: true) {
                entered.open()
                await written.wait()  // забег ещё пишется в базу
                return try await RunSession.start(Fixture.run(), store: store, rules: .version1)
            }
        }
        await entered.wait()
        var walk = Walk()
        let fix = walk.fix(east: 0, north: 0)
        // Текущий вид движения CoreMotion сообщает сразу и с давним началом — следующей записи может не быть долго.
        tracker.send(.motion(MotionSample(timestamp: Fixture.start - 600, activity: .automotive)))
        tracker.send(.fix(fix, receivedAt: fix.timestamp + 1))
        written.open()
        try await starting.value
        try await tracker.finish(at: Fixture.start + 60)

        let recording = try #require(await tracker.takeRecording())
        #expect(recording.entries.map(\.at) == [-600, fix.timestamp + 1 - Fixture.start])
        #expect(recording.entries.first?.input == .motion(time: -600, activity: .automotive))
        let runId = try #require(await tracker.state.runId)
        #expect(await store.chunks(of: runId).flatMap(\.motion).map(\.activity) == [.automotive])  // забег её получил
    }

    @Test("Запись: запоздавший датчик записан после того, что пришло раньше него, — в повторе придёт в том же порядке")
    func lateSensorKeepsReceiptOrderInRecording() async throws {
        let tracker = RunTracker()
        try await tracker.start(recording: true) {
            try await RunSession.start(Fixture.run(), store: InMemorySyncStore(), rules: .version1)
        }
        var walk = Walk()
        walk.time = Fixture.start + 30
        let fix = walk.fix(east: 0, north: 0)
        tracker.send(.fix(fix, receivedAt: Fixture.start + 31))
        tracker.send(.motion(MotionSample(timestamp: Fixture.start + 10, activity: .automotive)))  // пришла позже точки
        try await tracker.finish(at: Fixture.start + 60)

        let recording = try #require(await tracker.takeRecording())
        #expect(recording.entries.map(\.at) == [31, 31])
        guard case .motion(let time, _) = recording.entries[1].input else {
            Issue.record("второй должна быть запись вида движения")
            return
        }
        #expect(time == 10)  // время события — прежнее
    }
}
