import Foundation
import GameCore
import Testing

@testable import Sync

@Suite("Запись забега: куски и заявки в очереди")
struct RunRecorderTests {
    private let start = Fixture.start
    private var startMs: Int64 { StoragePrecision.milliseconds(start) }

    @Test("Кусок запечатывается по числу точек, остаток — при завершении; прогресс записан в забеге")
    func sealsByCount() async throws {
        let store = InMemorySyncStore()
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store)

        let chunks = await store.chunks(of: run.id)
        #expect(chunks.map { $0.firstSeq...$0.lastSeq } == [0...9, 10...19, 20...24])
        let stored = try #require(await store.runs().first)
        #expect(stored.lastSeq == 24 && stored.endedAtMs == StoragePrecision.milliseconds(start + 25))
        #expect(stored.recordedThroughSeq == 24 && stored.lastPointMs == StoragePrecision.milliseconds(start + 24))
        #expect(stored.serverState == .unknown)
    }

    @Test("Кусок запечатывается по возрасту: при очередной точке и по таймеру")
    func sealsByAge() async throws {
        let store = InMemorySyncStore()
        let run = Fixture.run()
        let recorder = try await RunRecorder.begin(
            run, store: store, policy: Fixture.policy(maxPoints: 1_000, maxAgeSeconds: 60))
        for seq in 0...60 {
            try await recorder.record(Fixture.point(seq))
        }
        #expect(await store.chunks(of: run.id).map(\.lastSeq) == [60])  // 60 с от первой точки куска

        for seq in 61...70 {
            try await recorder.record(Fixture.point(seq))
        }
        try await recorder.tick(now: start + 100)
        #expect(await store.chunks(of: run.id).count == 1)  // первой точке хвоста 39 с — рано
        try await recorder.tick(now: start + 121)
        #expect(await store.chunks(of: run.id).map { $0.firstSeq...$0.lastSeq } == [0...60, 61...70])
    }

    @Test("Точки только подряд, после завершения записи нет")
    func pointsInOrder() async throws {
        let store = InMemorySyncStore()
        let recorder = try await RunRecorder.begin(Fixture.run(), store: store)
        try await recorder.record(Fixture.point(0))
        await #expect(throws: RecorderError.outOfOrder(expected: 1, got: 2)) {
            try await recorder.record(Fixture.point(2))
        }
        try await recorder.finish(endedAt: start + 5)
        await #expect(throws: RecorderError.alreadyFinished) {
            try await recorder.record(Fixture.point(1))
        }
    }

    @Test("Время точки строго растёт в миллисекундах: иначе номер не расходуется")
    func timeIncreases() async throws {
        let store = InMemorySyncStore()
        let recorder = try await RunRecorder.begin(Fixture.run(), store: store)
        try await recorder.record(Fixture.point(0))
        var same = Fixture.point(1)
        same.timestamp = start + 0.0004  // та же миллисекунда после округления
        await #expect(throws: RecorderError.timeNotIncreasing) {
            try await recorder.record(same)
        }
        #expect(await recorder.nextSeq == 1)
        try await recorder.record(Fixture.point(1))
        #expect(await recorder.nextSeq == 2)
    }

    @Test("Точка вне окна забега: раньше начала больше чем на минуту или после предела длины")
    func runWindow() async throws {
        let store = InMemorySyncStore()
        let recorder = try await RunRecorder.begin(Fixture.run(), store: store)
        var early = Fixture.point(0)
        early.timestamp = start - 61
        await #expect(throws: RecorderError.outsideRunWindow) {
            try await recorder.record(early)
        }
        early.timestamp = start - 59
        try await recorder.record(early)  // GPS «догоняет» после нажатия «Старт»

        var late = Fixture.point(1)
        late.timestamp = start + 4 * 3_600 + 11 * 60
        await #expect(throws: RecorderError.outsideRunWindow) {
            try await recorder.record(late)
        }
    }

    @Test("Точка хранится в точности сервера, источник координат — рядом")
    func quantizes() async throws {
        let store = InMemorySyncStore()
        let run = Fixture.run()
        let recorder = try await RunRecorder.begin(run, store: store)
        let raw = TrackPoint(
            seq: 0, coordinate: Coordinate(latitude: 52.123456789, longitude: 23.987654321), timestamp: start + 0.0126,
            horizontalAccuracy: 4.26, speed: -1)
        try await recorder.record(raw, source: .simulated)
        try await recorder.finish(endedAt: start + 1)

        let chunk = try #require(await store.chunks(of: run.id).first)
        let stored = try #require(chunk.points.first)
        #expect(stored == raw.quantizedForStorage())
        #expect(stored.coordinate.latitude == 52.1234568 && stored.speed == nil && stored.horizontalAccuracy == 4.3)
        #expect(chunk.sources == [.simulated])
    }

    @Test("Петля запечатывает кусок сразу; короткая, повторная и «вперёд» — отказ")
    func claimSealsChunk() async throws {
        let store = InMemorySyncStore()
        let run = Fixture.run()
        let recorder = try await RunRecorder.begin(run, store: store, policy: Fixture.policy(maxPoints: 100))
        for seq in 0...14 {
            try await recorder.record(Fixture.point(seq))
        }
        #expect(try await recorder.claim(Fixture.loop(2, 14)) == 0)
        #expect(await store.chunks(of: run.id).map { $0.firstSeq...$0.lastSeq } == [0...14])
        await #expect(throws: RecorderError.loopAlreadyClaimed) {
            try await recorder.claim(Fixture.loop(5, 14))  // сервер узнаёт заявку по концу петли
        }
        await #expect(throws: RecorderError.loopTooShort) {
            try await recorder.claim(Fixture.loop(12, 14))
        }
        await #expect(throws: RecorderError.loopBeyondRecorded) {
            try await recorder.claim(Fixture.loop(5, 15))
        }
        for seq in 15...20 {
            try await recorder.record(Fixture.point(seq))
        }
        #expect(try await recorder.claim(Fixture.loop(10, 20)) == 1)
        #expect(await store.claims(of: run.id).map(\.claimNo) == [0, 1])
    }

    @Test("Датчики только дописываются: запись не позже отметки запечатанного куска отбрасывается")
    func sensorsAppendOnly() async throws {
        let store = InMemorySyncStore()
        let run = Fixture.run()
        let recorder = try await RunRecorder.begin(run, store: store, policy: Fixture.policy(maxPoints: 10))
        await recorder.record(MotionSample(timestamp: start + 5, activity: .running))
        await recorder.sensorsComplete(through: start + 8)
        for seq in 0...9 {
            try await recorder.record(Fixture.point(seq))
        }
        await recorder.sensorsComplete(through: start + 3)  // отметка не уменьшается
        await recorder.record(MotionSample(timestamp: start + 8, activity: .walking))  // не позже отметки 8 с
        await recorder.record(PedometerSample(start: start, end: start + 7, steps: 12))
        await recorder.record(MotionSample(timestamp: start + 9, activity: .walking))
        for seq in 10...19 {
            try await recorder.record(Fixture.point(seq))
        }

        let chunks = await store.chunks(of: run.id)
        let mark = StoragePrecision.milliseconds(start + 8)
        #expect(chunks.map(\.sensorsCompleteThroughMs) == [mark, mark])
        #expect(chunks[0].motion.map(\.activity) == [.running])
        #expect(chunks[1].motion.map(\.timestamp) == [start + 9] && chunks[1].steps.isEmpty)
        #expect(await recorder.lateSensorRecords == 2)
        #expect(await recorder.acceptsSensorsAfterMs == mark)
    }

    @Test("Без данных о датчиках отметка — начало окна забега (минута до старта)")
    func sensorsMarkDefault() async throws {
        let store = InMemorySyncStore()
        let run = Fixture.run()
        try await Fixture.record(run, points: 3, into: store)
        #expect(await store.chunks(of: run.id).first?.sensorsCompleteThroughMs == run.startedAtMs - 60_000)
    }

    @Test("Вид движения, начавшийся до старта, уходит с первым куском от начала окна")
    func motionBeforeStart() async throws {
        let store = InMemorySyncStore()
        let run = Fixture.run()
        let recorder = try await RunRecorder.begin(run, store: store, policy: Fixture.policy(maxPoints: 5))
        await recorder.record(MotionSample(timestamp: start - 3_600, activity: .automotive))
        await recorder.record(MotionSample(timestamp: start - 120, activity: .walking))
        await recorder.record(MotionSample(timestamp: start + 2, activity: .running))
        for seq in 0...4 {
            try await recorder.record(Fixture.point(seq))
        }
        await recorder.record(MotionSample(timestamp: start - 90, activity: .cycling))  // до окна, но куска уже нет

        let first = try #require(await store.chunks(of: run.id).first)
        #expect(first.motion.map(\.activity) == [.walking, .running])
        #expect(first.motion.first.map { StoragePrecision.milliseconds($0.timestamp) } == startMs - 60_000)
        #expect(await recorder.lateSensorRecords == 1)
    }

    @Test("Шагомер вне окна или с неверным интервалом не записывается; сверх предела куска — тоже")
    func sensorLimits() async throws {
        let store = InMemorySyncStore()
        let run = Fixture.run()
        var policy = Fixture.policy(maxPoints: 100)
        policy.maxSamples = 5
        let recorder = try await RunRecorder.begin(run, store: store, policy: policy)
        await recorder.record(PedometerSample(start: start - 120, end: start + 10, steps: 30))  // начат до окна
        await recorder.record(PedometerSample(start: start + 10, end: start + 5, steps: 3))  // конец раньше начала
        for second in 1...7 {  // GPS пропал, а движение пишется
            await recorder.record(MotionSample(timestamp: start + Double(second), activity: .walking))
        }
        #expect(await recorder.droppedSensorRecords == 4)
        try await recorder.record(Fixture.point(0))  // предел датчиков достигнут — кусок запечатан сразу

        let chunk = try #require(await store.chunks(of: run.id).first)
        #expect(chunk.points.count == 1 && chunk.motion.count == 5 && chunk.steps.isEmpty)
    }

    @Test("Забег без точек: последняя точка −1, кусков нет")
    func emptyRun() async throws {
        let store = InMemorySyncStore()
        let run = Fixture.run()
        try await Fixture.record(run, points: 0, into: store)
        #expect(await store.chunks(of: run.id).isEmpty)
        #expect(try await store.runs().first?.lastSeq == -1)
    }

    @Test("После перезапуска приложения запись продолжается с номера после запечатанных кусков")
    func resumes() async throws {
        let store = InMemorySyncStore()
        let run = Fixture.run()
        try await Fixture.record(run, points: 25, into: store, claims: [Fixture.loop(3, 12)], finish: false)
        // Запечатаны 0…9, 10…12 (петля), 13…22; хвост 23…24 был только в памяти. Последний кусок синхронизация
        // уже выбросила (сервер его отверг) — номера всё равно не должны повториться.
        await store.deleteChunk(of: run.id, firstSeq: 13)

        let resumed = try #require(try await RunRecorder.resume(runId: run.id, store: store, policy: Fixture.policy()))
        #expect(await resumed.nextSeq == 23)
        await #expect(throws: RecorderError.loopAlreadyClaimed) {
            try await resumed.claim(Fixture.loop(4, 12))
        }
        #expect(try await resumed.claim(Fixture.loop(13, 22)) == 1)
        try await resumed.record(Fixture.point(23))
        try await resumed.finish(endedAt: start + 30)

        #expect(await store.chunks(of: run.id).map(\.lastSeq) == [9, 12, 23])
        #expect(try await RunRecorder.resume(runId: run.id, store: store) == nil)  // завершённый не продолжается
    }

    @Test("Отвергнутый сервером забег не продолжается")
    func rejectedNotResumed() async throws {
        let store = InMemorySyncStore()
        let run = Fixture.run()
        try await Fixture.record(run, points: 5, into: store, finish: false)
        await store.updateRun(run.id) { $0.serverState = .rejected }
        #expect(try await RunRecorder.resume(runId: run.id, store: store) == nil)
    }

    @Test("Новый забег закрывает прерванный: конец — последняя сохранённая точка")
    func closesInterrupted() async throws {
        let store = InMemorySyncStore()
        let old = Fixture.run(startedAt: start)
        let other = Fixture.run(owner: "player-2", startedAt: start + 10)
        try await Fixture.record(old, points: 15, into: store, finish: false)  // 10…14 не запечатаны
        try await Fixture.record(other, points: 3, into: store, finish: false)

        try await Fixture.record(Fixture.run(startedAt: start + 3_600), points: 3, into: store)

        let closed = try #require(await store.runs().first { $0.id == old.id })
        #expect(closed.lastSeq == 9 && closed.endedAtMs == StoragePrecision.milliseconds(start + 9))
        #expect(try await store.runs().first { $0.id == other.id }?.isFinishedLocally == false)  // чужой не тронут
    }
}
