import Foundation
import GameCore
import Synchronization
import Testing

@testable import Sync

@Suite("Идущий забег: точки, датчики и таймер — одной очередью")
struct RunTrackerTests {
    typealias Walk = RunSessionTests.Walk

    final class Counter: Sendable {
        private let value = Mutex(0)
        func bump() { value.withLock { $0 += 1 } }
        var count: Int { value.withLock { $0 } }
    }

    private let store = InMemorySyncStore()

    private func session(
        _ run: LocalRun = Fixture.run(), policy: ChunkPolicy = Fixture.policy(maxPoints: 120)
    ) -> @Sendable () async throws -> RunSession {
        let store = self.store
        return { try await RunSession.start(run, store: store, rules: .version1, policy: policy) }
    }

    /// Источник шлёт точки сразу по получении: телефон получил точку через секунду после её времени.
    private func send(_ tracker: RunTracker, _ fixes: [LocationFix]) {
        for fix in fixes {
            tracker.send(.fix(fix, receivedAt: fix.timestamp + 1))
        }
    }

    private func seqs(_ runId: UUID?) async throws -> [Int] {
        await store.chunks(of: try #require(runId)).flatMap(\.points).map(\.seq)
    }

    @Test("Старт → точки → петля → «Финиш»: всё в очереди подряд; синхронизацию зовут на старте, петле и конце")
    func startLoopFinish() async throws {
        let queued = Counter()
        let tracker = RunTracker(onQueued: { queued.bump() })
        try await tracker.start(session())
        #expect(queued.count == 1)

        var walk = Walk()
        let square = walk.square(side: 80)
        send(tracker, square)
        await tracker.flush()
        let running = await tracker.state
        #expect(running.isRunning && running.stats.loops == 1 && running.stats.points == square.count)
        guard case .loopClaimed? = running.lastEvent else {
            Issue.record("петля не заявлена: \(String(describing: running.lastEvent))")
            return
        }
        let afterLoop = queued.count
        #expect(afterLoop >= 2)

        try await tracker.finish(at: walk.time)
        send(tracker, [walk.fix(east: 5, north: 5)])  // после «Финиша» — не этого забега
        await tracker.flush()

        let ended = await tracker.state
        #expect(!ended.isRunning && ended.runId == running.runId)
        let run = try #require(await store.runs().first)
        #expect(run.isFinishedLocally && run.lastSeq == square.count - 1)
        #expect(try await seqs(run.id) == Array(0..<square.count))
        #expect(queued.count > afterLoop)
    }

    @Test("Второй «Старт» — пока первый начинается и когда забег идёт — отклоняется до записи в базу")
    func secondStartIsRejected() async throws {
        let tracker = RunTracker()
        let make = session()
        let first = Task {
            try await tracker.start {
                try await Task.sleep(for: .milliseconds(200))
                return try await make()
            }
        }
        try await Task.sleep(for: .milliseconds(50))
        await #expect(throws: TrackerError.alreadyRunning) { try await tracker.start(session()) }
        try await first.value

        let calls = Counter()
        await #expect(throws: TrackerError.alreadyRunning) {
            try await tracker.start {
                calls.bump()
                throw CancellationError()
            }
        }
        #expect(calls.count == 0)
        #expect(await store.runs().count == 1)
    }

    @Test("Устаревание точки судится по времени получения, которое ставит источник, а не по времени обработки")
    func staleByReceiptTime() async throws {
        let tracker = RunTracker()
        try await tracker.start(session())
        var walk = Walk()
        let fresh = walk.fix(east: 0, north: 0)
        let cached = walk.fix(east: 1.4, north: 0)

        tracker.send(.fix(fresh, receivedAt: fresh.timestamp + 1))
        tracker.send(.fix(cached, receivedAt: cached.timestamp + 30))  // CoreLocation отдал запомненную
        await tracker.flush()

        let state = await tracker.state
        #expect(state.stats.points == 1)
        #expect(state.lastEvent == .ignored(.staleFix))
    }

    @Test("Перезапуск: точки, пришедшие до подключения забега из базы, уходят в него; номера продолжаются")
    func resumeTakesBufferedPoints() async throws {
        // Забег шёл: пять точек записаны кусками по пять — и приложение выгрузили.
        let before = try await session(policy: Fixture.policy(maxPoints: 5))()
        var walk = Walk()
        for fix in walk.straight(seconds: 5) {
            _ = try await before.handle(fix, now: fix.timestamp + 1)
        }

        // Источники запускаются сразу, забег подключается позже.
        let tracker = RunTracker()
        send(tracker, walk.straight(seconds: 3, from: 7))
        let resumed = try #require(
            try await RunTracker.recover(
                store: store, deviceId: Fixture.device, signedIn: Fixture.owner, now: walk.time,
                rules: { _ in .version1 }))
        try await tracker.resume(resumed)
        try await tracker.finish(at: walk.time)

