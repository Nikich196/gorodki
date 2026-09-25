import Foundation
import GameCore
import Synchronization
import Testing

@testable import Sync

@Suite("HUD забега: сигналы, плашки, метрики, туман, церемония, перезапуск (docs/architecture/run-hud.md)")
struct RunHUDTests {
    typealias Walk = RunSessionTests.Walk

    private let store = InMemorySyncStore()

    private static let place = Coordinate(latitude: 52.0976, longitude: 23.6880)

    private static func canClose(_ distance: Double, seq: Int = 0) -> ClosureHint {
        .canClose(
            ClosureTarget(
                seq: seq, coordinate: place, distanceMeters: distance, bearingDegrees: 0,
                estimatedAreaSquareMeters: 5_000, belowServerMinimum: false, tooLarge: false))
    }

    private func feed(_ session: RunSession, _ fixes: [LocationFix]) async throws -> [RunEvent] {
        var events: [RunEvent] = []
        for fix in fixes {
            events += try await session.handle(fix, now: fix.timestamp + 1)
        }
        return events
    }

    /// Ждать условия, уступая другим задачам (без сна): ответ поставщика тумана приходит из отдельной задачи.
    private func eventually(_ condition: () async -> Bool) async -> Bool {
        let deadline = ContinuousClock.now + .seconds(10)
        while ContinuousClock.now < deadline {
            if await condition() { return true }
            await Task.yield()
        }
        return false
    }

    // MARK: - Сигналы 50 и 15 м

    @Test("Подход к цели — по одному сигналу на порог; дрожание у порога в пределах гистерезиса — без повторов")
    func cuesOnApproach() {
        var cues = ClosureCues()
        let distances: [Double] = [80, 60, 51, 49, 52, 49, 58, 48, 40, 30, 16, 14, 18, 14, 24, 12, 5]

        let fired = distances.compactMap { cues.update(Self.canClose($0))?.thresholdMeters }

        #expect(fired == [50, 15])
    }

    @Test("Порог перевзводится, только когда расстояние выросло выше порога на гистерезис")
    func cueRearmsAfterHysteresis() {
        var cues = ClosureCues()
        #expect(cues.update(Self.canClose(45))?.thresholdMeters == 50)
        #expect(cues.update(Self.canClose(50 + RunHUD.closureCueHysteresisMeters))?.thresholdMeters == nil)
        #expect(cues.update(Self.canClose(49)) == nil)  // не ушёл дальше гистерезиса — не перевзведён
        #expect(cues.update(Self.canClose(50 + RunHUD.closureCueHysteresisMeters + 1)) == nil)
        #expect(cues.update(Self.canClose(49))?.thresholdMeters == 50)
    }

    @Test("Смена цели (заявка, разрыв) перевзводит оба порога; сразу внутри 15 м — один сигнал, ближний")
    func newTargetRearms() {
        var cues = ClosureCues()
        #expect(cues.update(Self.canClose(40, seq: 0))?.thresholdMeters == 50)
        #expect(cues.update(Self.canClose(10, seq: 0))?.thresholdMeters == 15)
        #expect(cues.update(.needsPath(targetSeq: 200, remainingMeters: 150)) == nil)
        #expect(cues.update(Self.canClose(10, seq: 200))?.thresholdMeters == 15)
        #expect(cues.update(Self.canClose(30, seq: 200)) == nil)  // 50 м уже «пройден» вместе с 15
    }

    @Test("Без «можно замкнуть» и без разрешения «Движение» сигналов нет")
    func noCuesWithoutClosure() {
        var cues = ClosureCues()
        #expect(cues.update(.noTrail) == nil)
        #expect(cues.update(.needsPath(targetSeq: 0, remainingMeters: 10)) == nil)
        #expect(cues.update(.needsTurn(targetSeq: 0)) == nil)
        #expect(cues.update(Self.canClose(10), hidden: true) == nil)

        var state = TrackerState()
        state.capturesNeedMotion = true
        #expect(!RunHUD.showsClosureHint(state))
        #expect(RunHUD.displayMeters(12.1) == 15 && RunHUD.displayMeters(0) == 0)
    }

    // MARK: - Плашки

