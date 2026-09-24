import Foundation
import GameCore
import Testing

@testable import Sync

@Suite("Забег на телефоне: точка → судья → очередь, петля → заявка")
struct RunSessionTests {
    /// Прогулка по плоскости «восток — север» вокруг точки в Бресте: точка раз в секунду.
    struct Walk {
        let plane = LocalTangentPlane(origin: Coordinate(latitude: 52.0976, longitude: 23.6880))
        var time = Fixture.start + 1

        mutating func fix(east: Double, north: Double, accuracy: Double = 5) -> LocationFix {
            defer { time += 1 }
            return LocationFix(
                coordinate: plane.unproject(PlanarPoint(east: east, north: north)), timestamp: time,
                horizontalAccuracy: accuracy)
        }

        /// Обход квадрата со стороной `side` метров шагом 1,4 м и возврат в начало.
        mutating func square(side: Double, from origin: (Double, Double) = (0, 0)) -> [LocationFix] {
            let corners = [(0.0, 0.0), (side, 0), (side, side), (0, side), (0, 0)]
            var fixes: [LocationFix] = []
            for (a, b) in zip(corners, corners.dropFirst()) {
                let steps = Int((side / 1.4).rounded(.up))
                for i in 0..<steps {
                    let t = Double(i) / Double(steps)
                    fixes.append(
                        fix(east: origin.0 + a.0 + (b.0 - a.0) * t, north: origin.1 + a.1 + (b.1 - a.1) * t))
                }
            }
            fixes.append(fix(east: origin.0, north: origin.1))
            return fixes
        }

        /// Прямая на восток шагом 1,4 м.
        mutating func straight(seconds: Int, from start: Double = 0) -> [LocationFix] {
            (0..<seconds).map { fix(east: start + 1.4 * Double($0), north: 0) }
        }
    }

    private let store = InMemorySyncStore()

    private func start(policy: ChunkPolicy = Fixture.policy(maxPoints: 120)) async throws -> RunSession {
        try await RunSession.start(Fixture.run(), store: store, policy: policy)
    }

    private func feed(_ session: RunSession, _ fixes: [LocationFix]) async throws -> [RunEvent] {
        var events: [RunEvent] = []
        for fix in fixes {
            events += try await session.handle(fix, now: fix.timestamp + 1)
        }
        return events
    }

    @Test("Все точки уходят в очередь подряд с нуля; принятые — в дистанцию и туман")
    func recordsEveryPoint() async throws {
        let session = try await start()
        var walk = Walk()

        let events = try await feed(session, walk.straight(seconds: 60))
        try await session.finish(endedAt: walk.time)

        #expect(events.allSatisfy { $0 == .accepted })
        let chunks = await store.chunks(of: session.runId)
        #expect(chunks.flatMap(\.points).map(\.seq) == Array(0..<60))
        let stats = await session.stats
        #expect(stats.points == 60 && stats.acceptedPoints == 60)
        #expect(abs(stats.distanceMeters - 1.4 * 59) < 1)
        #expect(stats.fogCells > 0)
        let run = try #require(try await store.runs().first)
        #expect(run.lastSeq == 59 && run.isFinishedLocally)
    }

    @Test("Обошёл квадрат — петля заявлена сразу: заявка в очереди, кусок с концом петли запечатан")
    func loopIsClaimed() async throws {
        let session = try await start()
        var walk = Walk()

        let events = try await feed(session, walk.square(side: 80))

        let claimed = events.compactMap { event -> (Int, LoopClaim)? in
            if case .loopClaimed(let number, let loop) = event { return (number, loop) }
            return nil
        }
        #expect(claimed.count == 1)
        let (claimNo, loop) = try #require(claimed.first)
        #expect(claimNo == 0)
        #expect(loop.estimatedArea > 5_000)
        let claims = await store.claims(of: session.runId)
        #expect(claims.map(\.loop) == [loop])
        let sealed = await store.chunks(of: session.runId)
        #expect(sealed.last?.lastSeq ?? -1 >= loop.endSeq)
        #expect(await session.stats.loops == 1)
    }