        #expect(try await seqs(resumed.runId) == Array(0..<8))
        #expect(await store.runs().allSatisfy(\.isFinishedLocally))
    }

    @Test("Продолжать нечего — накопленное выбрасывается, забег не создаётся")
    func flushWithoutRunDropsInput() async throws {
        let tracker = RunTracker()
        var walk = Walk()
        send(tracker, walk.straight(seconds: 3))
        await tracker.flush()

        #expect(await store.runs().isEmpty)
        #expect(await !tracker.state.isRunning)
    }

    private func insertRun(
        startedAt: Double, lastPoint: Double, _ change: (inout LocalRun) -> Void = { _ in }
    ) async -> UUID {
        var run = Fixture.run(startedAt: startedAt)
        change(&run)
        await store.insert(run)
        _ = await store.updateRun(run.id) { $0.lastPointMs = StoragePrecision.milliseconds(lastPoint) }
        return run.id
    }

    private func openRuns() async -> Set<UUID> {
        Set(await store.runs().filter { !$0.isFinishedLocally }.map(\.id))
    }

    @Test("Продолжается свой свежий забег этого устройства с известными правилами; более новые негодные — закрываются")
    func recoverPicksOnlyAResumableRun() async throws {
        let now = Fixture.start + 3_600
        // Годный — самый старый: если бы какое-то условие не проверялось, выбрался бы более новый негодный.
        let resumable = await insertRun(startedAt: now - 1_800, lastPoint: now - 60)
        let otherDevice = await insertRun(startedAt: now - 1_500, lastPoint: now - 30) { $0.deviceId = UUID() }
        let otherPlayer = await insertRun(startedAt: now - 1_400, lastPoint: now - 30) { $0.ownerId = "player-2" }
        let stale = await insertRun(startedAt: now - 1_300, lastPoint: now - 11 * 60)
        let unknownRules = await insertRun(startedAt: now - 1_200, lastPoint: now - 30) { $0.configVersion = 9 }

        let session = try await RunTracker.recover(
            store: store, deviceId: Fixture.device, signedIn: Fixture.owner, now: now,
            rules: { $0 == 1 ? .version1 : nil })

        #expect(session?.runId == resumable)
        #expect(await openRuns() == [resumable])
        #expect(![otherDevice, otherPlayer, stale, unknownRules].contains(session?.runId))
    }

    @Test("Предел длины вышел — забег не продолжается, а закрывается")
    func recoverSkipsRunPastLimit() async throws {
        let now = Fixture.start + 5 * 3_600
        let pastLimit = await insertRun(startedAt: now - 4 * 3_600 - 60, lastPoint: now - 30)

        let session = try await RunTracker.recover(
            store: store, deviceId: Fixture.device, signedIn: Fixture.owner, now: now, rules: { _ in .version1 })

        #expect(session == nil)
        #expect(await !openRuns().contains(pastLimit))
    }

    @Test("Никто не вошёл (вход истёк) — забег этого устройства всё равно продолжается: запись от входа не зависит")
    func recoverWithoutSignIn() async throws {
        let now = Fixture.start + 3_600
        let run = await insertRun(startedAt: now - 600, lastPoint: now - 20)

        let session = try await RunTracker.recover(
            store: store, deviceId: Fixture.device, signedIn: nil, now: now, rules: { _ in .version1 })

        #expect(session?.runId == run)
    }

    @Test("«Финиш» сразу после точек, без ожидания, — все точки в забеге: конец идёт той же очередью")
    func finishAfterQueuedPoints() async throws {
        let tracker = RunTracker()
        try await tracker.start(session())
        var walk = Walk()
        let fixes = walk.straight(seconds: 30)
        send(tracker, fixes)
        try await tracker.finish(at: walk.time)

        let run = try #require(await store.runs().first)
        #expect(run.lastSeq == fixes.count - 1)
        #expect(try await seqs(run.id) == Array(0..<fixes.count))
    }

    @Test(
        "Таймер: отметка датчиков отстаёт на 10 с, кусок старше минуты запечатан (зовут синхронизацию), предел — конец")
    func tickSealsAndEndsAtLimit() async throws {
        let queued = Counter()
        let tracker = RunTracker(onQueued: { queued.bump() })
        try await tracker.start(session(policy: Fixture.policy(maxPoints: 120, maxAgeSeconds: 60)))
        var walk = Walk()
        send(tracker, walk.straight(seconds: 3))
        await tracker.flush()
        let before = queued.count

        tracker.send(.tick(now: Fixture.start + 90))
        await tracker.flush()
        let runId = try #require(await tracker.state.runId)
        let chunks = await store.chunks(of: runId)
        #expect(chunks.count == 1 && queued.count == before + 1)
        #expect(chunks.first?.sensorsCompleteThroughMs == StoragePrecision.milliseconds(Fixture.start + 80))

        tracker.send(.tick(now: Fixture.start + PhoneRules.version1.maxRunHours * 3_600 + 11 * 60))
        await tracker.flush()
        let state = await tracker.state
        #expect(!state.isRunning && state.endedAtLimit)
        #expect(try #require(await store.runs().first).isFinishedLocally)
    }
}