    @Test("«След прервался» держится, пока идут разрывы, и гаснет после принятых точек и выдержки")
    func brokenTrackWarning() async throws {
        let session = try await RunSession.start(Fixture.run(), store: store, rules: .version1)
        var walk = Walk()
        _ = try await feed(session, walk.straight(seconds: 10))
        // Два скачка подряд (телепорт): след рвётся на каждом.
        let jumps = [walk.fix(east: 2_000, north: 0), walk.fix(east: 4_000, north: 0)]
        #expect(try await feed(session, jumps) == [.broken(.teleport), .broken(.teleport)])
        var state = TrackerState()
        state.stats = await session.stats
        #expect(state.stats.breaks[.teleport] == 2 && state.stats.acceptedAfterBreakAtMs == nil)
        let later = walk.time + 600
        #expect(RunHUD.warnings(state, now: later).map(\.kind) == [.trackBroken])
        #expect(RunHUD.warnings(state, now: later).first?.issue == .teleport)

        _ = try await feed(session, (0..<3).map { walk.fix(east: 4_000 + 1.4 * Double($0 + 1), north: 0) })
        state.stats = await session.stats
        let firstAccepted = try #require(state.stats.acceptedAfterBreakAtMs)
        let hold = Int64(RunHUD.warningHoldSeconds * 1_000)
        #expect(!RunHUD.warnings(state, now: Double(firstAccepted + hold - 1) / 1_000).isEmpty)
        #expect(RunHUD.warnings(state, now: Double(firstAccepted + hold) / 1_000).isEmpty)
    }

    @Test("«Слабый GPS» — по времени без принятых точек; принятая точка гасит")
    func weakGpsWarning() {
        var state = TrackerState()
        state.startedAtMs = 1_000_000
        state.stats.lastAcceptedAtMs = 1_010_000
        state.stats.lastIssue = .poorAccuracy
        state.stats.lastIssueAtMs = 1_012_000
        let quiet = Int64(RunHUD.weakGpsSeconds * 1_000)

        #expect(RunHUD.warnings(state, now: Double(1_010_000 + quiet - 1) / 1_000).isEmpty)
        #expect(RunHUD.warnings(state, now: Double(1_010_000 + quiet) / 1_000).map(\.kind) == [.weakGps])

        state.stats.lastAcceptedAtMs = 1_013_000
        #expect(RunHUD.warnings(state, now: Double(1_013_000 + quiet * 3) / 1_000).isEmpty)
    }

