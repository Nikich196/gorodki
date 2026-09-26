import Foundation
import GameCore
import Testing

@testable import Sync

/// Что показывают экраны забега (RunScreens.swift): числа и тексты HUD, кольцо и стрелка, две фазы церемонии, список
/// пропущенных, Live Activity, итог — «позже» до границы публичности.
@Suite("Экраны забега: HUD, церемония, Live Activity, итог (docs/architecture/run-hud.md)")
struct RunScreensTests {
    private let space = "\u{00A0}"
    private static let start = 1_790_000_000.0
    private static let runId = UUID()

    private static func target(
        seq: Int = 3, distance: Double, bearing: Double = 90, area: Double = 12_480, small: Bool = false,
        large: Bool = false
    ) -> ClosureTarget {
        ClosureTarget(
            seq: seq, coordinate: Coordinate(latitude: 52.09, longitude: 23.73), distanceMeters: distance,
            bearingDegrees: bearing, estimatedAreaSquareMeters: area, belowServerMinimum: small, tooLarge: large)
    }

    private static func state(
        hint: ClosureHint = .noTrail, distance: Double = 0, motion: Bool = true, fog: Double = 0,
        lowerBound: Bool = false
    ) -> TrackerState {
        var state = TrackerState()
        state.isRunning = true
        state.runId = runId
        state.startedAtMs = StoragePrecision.milliseconds(start)
        state.closureHint = hint
        state.capturesNeedMotion = !motion
        state.stats.distanceMeters = distance
        state.stats.fogNewSquareMeters = fog
        state.stats.fogNewIsLowerBound = lowerBound
        return state
    }

    private static func loop(_ claimNo: Int, area: Double = 12_000) -> ClaimedLoop {
        ClaimedLoop(
            claimNo: claimNo, loop: LoopClaim(startSeq: 0, endSeq: 10, closure: .proximity, estimatedArea: area))
    }

    private static func decision(_ claimNo: Int, applied: Bool = true, taken: Double = 12_480, code: String? = nil)
        -> CeremonyDecision
    {
        CeremonyDecision(
            runId: runId, claimNo: claimNo, applied: applied, takenSquareMeters: taken,
            refusalCode: applied ? nil : code)
    }

    // MARK: - HUD

    @Test("Три метрики: «3,21» км, «17:42», темп «5:32» — и «–:––», пока меньше 100 м")
    func metrics() {
        let now = Self.start + 1_062
        let running = RunHUDReadout(Self.state(distance: 3_207), now: now)
        #expect(running.distanceText == "3,21" && running.elapsedText == "17:42")
        #expect(running.paceText == "5:31")

        let early = RunHUDReadout(Self.state(distance: 80), now: now)
        #expect(early.paceText == RunHUDReadout.noPace && early.distanceText == "0,08")
    }