    @Test("Точка с тем же временем (повтор GPS) отброшена, номер не израсходован")
    func duplicateTimeIsDropped() async throws {
        let session = try await start()
        var walk = Walk()
        let first = walk.fix(east: 0, north: 0)
        var repeated = first
        repeated.coordinate = walk.plane.unproject(PlanarPoint(east: 1, north: 0))
        let next = walk.fix(east: 1.4, north: 0)

        #expect(try await session.handle(first, now: first.timestamp) == [.accepted])
        #expect(try await session.handle(repeated, now: first.timestamp) == [.dropped])
        #expect(try await session.handle(next, now: next.timestamp) == [.accepted])
        try await session.finish(endedAt: next.timestamp)

        let points = await store.chunks(of: session.runId).flatMap(\.points)
        #expect(points.map(\.seq) == [0, 1])
        #expect(points.map(\.timestamp) == [first.timestamp, next.timestamp])
    }

    @Test("Плохая точность — точка записана для сервера, но в след и дистанцию не идёт")
    func poorAccuracyIsRecordedButIgnored() async throws {
        let session = try await start()
        var walk = Walk()
        var fixes = walk.straight(seconds: 10)
        fixes[5].horizontalAccuracy = 200

        let events = try await feed(session, fixes)
        try await session.finish(endedAt: walk.time)

        #expect(events[5] == .ignored(.poorAccuracy))
        #expect(try await store.chunks(of: session.runId).flatMap(\.points).count == 10)
        let stats = await session.stats
        #expect(stats.acceptedPoints == 9 && stats.lastIssue == .poorAccuracy)
    }

    @Test("Судья видит точку уже округлённой, как сервер: точность 25,04 м хранится как 25,0 — точка принята")
    func judgeSeesTheStoredPoint() async throws {
        let session = try await start()
        var walk = Walk()
        var fixes = walk.straight(seconds: 3)
        fixes[1].horizontalAccuracy = 25.04  // порог судьи — 25 м; без округления точка была бы отброшена

        let events = try await feed(session, fixes)

        #expect(events == [.accepted, .accepted, .accepted])
    }

    @Test("След порван посреди квадрата — петля через разрыв не заявляется")
    func brokenTrackDoesNotClose() async throws {
        let session = try await start()
        var walk = Walk()
        var fixes = walk.square(side: 80)
        // Телепорт на половине пути: на 2 км в сторону и обратно — две точки, которых не бывает у человека.
        let middle = fixes.count / 2
        fixes[middle].coordinate = walk.plane.unproject(PlanarPoint(east: 2_000, north: 2_000))

        let events = try await feed(session, fixes)

        #expect(events.contains { if case .broken = $0 { true } else { false } })
        #expect(!events.contains { if case .loopClaimed = $0 { true } else { false } })
        #expect(try await store.claims(of: session.runId).isEmpty)
    }

    @Test("Предел длины забега — забег завершается сам, дальше точки не принимаются")
    func maxRunLengthFinishesTheRun() async throws {
        var policy = Fixture.policy(maxPoints: 120)
        policy.maxRunHours = 0.01  // 36 с + 10 мин запаса сервера
        let session = try await start(policy: policy)
        var walk = Walk()
        _ = try await feed(session, walk.straight(seconds: 20))
        walk.time += 700  // за пределом окна

        let late = walk.fix(east: 30, north: 0)
        #expect(try await session.handle(late, now: late.timestamp) == [.finishedAtLimit])
        #expect(await session.isFinished)
        #expect(try await session.handle(walk.fix(east: 31, north: 0), now: walk.time) == [])
        let run = try #require(try await store.runs().first)
        #expect(run.isFinishedLocally && run.lastSeq == 19)
    }

    @Test("Перезапуск приложения: забег продолжается с номера после записанных, время — только вперёд")
    func resumeContinuesNumbering() async throws {
        let session = try await start(policy: Fixture.policy(maxPoints: 10))
        var walk = Walk()
        _ = try await feed(session, walk.straight(seconds: 25))  // запечатаны 0…19, хвост 20…24 потерян

        let resumed = try #require(
            try await RunSession.resume(runId: session.runId, store: store, policy: Fixture.policy(maxPoints: 10)))
        let stale = LocationFix(
            coordinate: walk.plane.unproject(PlanarPoint(east: 0, north: 0)), timestamp: Fixture.start + 5,
            horizontalAccuracy: 5)
        #expect(try await resumed.handle(stale, now: walk.time) == [.dropped])
        let next = walk.fix(east: 40, north: 0)
        #expect(try await resumed.handle(next, now: next.timestamp) == [.accepted])
        try await resumed.finish(endedAt: next.timestamp)