    @Test("Сервер недоступен, нужен вход, сбиты часы — разные плашки; порядок — предложенный")
    func syncWarningsAndOrder() {
        var state = TrackerState()
        let now = 2_000.0
        #expect(
            RunHUD.warnings(state, now: now, sync: SyncHealth(lastStop: .offline)).map(\.kind) == [.serverUnavailable])
        #expect(
            RunHUD.warnings(state, now: now, sync: SyncHealth(lastStop: .rateLimited)).map(\.kind) == [
                .serverUnavailable
            ])
        #expect(RunHUD.warnings(state, now: now, sync: SyncHealth(wake: .needsSignIn)).map(\.kind) == [.signInNeeded])
        #expect(
            RunHUD.warnings(state, now: now, sync: SyncHealth(wake: .blocked(.clockInvalid))).map(\.kind)
                == [.clockInvalid])
        #expect(RunHUD.warnings(state, now: now, sync: SyncHealth()).isEmpty)

        state.storageFailed = true
        state.capturesNeedMotion = true
        state.resumedAfterRestart = true
        state.stats.lastBreak = .vehicle
        state.stats.lastBreakAtMs = 1_000_000
        let all = RunHUD.warnings(
            state, now: now, sync: SyncHealth(lastStop: .offline, wake: .blocked(.clockInvalid)),
            queueSurvivesRestart: false)
        #expect(
            all.map(\.kind) == [
                .storageFailed, .queueNotDurable, .motionMissing, .clockInvalid, .trackBroken, .resumed,
                .serverUnavailable,
            ])
    }

    // MARK: - Метрики

    @Test("Время и средний темп: живой забег — по часам; темп — с 100 м")
    func liveClockAndPace() {
        var state = TrackerState()
        state.startedAtMs = 1_790_000_000_000
        state.stats.lastPointAtMs = 1_790_000_060_000
        state.stats.distanceMeters = 99
        #expect(RunHUD.elapsedSeconds(state, now: 1_790_000_600) == 600)
        #expect(RunHUD.averagePaceSecondsPerKilometer(state, now: 1_790_000_600) == nil)

        state.stats.distanceMeters = 2_000
        #expect(RunHUD.averagePaceSecondsPerKilometer(state, now: 1_790_000_600) == 300)
    }

    @Test("Демо-повтор ×20: время и темп — по времени последней точки, а не по часам")
    func replayClock() async throws {
        var replay = Fixture.run()
        replay.source = .replay
        let run = replay
        let store = self.store
        let tracker = RunTracker()
        try await tracker.start { try await RunSession.start(run, store: store, rules: .version1) }
        var walk = Walk()
        for fix in walk.straight(seconds: 120) {
            tracker.send(.fix(fix, receivedAt: fix.timestamp + 1))
        }
        await tracker.flush()
        let state = await tracker.state
        #expect(state.isReplay)
        let lastMs = try #require(state.stats.lastPointAtMs)
        let expected = Double(lastMs - run.startedAtMs) / 1_000
        // «Сейчас» по часам — через сутки: повтору всё равно.
        let tomorrow = Fixture.start + 86_400
        #expect(RunHUD.elapsedSeconds(state, now: tomorrow) == expected)
        let pace = try #require(RunHUD.averagePaceSecondsPerKilometer(state, now: tomorrow))
        #expect(abs(pace - expected / (state.stats.distanceMeters / 1_000)) < 1e-9)
        #expect(pace < 1_000)  // ≈ 12 мин/км шагом 1,4 м/с, а не «сутки на километр»
    }

    // MARK: - «≈+N га» тумана

    @Test("Туман забега против тумана за всё время: неизвестный тайл — «≥» и счёт по известным")
    func fogEstimateWithUnknownTile() async throws {
        let session = try await RunSession.start(Fixture.run(), store: store, rules: .version1)
        // Прямая через границу тайлов уровня 14 — туман забега в двух тайлах.
        let pixel = FogGrid.pixel(of: Self.place)
        let tileEdge = Double((Int(pixel.x) >> 8 + 1) << 8)
        let toEdge = (tileEdge - pixel.x) * FogGrid.cellSizeMeters(atLatitude: Self.place.latitude)
        var walk = Walk()
        _ = try await feed(session, (0..<120).map { walk.fix(east: toEdge - 80 + 1.4 * Double($0), north: 0) })

        let keys = await session.fogTilesToRequest()
        #expect(keys.count == 2)
        #expect(await session.fogTilesToRequest().isEmpty)  // каждый тайл просится один раз
        var stats = await session.stats
        #expect(stats.fogAreaSquareMeters > 0 && stats.fogNewSquareMeters == 0 && stats.fogNewIsLowerBound)

        await session.allTimeFogArrived(keys[0], FogTileBits(words: []))  // у сервера тайла нет — всё новое
        await session.allTimeFogArrived(keys[1], nil)  // запрос не удался
        stats = await session.stats
        let known = try #require(await session.fog.tiles[keys[0]])
        #expect(stats.fogNewIsLowerBound)
        #expect(abs(stats.fogNewSquareMeters - Double(known.count) * keys[0].cellAreaSquareMeters) < 1e-6)
        #expect(stats.fogNewSquareMeters > 0 && stats.fogNewSquareMeters < stats.fogAreaSquareMeters)
    }

    @Test("Известный тайл с уже открытыми клетками: они не новые, новое — только дальше по маршруту")
    func fogEstimateAgainstKnownTiles() async throws {
        let session = try await RunSession.start(Fixture.run(), store: store, rules: .version1)
        var walk = Walk()
        _ = try await feed(session, walk.straight(seconds: 70))
        let before = await session.fog.tiles
        for key in await session.fogTilesToRequest() {
            await session.allTimeFogArrived(key, before[key])
        }
        var stats = await session.stats
        #expect(stats.fogNewSquareMeters == 0 && !stats.fogNewIsLowerBound)

        _ = try await feed(session, (0..<60).map { walk.fix(east: 98 + 1.4 * Double($0), north: 0) })
        for key in await session.fogTilesToRequest() {
            await session.allTimeFogArrived(key, FogTileBits())
        }
        stats = await session.stats
        #expect(stats.fogNewSquareMeters > 0 && stats.fogNewSquareMeters < stats.fogAreaSquareMeters)
        #expect(!stats.fogNewIsLowerBound)
    }

    /// Поставщик тумана за всё время: у сервера тайлов нет (всё новое), запросы считаются.
    actor CountingFog: AllTimeFogProvider {
        private(set) var requested: [FogTileKey] = []

        func allTimeTile(_ key: FogTileKey, league: League) async -> FogTileBits? {
            requested.append(key)
            return FogTileBits()
        }
    }

    @Test("Трекер: новый тайл тумана забега — у поставщика ровно один раз; ответ — в сводке")
    func trackerAsksEachTileOnce() async throws {
        let fog = CountingFog()
        let tracker = RunTracker(fogTiles: fog)
        let store = self.store
        try await tracker.start { try await RunSession.start(Fixture.run(), store: store, rules: .version1) }
        var walk = Walk()
        for fix in walk.straight(seconds: 90) {
            tracker.send(.fix(fix, receivedAt: fix.timestamp + 1))
        }
        await tracker.flush()
        #expect(await eventually { await !fog.requested.isEmpty })
        #expect(
            await eventually {
                await tracker.flush()
                return await !tracker.state.stats.fogNewIsLowerBound
            })
        for fix in (0..<30).map({ walk.fix(east: 126 + 1.4 * Double($0), north: 0) }) {
            tracker.send(.fix(fix, receivedAt: fix.timestamp + 1))
        }
        await tracker.flush()

        let requested = await fog.requested
        #expect(requested.count == Set(requested).count)
        let stats = await tracker.state.stats
        #expect(stats.fogNewSquareMeters > 0 && abs(stats.fogNewSquareMeters - stats.fogAreaSquareMeters) < 1e-6)
    }

    // MARK: - Церемония

    @Test("Две заявки между двумя снимками экрана — две церемонии; кольцо петли — отдельным запросом")
    func twoClaimsTwoCeremonies() async throws {
        let tracker = RunTracker()
        let store = self.store
        try await tracker.start {
            try await RunSession.start(
                Fixture.run(), store: store, rules: .version1, policy: Fixture.policy(maxPoints: 120))
        }
        var walk = Walk()
        for fix in walk.square(side: 80) + walk.square(side: 80) {
            tracker.send(.fix(fix, receivedAt: fix.timestamp + 1))
        }
        await tracker.flush()
        let state = await tracker.state
        #expect(state.claimedLoops.map(\.claimNo) == [0, 1])

        var queue = CeremonyQueue()
        #expect(queue.newLoops(in: state).map(\.claimNo) == [0, 1])
        #expect(queue.newLoops(in: state).isEmpty)

        let ring = try #require(await tracker.ring(claimNo: 1))
        #expect(ring.loop == state.claimedLoops[1].loop)
        #expect(ring.coordinates.count == ring.loop.endSeq - ring.loop.startSeq + 1)
        #expect(await tracker.trail().count == 1)
        #expect(await tracker.ring(claimNo: 5) == nil)
    }

    @Test("Вторая фаза: только после первой в этом процессе; решение — статус и «взятое», отказ — код")
    func secondPhase() {
        let runId = UUID()
        var state = TrackerState()
        state.runId = runId
        state.claimedLoops = [ClaimedLoop(claimNo: 3, loop: Fixture.loop(40, 90))]
        var queue = CeremonyQueue()
        _ = queue.newLoops(in: state)

        var applied = PendingClaim(runId: runId, claimNo: 3, loop: Fixture.loop(40, 90))
        applied.outcome = ClaimOutcome(
            status: "applied", areaSquareMeters: 11_000, areaByOutcome: ["claimedNeutral": 11_000])
        // Заявка, сделанная до перезапуска приложения, решена сейчас: в отчёте есть, второй фазы нет.
        var before = PendingClaim(runId: runId, claimNo: 1, loop: Fixture.loop(2, 30))
        before.outcome = ClaimOutcome(status: "rejected", rejectCode: "too_small")
        var refused = PendingClaim(runId: runId, claimNo: 3, loop: Fixture.loop(40, 90))
        refused.refusedCode = "claim_limit"
        var report = SyncReport()
        report.settledClaims = [SettledClaim(before), SettledClaim(applied)]

        #expect(
            queue.decisions(in: report) == [
                CeremonyDecision(runId: runId, claimNo: 3, applied: true, takenSquareMeters: 11_000, refusalCode: nil)
            ])
        report.settledClaims = [SettledClaim(refused)]
        #expect(queue.decisions(in: report).map(\.refusalCode) == ["claim_limit"])
        #expect(CeremonyQueue().decisions(in: report).isEmpty)
        #expect(RunHUD.refusalDetail("stale") == .brief && RunHUD.refusalDetail("too_small") == .withReason)
    }

    // MARK: - Перезапуск приложения

    @Test("Перезапуск: сводка и «≈+N га» продолжаются с сохранённых при запечатывании; пробный забег — с нуля")
    func summarySurvivesRestart() async throws {
        let run = Fixture.run()
        let policy = Fixture.policy(maxPoints: 10)
        let session = try await RunSession.start(run, store: store, rules: .version1, policy: policy)
        var walk = Walk()
        _ = try await feed(session, walk.square(side: 80) + walk.straight(seconds: 40))
        for key in await session.fogTilesToRequest() {
            await session.allTimeFogArrived(key, FogTileBits())
        }
        _ = try await feed(session, (0..<12).map { walk.fix(east: 60 + 1.4 * Double($0), north: 0) })  // запечатать

        let saved = try #require(await store.runs().first?.summary)
        #expect(saved.distanceMeters > 300 && saved.fogNewSquareMeters > 0 && saved.points > 250)
        #expect(saved.latitude == 52.1)

        let resumed = try #require(
            try await RunSession.resume(runId: run.id, store: store, rules: .version1, policy: policy))
        let stats = await resumed.stats
        #expect(stats.distanceMeters == saved.distanceMeters && stats.points == saved.points)
        #expect(stats.fogNewSquareMeters == saved.fogNewSquareMeters)
        #expect(stats.fogAreaSquareMeters == saved.fogAreaSquareMeters)
        #expect(stats.loops == 1 && stats.loopAreaSquareMeters > 5_000)
        #expect(await resumed.closureHint == .noTrail)

        _ = try await feed(resumed, (0..<20).map { walk.fix(east: 80 + 1.4 * Double($0), north: 0) })
        #expect(await resumed.stats.distanceMeters > saved.distanceMeters + 20)

        let probe = try #require(
            try await RunSession.resume(
                runId: run.id, store: store, rules: .version1, policy: policy, continuingSummary: false))
        #expect(await probe.stats.distanceMeters == 0 && probe.motionAuthorized)
    }

    @Test(
        "Продолженный забег без «Движения»: флаг из забега — плашка остаётся; «перезапускалось» — до «можно замкнуть»")
    func resumedWithoutMotion() async throws {
        var run = Fixture.run()
        run.motionAuthorized = false
        let session = try await RunSession.start(
            run, store: store, rules: .version1, policy: Fixture.policy(maxPoints: 5))
        var walk = Walk()
        _ = try await feed(session, walk.straight(seconds: 12))

        let recovered = try #require(
            try await RunTracker.recover(
                store: store, deviceId: Fixture.device, signedIn: Fixture.owner, now: walk.time + 5,
                rules: { _ in .version1 }))
        #expect(!recovered.motionAuthorized)
        let tracker = RunTracker()
        try await tracker.resume(recovered)
        var state = await tracker.state
        #expect(state.capturesNeedMotion && state.resumedAfterRestart)
        #expect(RunHUD.warnings(state, now: walk.time).map(\.kind) == [.motionMissing, .resumed])

        for fix in walk.square(side: 80, from: (20, 20)) {
            tracker.send(.fix(fix, receivedAt: fix.timestamp + 1))
        }
        await tracker.flush()
        state = await tracker.state
        #expect(!state.resumedAfterRestart && state.capturesNeedMotion)
    }

    @Test("Итог после «Финиша» и FogChanged: «≈» тумана — сохранённое, не пересчитанное по кэшу")
    func frozenFogEstimate() async throws {
        let run = Fixture.run()
        let session = try await RunSession.start(run, store: store, rules: .version1)
        var walk = Walk()
        _ = try await feed(session, walk.straight(seconds: 60))
        for key in await session.fogTilesToRequest() {
            await session.allTimeFogArrived(key, FogTileBits())
        }
        try await session.finish(endedAt: walk.time)
        let finished = try #require(await store.runs().first)
        let frozen = try #require(finished.summary)
        // «Финиш» досчитал хвост тумана (последние 30 с) — в сводке он есть.
        #expect(frozen.fogNewSquareMeters == frozen.fogAreaSquareMeters && frozen.fogNewSquareMeters > 0)
        #expect(frozen.fogNewSquareMeters == (await session.stats).fogNewSquareMeters)

        // Сервер открыл туман забега, кэш получил его клетки: пересчёт дал бы ноль — итог его не делает.
        for (key, bits) in await session.fog.tiles {
            await session.allTimeFogArrived(key, bits)
        }
        let after = try #require(await store.runs().first)
        #expect(after.summary == frozen)
        #expect(RunResult(run: after, claims: [], now: walk.time).fogEstimateSquareMeters == frozen.fogNewSquareMeters)
    }
}
