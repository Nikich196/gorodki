import Foundation
import GameCore
import Testing

@testable import Sync

/// Пробный забег «Лаборатории»: хозяин `LocalRun.labOwnerId`, стирается отдельно от забегов игрока, сводка очереди
/// для экрана. Что синхронизация его не отправляет — `SyncEngineTests`, что продолжение не путает его с забегом
/// игрока — `RunTrackerTests`.
@Suite("Пробный забег «Лаборатории»: очередь на телефоне")
struct LabProbeTests {
    private let store = InMemorySyncStore()

    @Test("«Удалить пробные забеги» стирает только пробные — с кусками и заявками; забеги игрока остаются")
    func removeLabRunsKeepsPlayerRuns() async throws {
        let probe = Fixture.run(owner: LocalRun.labOwnerId)
        try await Fixture.record(probe, points: 25, into: store, claims: [Fixture.loop(2, 14)])
        let own = Fixture.run(startedAt: Fixture.start + 3_600)
        try await Fixture.record(own, points: 5, into: store, claims: [Fixture.loop(0, 4)])
        #expect(probe.isLabProbe && !own.isLabProbe)

        #expect(try await store.removeLabRuns() == 1)

        #expect(await store.runs().map(\.id) == [own.id])
        #expect(await store.chunks(of: probe.id).isEmpty)
        #expect(await store.claims(of: probe.id).isEmpty)
        #expect(await store.chunks(of: own.id).count == 1)
        #expect(await store.claims(of: own.id).count == 1)
        #expect(try await store.removeLabRuns() == 0)
    }

    @Test("Сводка очереди: куски, точки, заявки, разрывы GPS длиннее 15 с и отставание отметки датчиков")
    func queuedSummary() async throws {
        let run = Fixture.run()
        let recorder = try await RunRecorder.begin(run, store: store, policy: Fixture.policy(maxPoints: 4))
        // По секунде, потом разрыв 16 с (длиннее порога) и ровно 15 с (не длиннее).
        let offsets: [Double] = [0, 1, 2, 3, 4, 5, 21, 36, 37]
        for (seq, offset) in offsets.enumerated() {
            try await recorder.record(
                TrackPoint(
                    seq: seq, coordinate: Coordinate(latitude: 52.09 + Double(seq) * 9e-6, longitude: 23.68),
                    timestamp: Fixture.start + offset, horizontalAccuracy: 5))
        }
        await recorder.sensorsComplete(through: Fixture.start + 25)
        try await recorder.claim(Fixture.loop(0, 4))  // запечатывает остаток с отметкой датчиков
        try await recorder.finish(endedAt: Fixture.start + 37)

        let summary = try #require(try await QueuedRunSummary.of(run.id, in: store))

        #expect(summary.chunks == 3 && summary.points == offsets.count && summary.claims == 1)
        #expect(summary.gapsOverLimit == 1)
        #expect(summary.longestGapSeconds == 16)
        #expect(summary.recordedSeconds == 37)
        #expect(summary.sensorLagSeconds == 12)
        #expect(try await QueuedRunSummary.of(UUID(), in: store) == nil)
    }

    @Test("GPS молчит, а таймер идёт: отметка датчиков дальше последней точки — отставание 0, а не меньше")
    func sensorLagIsNeverNegative() async throws {
        let run = Fixture.run()
        let recorder = try await RunRecorder.begin(run, store: store)
        try await recorder.record(Fixture.point(0))
        await recorder.sensorsComplete(through: Fixture.start + 50)
        try await recorder.finish(endedAt: Fixture.start + 60)

        let summary = try #require(try await QueuedRunSummary.of(run.id, in: store))

        #expect(summary.sensorLagSeconds == 0)
    }

    @Test("Забег без записанных кусков: точек нет, отметки датчиков ещё нет")
    func queuedSummaryBeforeFirstChunk() async throws {
        let run = Fixture.run()
        _ = try await RunRecorder.begin(run, store: store)

        let summary = try #require(try await QueuedRunSummary.of(run.id, in: store))

        #expect(summary == QueuedRunSummary())
        #expect(summary.sensorLagSeconds == nil)
    }
}