    @Test("«+0,12 га тумана»; неизвестные тайлы — «≥ +0,12 га тумана»")
    func fogChip() {
        let now = Self.start + 60
        #expect(RunHUDReadout(Self.state(fog: 1_234), now: now).fogText == "+0,12\(space)га тумана")
        #expect(
            RunHUDReadout(Self.state(fog: 1_234, lowerBound: true), now: now).fogText
                == "≥\(space)+0,12\(space)га тумана")
    }

    @Test("«До замыкания 140 м» и «Замкни петлю — будет ≈ +1,2 га»; шаг экрана 5 м — вверх")
    func canClose() {
        let display = ClosureDisplay(.canClose(Self.target(distance: 136.2)), hidden: false)
        #expect(display.caption == "До замыкания" && display.meters == 140 && display.headline == nil)
        #expect(display.hint() == "Замкни петлю — будет ≈\(space)+1,2\(space)га")
        #expect(display.readout?.bearingDegrees == 90)
    }

    @Test("Маленькая и слишком большая петля — сказать до замыкания; нет следа, «нужен путь», «сверни»")
    func otherStates() {
        let small = ClosureDisplay(.canClose(Self.target(distance: 20, area: 1_200, small: true)), hidden: false)
        #expect(small.hint() == "Пока петля меньше 0,25\(space)га — сервер её не засчитает")
        let large = ClosureDisplay(.canClose(Self.target(distance: 20, large: true)), hidden: false)
        #expect(large.hint() == "Петля больше 3,5\(space)км² — такие не засчитываются")

        #expect(ClosureDisplay(.noTrail, hidden: false) == .waitingForTrail)
        let path = ClosureDisplay(.needsPath(targetSeq: 0, remainingMeters: 118), hidden: false)
        #expect(path.meters == 120 && path.caption == "До петли")
        let turn = ClosureDisplay(.needsTurn(targetSeq: 0), hidden: false)
        #expect(turn.headline == "Сверни" && turn.meters == nil && turn.readout == nil)
    }

    @Test("Без «Движения» «до замыкания» спрятано: ни числа, ни стрелки — и плашка объясняет почему")
    func hiddenWithoutMotion() {
        let state = Self.state(hint: .canClose(Self.target(distance: 40)), motion: false)
        let readout = RunHUDReadout(state, now: Self.start + 10)
        #expect(readout.closure == .hidden)
        #expect(readout.closure.meters == nil && readout.closure.readout == nil)
        #expect(readout.warnings.map(\.kind) == [.motionMissing])
        #expect(readout.warnings.first?.text.title == "Захваты не засчитаются")

        var progress = ClosureProgress()
        #expect(progress.update(state.closureHint, hidden: true) == nil)
    }

    @Test("Кольцо: от самой дальней точки к цели; новая цель и смена состояния — заново")
    func progressRing() {
        var progress = ClosureProgress()
        #expect(progress.update(.noTrail) == nil)
        #expect(progress.update(.needsPath(targetSeq: 0, remainingMeters: 150)) == 0)
        #expect(progress.update(.needsPath(targetSeq: 0, remainingMeters: 75)) == 0.5)
        #expect(progress.update(.canClose(Self.target(seq: 0, distance: 200))) == 0)
        #expect(progress.update(.canClose(Self.target(seq: 0, distance: 50))) == 0.75)
        #expect(progress.update(.canClose(Self.target(seq: 0, distance: 250))) == 0)  // ушёл дальше — новая «дальняя»
        #expect(progress.update(.canClose(Self.target(seq: 7, distance: 100))) == 0)  // заявка — цель новая
        #expect(progress.update(.canClose(Self.target(seq: 7, distance: 0))) == 1)
    }

    @Test("Стрелка: по компасу, без компаса — по курсу движения, без обоих — не показывать")
    func arrow() throws {
        #expect(RunHUD.arrowAngle(bearing: 90, heading: 45, course: 180) == 45)
        #expect(RunHUD.arrowAngle(bearing: 90, heading: nil, course: 180) == 270)
        #expect(RunHUD.arrowAngle(bearing: 10, heading: 350, course: nil) == 20)
        #expect(RunHUD.arrowAngle(bearing: 90, heading: nil, course: nil) == nil)

        let origin = Coordinate(latitude: 52.09, longitude: 23.73)
        let plane = LocalTangentPlane(origin: origin)
        let east = [origin, plane.unproject(PlanarPoint(east: 30, north: 0))]
        let north = [origin, plane.unproject(PlanarPoint(east: 0, north: 30))]
        let south = [origin, plane.unproject(PlanarPoint(east: 0, north: -30))]
        #expect(abs(try #require(RunHUD.course(of: east)) - 90) < 0.5)
        #expect(abs(try #require(RunHUD.course(of: south)) - 180) < 0.5)
        let northCourse = try #require(RunHUD.course(of: north))
        #expect(northCourse < 0.5 || northCourse > 359.5)
        let standing = [origin, plane.unproject(PlanarPoint(east: 3, north: 2))]
        #expect(RunHUD.course(of: standing) == nil)  // ближе 10 м — шум, курса нет
        #expect(RunHUD.course(of: []) == nil)
    }

    // MARK: - Церемония в две фазы

    @Test("Экран виден: петля играет сразу, вторая ждёт; «Продолжить забег» — следующая")
    func twoLoopsInARow() {
        var stage = CeremonyStage()
        stage.claimed([Self.loop(0), Self.loop(1)], runId: Self.runId, visible: true)
        #expect(stage.playing?.claimNo == 0 && stage.upcoming.map(\.claimNo) == [1] && stage.missed.isEmpty)
        #expect(stage.playing?.status == CeremonyText.checking)

        stage.finishPlaying()
        #expect(stage.playing?.claimNo == 1 && stage.upcoming.isEmpty)
        stage.finishPlaying()
        #expect(stage.playing == nil)
    }

    @Test("Вторая фаза — без новой церемонии: обновляет играющую; решение по закрытой — тост")
    func secondPhase() {
        var stage = CeremonyStage()
        stage.claimed([Self.loop(0)], runId: Self.runId, visible: true)
        let updated = stage.decided([Self.decision(0)])
        #expect(updated.map(\.claimNo) == [0])
        #expect(stage.playing?.status == "подтверждено" && stage.upcoming.isEmpty && stage.toasts.isEmpty)

        stage.claimed([Self.loop(1)], runId: Self.runId, visible: true)
        stage.finishPlaying()  // петля 0 закрыта, играет петля 1
        stage.finishPlaying()  // закрыта и петля 1 — ещё «проверяем»
        stage.decided([Self.decision(1, applied: false, code: "too_small")])
        #expect(stage.playing == nil && stage.toasts.map(\.claimNo) == [1])
        #expect(stage.toasts.first?.status == "не засчитана · Петля меньше 0,25 га")
        stage.dismissToast(stage.toasts[0].id)
        #expect(stage.toasts.isEmpty)
    }

    @Test("В кармане: петли — в список пропущенных, решения обновляют их; список закрыт — решения тостом")
    func missedInPocket() {
        var stage = CeremonyStage()
        stage.claimed([Self.loop(0), Self.loop(1, area: 8_000)], runId: Self.runId, visible: false)
        #expect(stage.playing == nil && stage.missed.map(\.claimNo) == [0, 1])
        stage.decided([Self.decision(1)])
        #expect(stage.missed.map(\.status) == [CeremonyText.checking, "подтверждено"])

        stage.dismissMissed()
        #expect(stage.missed.isEmpty)
        stage.decided([Self.decision(0)])
        #expect(stage.toasts.map(\.claimNo) == [0])
    }

    @Test("Решение для петли, которой не было в этом процессе, ничего не показывает; конец забега — всё заново")
    func unknownDecisionAndRunEnd() {
        var stage = CeremonyStage()
        #expect(stage.decided([Self.decision(5)]).isEmpty && stage.toasts.isEmpty)
        stage.claimed([Self.loop(0)], runId: Self.runId, visible: true)
        stage.runEnded()
        #expect(stage == CeremonyStage())
    }

    @Test("Очередь и сцена вместе: две заявки между снимками — две церемонии; вторая фаза — из отчёта прохода")
    func queueFeedsStage() {
        var queue = CeremonyQueue()
        var stage = CeremonyStage()
        var state = Self.state()
        state.claimedLoops = [Self.loop(0), Self.loop(1)]
        stage.claimed(queue.newLoops(in: state), runId: Self.runId, visible: true)
        #expect(queue.newLoops(in: state).isEmpty)  // каждая — один раз
        #expect(stage.playing?.claimNo == 0 && stage.upcoming.count == 1)

        var claim = PendingClaim(runId: Self.runId, claimNo: 0, loop: Self.loop(0).loop)
        claim.outcome = ClaimOutcome(status: "applied", areaSquareMeters: 12_480)
        var report = SyncReport()
        report.settledClaims = [SettledClaim(claim)]
        stage.decided(queue.decisions(in: report))
        #expect(stage.playing?.decision?.takenSquareMeters == 12_480)
    }

    @Test("Числа церемонии: «≈ +1,2 га» → «+1,25 га»")
    func ceremonyNumbers() {
        #expect(CeremonyText.estimate(12_480) == "≈\(space)+1,2\(space)га")
        #expect(CeremonyText.taken(12_480) == "+1,25\(space)га")
    }

    // MARK: - Отказы

    @Test("Отказы: сбои — одной фразой, остальное — с причиной; разрыв следа — словами")
    func refusals() {
        for code in ["stale", "failed", "engine_failed", "too_many_attempts", "run_not_found"] {
            #expect(RefusalText.reason(code) == RefusalText.brief)
        }
        #expect(RefusalText.reason("segment_broken:vehicle") == "След прервался: транспорт")
        #expect(RefusalText.reason("motion_not_authorized") == "Нет доступа к «Движению и фитнесу»")
        #expect(RefusalText.reason("claim_limit") == "Слишком много заявок — лимит")
        #expect(RefusalText.reason(PendingClaim.runRejectedCode) == "Забег не принят сервером")
        #expect(RefusalText.reason("from_the_future") == "Сервер не засчитал петлю")
        #expect(RefusalText.breakTitle(.vehicle) == "Похоже на транспорт — захват на паузе")
    }

    @Test("Тексты без рода: ни «пробежал», ни «вышел», ни «готова» — во всех плашках и отказах")
    func genderless() {
        var texts = RunWarning.Kind.allCases.flatMap { kind -> [String] in
            let text = RunWarning(kind, issue: .noSteps).text
            return [text.title, text.detail]
        }
        let codes = [
            "too_short", "not_closed", "too_small", "too_narrow", "too_large", "empty", "daily_limit", "device_shared",
            "account_frozen", "claim_invalid", "claim_conflict", "upload_window_closed",
        ]
        texts += codes.map(RefusalText.reason)
        for text in texts {
            for word in ["пробежал", "вышел", "зашёл", "сделал", "смог"] {
                #expect(!text.contains(word), "«\(text)»")
            }
        }
    }

    // MARK: - Live Activity

    @Test("Live Activity: «До замыкания 140 м» без стрелки и числа строкой; транспорт — первой строкой")
    func liveActivity() {
        var state = Self.state(hint: .canClose(Self.target(distance: 138)), distance: 3_207, fog: 1_234)
        let now = Self.start + 1_062
        let readout = RunHUDReadout(state, now: now)
        #expect(RunActivityText.title(readout) == "До замыкания 140\(space)м")
        #expect(
            RunActivityText.detail(readout)
                == "3,21\(space)км · 5:31\(space)/км · +0,12\(space)га тумана")

        state.stats.lastBreak = .vehicle
        state.stats.lastBreakAtMs = StoragePrecision.milliseconds(now - 2)
        #expect(RunActivityText.title(RunHUDReadout(state, now: now)) == "Похоже на транспорт — захват на паузе")

        let early = RunHUDReadout(Self.state(), now: Self.start + 5)
        #expect(RunActivityText.title(early) == "Забег · Ждём GPS…")
        #expect(RunActivityText.detail(early) == "0,00\(space)км · +0,00\(space)га тумана")
    }

    // MARK: - Итог

    private static func finishedRun(_ change: (inout LocalRun) -> Void = { _ in }) -> LocalRun {
        var run = LocalRun(
            id: runId, ownerId: "p", league: .run, configVersion: 1,
            startedAtMs: StoragePrecision.milliseconds(start), deviceId: UUID(), appVersion: "t", motionAuthorized: true
        )
        run.endedAtMs = run.startedAtMs + 1_062_000
        run.lastSeq = 1_000
        run.serverState = .started
        run.finishSent = true
        run.confirmedComplete = true
        var summary = RunSummary()
        summary.distanceMeters = 3_207
        summary.breaks = ["vehicle": 2]
        summary.fogNewSquareMeters = 1_234
        summary.latitude = 52.1
        run.summary = summary
        change(&run)
        return run
    }

    private static func claim(_ no: Int, _ change: (inout PendingClaim) -> Void = { _ in }) -> PendingClaim {
        var claim = PendingClaim(runId: runId, claimNo: no, loop: loop(no).loop)
        claim.sent = true
        change(&claim)
        return claim
    }

    @Test("Итог до границы публичности: «взятое» есть, разбивки нет — «позже»; визиты и туман сервера — «позже»")
    func resultBeforeBoundary() {
        let claims = [
            Self.claim(0) { $0.outcome = ClaimOutcome(status: "applied", areaSquareMeters: 12_480) },
            Self.claim(1) { $0.outcome = ClaimOutcome(status: "rejected", rejectCode: "too_small") },
            Self.claim(2),
        ]
        let result = RunResult(run: Self.finishedRun(), claims: claims, now: Self.start + 2_000)
        let readout = RunResultReadout(result)

        #expect(readout.distanceText == "3,21" && readout.durationText == "17:42" && readout.paceText == "5:31")
        #expect(readout.takenText == "+1,25\(space)га" && readout.estimateText == "≈\(space)+3,6\(space)га")
        #expect(readout.breakdown == nil && readout.breakdownPending)
        #expect(readout.visitedParcels == nil && readout.fogServerText == nil)
        #expect(readout.fogEstimateText == "+0,12\(space)га")
        #expect(readout.claims.map(\.state) == [.applied, .refused, .waiting])
        #expect(readout.claims[0].status == "подтверждено · +1,25\(space)га")
        #expect(readout.claims[1].status == "не засчитана · Петля меньше 0,25 га")
        #expect(readout.claims[2].status == "проверяется…")
        #expect(readout.breaks == [RunResultReadout.Row(title: "транспорт", value: "×2")])
        #expect(readout.readiness == .computing && readout.readinessText == "сервер считает")
    }

    @Test("Итог после границы: разбивка по видам по-русски, незнакомый вид — как есть; визиты и туман сервера")
    func resultAfterBoundary() {
        let claims = [
            Self.claim(0) {
                $0.outcome = ClaimOutcome(
                    status: "applied", areaSquareMeters: 12_480,
                    areaByOutcome: ["refreshed": 500, "claimedNeutral": 12_480, "somethingNew": 7, "shielded": 0])
            }
        ]
        let run = Self.finishedRun {
            $0.fogNewCells = 100
            $0.visitedParcels = 3
        }
        let readout = RunResultReadout(RunResult(run: run, claims: claims, now: Self.start + 3_000))
        #expect(
            readout.breakdown?.map(\.title) == ["Ничья земля", "Своя освежена", "somethingNew"],
            "нулевые виды не показываются")
        #expect(readout.breakdown?.first?.value == "12\(space)480\(space)м²")
        #expect(!readout.breakdownPending && readout.visitedParcels == 3 && readout.fogServerText != nil)
        #expect(readout.readiness == .ready)
    }

    @Test("Забег отвергнут: итог говорит об этом, «готов»; офлайн — «ждёт сети»")
    func rejectedAndOffline() {
        let rejected = Self.finishedRun {
            $0.serverState = .rejected
            $0.rejectCode = "replay_forbidden"
        }
        let readout = RunResultReadout(RunResult(run: rejected, claims: [], now: Self.start + 2_000))
        #expect(readout.rejection?.hasPrefix("Забег не принят") == true && readout.readiness == .ready)

        let offline = Self.finishedRun {
            $0.serverState = .unknown
            $0.confirmedComplete = false
        }
        let waiting = RunResultReadout(RunResult(run: offline, claims: [Self.claim(0)], now: Self.start + 2_000))
        #expect(waiting.claims.first?.status == "ждёт сети" && waiting.readiness == .waitingForNetwork)
    }
}
