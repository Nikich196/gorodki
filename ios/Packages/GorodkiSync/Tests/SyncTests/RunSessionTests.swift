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

    private func start(
        policy: ChunkPolicy = Fixture.policy(maxPoints: 120), rules: PhoneRules = .version1
    ) async throws -> RunSession {
        try await RunSession.start(Fixture.run(), store: store, rules: rules, policy: policy)
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

    @Test("Судья и детектор — из правил версии конфига, а не из чисел сборки")
    func rulesComeFromConfigVersion() async throws {
        var strict = PhoneRules.version1
        strict.run.maxAccuracyMeters = 10  // точка с точностью 15 м — отброшена
        strict.loopDetector.minPathMeters = 1_000  // квадрат 80 м (320 м пути) — не петля
        let session = try await start(rules: strict)
        var walk = Walk()
        var fixes = walk.square(side: 80)
        fixes[3].horizontalAccuracy = 15

        let events = try await feed(session, fixes)

        #expect(events[3] == .ignored(.poorAccuracy))
        #expect(!events.contains { if case .loopClaimed = $0 { true } else { false } })
    }

    @Test("Новичок: порог точности 35 м вместо 25 — как сервер; после перезапуска — тот же порог")
    func newcomerAccuracy() async throws {
        let session = try await RunSession.start(
            Fixture.run(), store: store, rules: .version1, newcomer: true, policy: Fixture.policy(maxPoints: 5))
        var walk = Walk()
        var fixes = walk.straight(seconds: 10)
        fixes[2].horizontalAccuracy = 30
        let events = try await feed(session, Array(fixes.prefix(6)))
        #expect(events[2] == .accepted)

        let resumed = try #require(
            try await RunSession.resume(
                runId: session.runId, store: store, rules: .version1, policy: Fixture.policy(maxPoints: 5)))
        var late = fixes[7]
        late.horizontalAccuracy = 30
        #expect(try await resumed.handle(late, now: late.timestamp) == [.accepted])

        let veteran = try await RunSession.start(Fixture.run(), store: InMemorySyncStore(), rules: .version1)
        var veteranWalk = Walk()
        var veteranFixes = veteranWalk.straight(seconds: 3)
        veteranFixes[1].horizontalAccuracy = 30
        #expect(try await feed(veteran, veteranFixes)[1] == .ignored(.poorAccuracy))
    }

    @Test("Новичок — пока в очереди нет ни одной засчитанной заявки этого игрока")
    func newcomerFromQueue() async throws {
        #expect(try await RunSession.isNewcomer(ownerId: Fixture.owner, store: store))
        try await Fixture.record(Fixture.run(), points: 25, into: store, claims: [Fixture.loop(2, 14)])
        #expect(try await RunSession.isNewcomer(ownerId: Fixture.owner, store: store))

        let runId = try #require(await store.runs().first?.id)
        var claim = try #require(await store.claims(of: runId).first)
        claim.outcome = ClaimOutcome(status: "applied", waitingFor: nil, rejectCode: nil, areaSquareMeters: 5_000)
        await store.save(claim)

        #expect(try await !RunSession.isNewcomer(ownerId: Fixture.owner, store: store))
        #expect(try await RunSession.isNewcomer(ownerId: "someone-else", store: store))
    }

    @Test("Устаревшая точка (старше 10 с по часам телефона) не нумеруется и не уходит на сервер")
    func staleFixIsNotNumbered() async throws {
        let session = try await start()
        var walk = Walk()
        let cached = walk.fix(east: 0, north: 0)  // запомненная CoreLocation точка
        let fresh = walk.fix(east: 1.4, north: 0)

        #expect(try await session.handle(cached, now: cached.timestamp + 30) == [.ignored(.staleFix)])
        #expect(try await session.handle(fresh, now: fresh.timestamp) == [.accepted])
        try await session.finish(endedAt: fresh.timestamp)

        let points = await store.chunks(of: session.runId).flatMap(\.points)
        #expect(points.map(\.seq) == [0])
        #expect(points.map(\.timestamp) == [fresh.timestamp])
        #expect(await session.isFinished)
    }

    @Test(
        "Неверная точка (точность −1, координата вне диапазона) не нумеруется: иначе «идеальная» точка или отказ куска",
        arguments: [(-1.0, 52.1), (5, 95), (5, .nan), (.infinity, 52.1)])
    func invalidFixIsNotNumbered(accuracy: Double, latitude: Double) async throws {
        let session = try await start()
        var walk = Walk()
        let bad = LocationFix(
            coordinate: Coordinate(latitude: latitude, longitude: 23.7), timestamp: walk.time,
            horizontalAccuracy: accuracy)
        walk.time += 1
        let good = walk.fix(east: 0, north: 0)

        #expect(try await session.handle(bad, now: bad.timestamp) == [.dropped])
        #expect(try await session.handle(good, now: good.timestamp) == [.accepted])
        try await session.finish(endedAt: good.timestamp)

        #expect(await store.chunks(of: session.runId).flatMap(\.points).map(\.timestamp) == [good.timestamp])
    }

    @Test("«Финиш», пока точка записывается, — ждёт её: точка в куске, номер последней точки сходится с кусками")
    func finishWaitsForPointBeingRecorded() async throws {
        let slow = GatedStore()
        let session = try await RunSession.start(
            Fixture.run(), store: slow, rules: .version1, policy: Fixture.policy(maxPoints: 1, maxAgeSeconds: 3_600))
        var walk = Walk()
        let a = walk.fix(east: 0, north: 0)
        await slow.holdChunkSaves()

        let point = Task { try await session.handle(a, now: a.timestamp) }
        try await slow.waitUntilSaving()
        let finishing = Task { try await session.finish(endedAt: a.timestamp + 1) }
        try await Task.sleep(for: .milliseconds(200))
        #expect(await !session.isFinished)  // «Финиш» ждёт, пока точка запишется
        await slow.release()

        #expect(try await point.value == [.accepted])
        try await finishing.value
        let run = try #require(await slow.runs().first)
        let seqs = await slow.inner.chunks(of: session.runId).flatMap(\.points).map(\.seq)
        #expect(seqs == [0] && run.lastSeq == 0)
        #expect(await session.sealedChunks == 1)
    }

    @Test("Запоздавшая запись датчика (раньше уже отправленной отметки) не уходит на сервер — и судье телефона тоже")
    func lateSensorIsNotJudged() async throws {
        let session = try await start(policy: Fixture.policy(maxPoints: 5, maxAgeSeconds: 3_600))
        var walk = Walk()
        let first = walk.straight(seconds: 5)
        _ = try await feed(session, Array(first.prefix(3)))
        await session.sensorsComplete(through: first[2].timestamp)
        _ = try await feed(session, Array(first.suffix(2)))  // пятая точка запечатала кусок с отметкой

        // «Транспорт» задним числом — сервер его не получит (кусок с отметкой уже ушёл), значит и судье нельзя.
        await session.record(MotionSample(timestamp: first[1].timestamp, activity: .automotive))
        let events = try await feed(session, walk.straight(seconds: 40, from: 7))

        #expect(!events.contains(.broken(.vehicle)))
        #expect(await store.chunks(of: session.runId).flatMap(\.motion).isEmpty)
    }

    @Test("Предел длины при молчащем GPS: таймер завершает забег, конец — последняя точка")
    func tickFinishesAtLimitWithoutPoints() async throws {
        let session = try await start()
        var walk = Walk()
        let fixes = walk.straight(seconds: 3)
        _ = try await feed(session, fixes)
        let limit = Fixture.start + PhoneRules.version1.maxRunHours * 3_600

        #expect(try await session.tick(now: limit + 60).isEmpty)  // в пределах запаса окна
        #expect(try await session.tick(now: limit + 11 * 60) == [.finishedAtLimit])
        #expect(await session.isFinished)
        let run = try #require(await store.runs().first)
        #expect(run.endedAtMs == StoragePrecision.milliseconds(fixes[2].timestamp) && run.lastSeq == 2)
    }

    @Test("Точка раньше начала забега больше чем на минуту — не этого забега: отброшена, забег продолжается")
    func pointBeforeStartIsDropped() async throws {
        let session = try await start()
        let early = LocationFix(
            coordinate: Walk().plane.unproject(PlanarPoint(east: 0, north: 0)), timestamp: Fixture.start - 120,
            horizontalAccuracy: 5)

        #expect(try await session.handle(early, now: early.timestamp + 1) == [.dropped])
        #expect(await !session.isFinished)
        var walk = Walk()
        let next = walk.fix(east: 0, north: 0)
        #expect(try await session.handle(next, now: next.timestamp) == [.accepted])
    }

    @Test("Точка, пришедшая, пока предыдущая записывается, ждёт её конца; обе записаны подряд")
    func concurrentFixesAreSerialized() async throws {
        let slow = GatedStore()
        let session = try await RunSession.start(
            Fixture.run(), store: slow, rules: .version1, policy: Fixture.policy(maxPoints: 1, maxAgeSeconds: 3_600))
        var walk = Walk()
        let a = walk.fix(east: 0, north: 0)
        let b = walk.fix(east: 1.4, north: 0)
        await slow.holdChunkSaves()

        let first = Task { try await session.handle(a, now: a.timestamp) }  // кусок из одной точки записывается
        try await slow.waitUntilSaving()
        let second = Task { try await session.handle(b, now: b.timestamp) }
        try await Task.sleep(for: .milliseconds(200))
        #expect(await slow.saveAttempts == 1)  // вторая точка не обгоняет первую
        await slow.release()

        #expect(try await first.value == [.accepted])
        #expect(try await second.value == [.accepted])
        #expect(await slow.inner.chunks(of: session.runId).flatMap(\.points).map(\.seq) == [0, 1])
    }

    @Test("Точка и датчик во время записи куска не теряются: точка ждёт записи, датчик — в следующий кусок")
    func pointDuringSealIsKept() async throws {
        let slow = GatedStore()
        let session = try await RunSession.start(
            Fixture.run(), store: slow, rules: .version1, policy: Fixture.policy(maxPoints: 120, maxAgeSeconds: 3_600))
        var walk = Walk()
        _ = try await feed(session, walk.straight(seconds: 5))
        await slow.holdChunkSaves()

        let sealing = Task { try await session.tick(now: Fixture.start + 7_200) }  // кусок «созрел»
        try await slow.waitUntilSaving()
        let during = walk.fix(east: 10, north: 0)
        let point = Task { try await session.handle(during, now: during.timestamp) }
        await session.record(MotionSample(timestamp: during.timestamp, activity: .walking))  // датчики очереди не ждут
        await slow.release()
        try await sealing.value
        #expect(try await point.value == [.accepted])
        try await session.finish(endedAt: walk.time)

        let chunks = await slow.inner.chunks(of: session.runId)
        #expect(chunks.flatMap(\.points).map(\.seq) == Array(0..<6))
        #expect(chunks.flatMap(\.motion).map(\.activity) == [.walking])
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
        var rules = PhoneRules.version1
        rules.maxRunHours = 0.01  // 36 с + 10 мин запаса сервера — из правил версии конфига
        let session = try await start(rules: rules)
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
            try await RunSession.resume(
                runId: session.runId, store: store, rules: .version1, policy: Fixture.policy(maxPoints: 10)))
        let stale = LocationFix(
            coordinate: walk.plane.unproject(PlanarPoint(east: 0, north: 0)), timestamp: Fixture.start + 5,
            horizontalAccuracy: 5)
        #expect(try await resumed.handle(stale, now: stale.timestamp + 1) == [.dropped])
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

        #expect(try await RunSession.resume(runId: session.runId, store: store, rules: .version1) == nil)
        #expect(try await RunSession.resume(runId: UUID(), store: store, rules: .version1) == nil)
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
        let jumped = try await RunSession.start(Fixture.run(), store: InMemorySyncStore(), rules: .version1)
        var jumpWalk = Walk()
        var jumpFixes = jumpWalk.straight(seconds: 100)
        let jump = jumpWalk.fix(east: 50_000, north: 0)
        jumpFixes.append(jump)
        #expect(try await feed(jumped, jumpFixes).contains(.broken(.teleport)))
        try await jumped.finish(endedAt: jumpWalk.time)  // до скачка — ждёт своих 30 с; конец забега засчитывает всё
        let jumpedFog = await jumped.fog
        #expect(jumpFixes.dropLast().allSatisfy { jumpedFog.isRevealed(FogGrid.cell(of: $0.coordinate)) })
        #expect(!jumpedFog.isRevealed(FogGrid.cell(of: jump.coordinate)))
    }

    @Test("Телепорт, а следом машина: путь до телепорта в 30-секундном окне машины тоже не открывает туман")
    func teleportThenVehicleBreak() async throws {
        // Бег 3 м/с на восток 100 с, скачок на 5 км, оттуда — «транспорт» по датчикам: судья рвёт след через 20 с.
        let session = try await start()
        var walk = Walk()
        var fixes = (0..<100).map { walk.fix(east: 3 * Double($0), north: 0) }
        let jump = walk.fix(east: 5_000, north: 5_000)
        fixes.append(jump)
        var events = try await feed(session, fixes)
        await session.record(MotionSample(timestamp: jump.timestamp, activity: .automotive))
        let driving = (1...40).map { walk.fix(east: 5_000 + 3 * Double($0), north: 5_000) }
        events += try await feed(session, driving)
        fixes += driving
        try await session.finish(endedAt: walk.time)

        #expect(events.contains(.broken(.teleport)))
        let vehicleBreak = try #require(events.firstIndex(of: .broken(.vehicle)))
        let breakTime = fixes[vehicleBreak].timestamp
        let fog = await session.fog
        let excludedBeforeTeleport = fixes[..<100].filter { breakTime - $0.timestamp <= 30 }
        #expect(!excludedBeforeTeleport.isEmpty)
        // Последние точки перед скачком — дальше 25 м от засчитанных: их клетки открылись бы только ими самими.
        #expect(!fog.isRevealed(FogGrid.cell(of: try #require(excludedBeforeTeleport.last).coordinate)))
        #expect(fog.isRevealed(FogGrid.cell(of: fixes[10].coordinate)))
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

/// Хранилище, которое по команде придерживает запись кусков — как медленный диск: пока кусок записывается, актор
/// записи принимает следующие точки.
actor GatedStore: SyncStore {
    let inner = InMemorySyncStore()
    private var holding = false
    private var saving = false
    private var gate: [CheckedContinuation<Void, Never>] = []
    private(set) var saveAttempts = 0

    func holdChunkSaves() { holding = true }

    func release() {
        holding = false
        gate.forEach { $0.resume() }
        gate = []
    }

    func waitUntilSaving() async throws {
        for _ in 0..<2_500 where !saving {
            try await Task.sleep(for: .milliseconds(2))
        }
    }

    func runs() async -> [LocalRun] { await inner.runs() }
    func insert(_ run: LocalRun) async { await inner.insert(run) }
    func updateRun(_ id: UUID, _ change: @Sendable (inout LocalRun) -> Void) async -> LocalRun? {
        await inner.updateRun(id, change)
    }
    func chunks(of runId: UUID) async -> [SealedChunk] { await inner.chunks(of: runId) }
    func save(_ chunk: SealedChunk) async {
        await holdIfAsked()
        await inner.save(chunk)
    }
    func seal(_ chunk: SealedChunk, progress: @Sendable (inout LocalRun) -> Void) async {
        await holdIfAsked()
        await inner.seal(chunk, progress: progress)
    }
    private func holdIfAsked() async {
        saveAttempts += 1
        if holding {
            saving = true
            await withCheckedContinuation { gate.append($0) }
        }
    }
    func replaceChunk(of runId: UUID, firstSeq: Int, with pieces: [SealedChunk]) async {
        await inner.replaceChunk(of: runId, firstSeq: firstSeq, with: pieces)
    }
    func deleteChunk(of runId: UUID, firstSeq: Int) async { await inner.deleteChunk(of: runId, firstSeq: firstSeq) }
    func claims(of runId: UUID) async -> [PendingClaim] { await inner.claims(of: runId) }
    func save(_ claim: PendingClaim) async { await inner.save(claim) }
}