        let seqs = await store.chunks(of: session.runId).flatMap(\.points).map(\.seq)
        #expect(seqs == Array(0..<20) + [20])
    }

    @Test("Завершённый или отвергнутый забег не продолжается")
    func finishedRunIsNotResumed() async throws {
        let session = try await start()
        var walk = Walk()
        _ = try await feed(session, walk.straight(seconds: 5))
        try await session.finish(endedAt: walk.time)

        #expect(try await RunSession.resume(runId: session.runId, store: store) == nil)
        #expect(try await RunSession.resume(runId: UUID(), store: store) == nil)
    }

    @Test("Туман открывается с запаздыванием 30 с; в конце забега — весь хвост")
    func fogLagsThirtySeconds() async throws {
        let session = try await start()
        var walk = Walk()

        _ = try await feed(session, walk.straight(seconds: 25))
        #expect(await session.stats.fogCells == 0)
        _ = try await feed(session, walk.straight(seconds: 20, from: 35))
        let midway = await session.stats.fogCells
        #expect(midway > 0)
        try await session.finish(endedAt: walk.time)
        #expect(await session.stats.fogCells > midway)
    }

    @Test("Машина: путь за 30 с до разрыва туман не открывает — как на сервере; телепорт путь до скачка не отменяет")
    func fogSkipsTheLastThirtySecondsBeforeABreak() async throws {
        // 100 с пешком на восток, потом быстрее 25 км/ч: судья рвёт след. Путь за 30 с до разрыва — не в тумане.
        let session = try await start()
        var walk = Walk()
        var fixes = walk.straight(seconds: 100)
        fixes += (1...60).map { walk.fix(east: 1.4 * 99 + 15 * Double($0), north: 0) }
        let events = try await feed(session, fixes)
        try await session.finish(endedAt: walk.time)

        let breakIndex = try #require(events.firstIndex(of: .broken(.tooFast)))
        let breakTime = fixes[breakIndex].timestamp
        let fog = await session.fog
        func opened(_ fix: LocationFix) -> Bool { fog.isRevealed(FogGrid.cell(of: fix.coordinate)) }
        // Старше 30 с до разрыва и дальше 25 м от последней засчитанной точки — открыто; позже — нет.
        let counted = fixes[..<breakIndex].filter { breakTime - $0.timestamp > 30 }
        let skipped = fixes[..<breakIndex].filter { breakTime - $0.timestamp <= 30 }
        let lastCounted = try #require(counted.last)
        let plane = walk.plane
        #expect(counted.allSatisfy(opened))
        #expect(
            skipped.filter {
                plane.project($0.coordinate).east - plane.project(lastCounted.coordinate).east > 30
                    && plane.project(fixes[breakIndex].coordinate).east - plane.project($0.coordinate).east > 30
            }
            .allSatisfy { !opened($0) })
        #expect(!opened(fixes[breakIndex]))

        // Телепорт: путь до скачка честный — открыт весь, сам скачок — нет.
        let jumped = try await RunSession.start(Fixture.run(), store: InMemorySyncStore())
        var jumpWalk = Walk()
        var jumpFixes = jumpWalk.straight(seconds: 100)
        let jump = jumpWalk.fix(east: 50_000, north: 0)
        jumpFixes.append(jump)
        #expect(try await feed(jumped, jumpFixes).contains(.broken(.teleport)))
        let jumpedFog = await jumped.fog
        #expect(jumpFixes.dropLast().allSatisfy { jumpedFog.isRevealed(FogGrid.cell(of: $0.coordinate)) })
        #expect(!jumpedFog.isRevealed(FogGrid.cell(of: jump.coordinate)))
    }

    @Test("Датчики уходят и судье, и в очередь — в точности хранения")
    func sensorsGoToJudgeAndQueue() async throws {
        let session = try await start()
        var walk = Walk()
        await session.record(MotionSample(timestamp: Fixture.start + 0.5, activity: .walking))
        await session.record(PedometerSample(start: Fixture.start + 1, end: Fixture.start + 6.0004, steps: 9))
        await session.sensorsComplete(through: Fixture.start + 6)
        _ = try await feed(session, walk.straight(seconds: 5))
        try await session.finish(endedAt: walk.time)

        let chunk = try #require(try await store.chunks(of: session.runId).first)
        #expect(chunk.motion.map(\.activity) == [.walking])
        #expect(chunk.steps.map(\.steps) == [9])
        #expect(chunk.steps.first?.end == StoragePrecision.time(Fixture.start + 6.0004))
    }
}
