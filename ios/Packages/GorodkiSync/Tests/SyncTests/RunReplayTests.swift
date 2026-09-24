import Foundation
import GameCore
import Synchronization
import Testing

@testable import Sync

@Suite("Демо-повтор: запись забега и проигрывание ×20 по виртуальному времени")
struct RunReplayTests {
    typealias Walk = RunSessionTests.Walk

    /// Сон повтора без настоящего ожидания: только считает, сколько «прождали».
    final class Sleeps: Sendable {
        private let total = Mutex(Duration.zero)
        private let calls = Mutex(0)
        private let failAfter: Int?

        init(failAfter: Int? = nil) { self.failAfter = failAfter }

        var slept: Duration { total.withLock { $0 } }

        func sleep(_ duration: Duration) throws {
            let call = calls.withLock { calls in
                calls += 1
                return calls
            }
            if let failAfter, call > failAfter { throw CancellationError() }
            total.withLock { $0 += duration }
        }
    }

    /// Записанная прогулка: квадрат со стороной 80 м (петля), вид движения «ходьба» и шаги каждые 10 с.
    private func record() async throws -> (RunRecording, points: Int) {
        let store = InMemorySyncStore()
        let tracker = RunTracker()
        try await tracker.start(recording: true) {
            try await RunSession.start(Fixture.run(), store: store, rules: .version1)
        }
        var walk = Walk()
        let square = walk.square(side: 80)
        tracker.send(.motion(MotionSample(timestamp: Fixture.start + 0.5, activity: .walking)))
        for (index, fix) in square.enumerated() {
            tracker.send(.fix(fix, receivedAt: fix.timestamp + 1))
            if index % 10 == 9 {
                tracker.send(.steps(PedometerSample(start: fix.timestamp - 10, end: fix.timestamp, steps: 13)))
            }
        }
        try await tracker.finish(at: walk.time + 1)
        #expect(await tracker.state.stats.loops == 1)
        return (try #require(await tracker.takeRecording()), square.count)
    }

    @Test("Запись: всё поступившее — со временем от старта, без абсолютной даты; переживает JSON; забирается один раз")
    func recordingKeepsInputsRelativeToStart() async throws {
        let store = InMemorySyncStore()
        let tracker = RunTracker()
        try await tracker.start(recording: true) {
            try await RunSession.start(Fixture.run(), store: store, rules: .version1)
        }
        var walk = Walk()
        let fix = walk.fix(east: 0, north: 0)
        tracker.send(.fix(fix, receivedAt: fix.timestamp + 2))
        tracker.send(.motion(MotionSample(timestamp: Fixture.start + 3, activity: .walking)))
        tracker.send(.tick(now: Fixture.start + 5))
        try await tracker.finish(at: Fixture.start + 60)

        let recording = try #require(await tracker.takeRecording())
        #expect(recording.duration == 60 && recording.league == .run && recording.hasMotion)
        #expect(recording.entries.map(\.at) == [fix.timestamp + 2 - Fixture.start, 3])  // таймер не пишется
        guard case .fix(_, _, let time, _, _, _) = recording.entries[0].input else {
            Issue.record("первой должна быть точка")
            return
        }
        #expect(time == fix.timestamp - Fixture.start)
        let json = try JSONEncoder().encode(recording)
        #expect(try JSONDecoder().decode(RunRecording.self, from: json) == recording)
        #expect(!String(decoding: json, as: UTF8.self).contains("1790000"))  // ни одного абсолютного времени
        #expect(await tracker.takeRecording() == nil)
    }

    @Test("Без записи — записи нет")
    func noRecordingByDefault() async throws {
        let tracker = RunTracker()
        try await tracker.start {
            try await RunSession.start(Fixture.run(), store: InMemorySyncStore(), rules: .version1)
        }
        try await tracker.finish(at: Fixture.start + 10)
        #expect(await tracker.takeRecording() == nil)
    }

    @Test(
        "Повтор: время сдвинуто в прошлое, точки не устаревают, датчики не запаздывают, петля та же; ×20 — в 20 раз быстрее"
    )
    func replayReproducesTheRun() async throws {
        let (recording, points) = try await record()
        let store = InMemorySyncStore()
        let now = Fixture.start + 86_400
        let replay = RunReplay(recording, speed: 20)
        let startedAt = replay.startedAt(now: now)
        var run = Fixture.run(startedAt: startedAt)
        run.source = .replay
        let tracker = RunTracker()
        try await tracker.start { [run] in try await RunSession.start(run, store: store, rules: .version1) }
        let sleeps = Sleeps()

        try await replay.play(into: tracker, startedAt: startedAt, sleep: { try sleeps.sleep($0) })

        let state = await tracker.state
        #expect(!state.isRunning && state.stats.points == points && state.stats.loops == 1)
        let stored = try #require(await store.runs().first)
        #expect(stored.source == .replay && stored.endedAtMs == StoragePrecision.milliseconds(now))
        let chunks = await store.chunks(of: stored.id)
        #expect(chunks.flatMap(\.points).count == points)
        #expect(chunks.flatMap(\.motion).count == 1 && chunks.flatMap(\.steps).count == points / 10)
        #expect(chunks.allSatisfy { $0.sensorsCompleteThroughMs <= StoragePrecision.milliseconds(now) })
        #expect(await store.claims(of: stored.id).count == 1)
        // Каждое ожидание округляется до миллисекунды: погрешность — не больше миллисекунды на ожидание.
        let expected = Duration.milliseconds(Int64((recording.duration / 20 * 1_000).rounded()))
        let waits = Double(recording.entries.count) + recording.duration / 5 + 2
        #expect(abs((sleeps.slept - expected) / .milliseconds(1)) <= waits)
    }

    @Test("Повтор остановлен — забег заканчивается на достигнутом времени записи")
    func cancelledReplayFinishesAtReachedTime() async throws {
        let (recording, _) = try await record()
        let store = InMemorySyncStore()
        let now = Fixture.start + 86_400
        let replay = RunReplay(recording, speed: 20)
        let startedAt = replay.startedAt(now: now)
        let tracker = RunTracker()
        try await tracker.start {
            try await RunSession.start(Fixture.run(startedAt: startedAt), store: store, rules: .version1)
        }

        await #expect(throws: CancellationError.self) {
            try await replay.play(into: tracker, startedAt: startedAt, sleep: { try Sleeps(failAfter: 0).sleep($0) })
        }

        let stored = try #require(await store.runs().first)
        #expect(stored.isFinishedLocally)
        #expect(try #require(stored.endedAtMs) < StoragePrecision.milliseconds(now))
    }
}
