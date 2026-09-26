import Foundation
import GameCore
import Persistence
import Sync
import Testing

@testable import Gorodki

/// Модель экранов забега (`RunScreenModel`, `RunResultModel`): «Старт» с подсказками разрешений, числа HUD, скрытие без
/// «Движения», две фазы церемонии, список пропущенных, голос и вибрация, итог — «позже» до границы публичности.
/// Трекер, разрешения и голос — подмены: правила проверяются без системы.
@MainActor
@Suite("Экраны забега: модель HUD, церемонии и итога")
struct RunScreenModelTests {
    private let space = "\u{00A0}"
    private static let runId = UUID()
    private static let start = Date(timeIntervalSince1970: 1_790_000_000)

    private final class Driver: RunDriving {
        var started: [League] = []
        var finished = 0
        var startError: (any Error)?

        func startRun(league: League) async throws {
            if let startError { throw startError }
            started.append(league)
        }

        func finish() async throws { finished += 1 }
        func trail() async -> [[Coordinate]] { [] }
        func ring(claimNo: Int) async -> LoopRing? { nil }
        func requestFullAccuracy() async -> Bool { false }
    }

    private final class Access: RunStartAccess {
        var undetermined: Set<RunStartPermission> = [.location, .motion]
        var locationDenied = false
        var requested: [RunStartPermission] = []

        func needsPrimer(_ permission: RunStartPermission) -> Bool { undetermined.contains(permission) }

        func request(_ permission: RunStartPermission) async {
            requested.append(permission)
            undetermined.remove(permission)
        }
    }

    private final class Feedback: RunFeedback {
        var haptics: [RunFeedbackEvent] = []
        var spoken: [String] = []
        var alerts: [String] = []

        func haptic(_ event: RunFeedbackEvent) { haptics.append(event) }
        func speak(_ text: String) { spoken.append(text) }
        func alert(title: String, body: String) { alerts.append(title + " · " + body) }
    }

    private struct Results: RunResultSource {
        var published = false

        func result(of runId: UUID, now: Double) async -> RunResult? {
            var run = LocalRun(
                id: runId, ownerId: "p", league: .run, configVersion: 1, startedAtMs: 1_790_000_000_000,
                deviceId: UUID(), appVersion: "t", motionAuthorized: true)
            run.endedAtMs = run.startedAtMs + 1_062_000
            run.lastSeq = 10
            run.serverState = .started
            run.finishSent = true
            run.confirmedComplete = true
            if published {
                run.fogNewCells = 10
                run.visitedParcels = 2
            }
            var claim = PendingClaim(
                runId: runId, claimNo: 0,
                loop: LoopClaim(startSeq: 0, endSeq: 9, closure: .proximity, estimatedArea: 12_000))
            claim.sent = true
            claim.outcome = ClaimOutcome(
                status: "applied", areaSquareMeters: 12_480,
                areaByOutcome: published ? ["claimedNeutral": 12_480] : nil)
            return RunResult(run: run, claims: [claim], now: now)
        }

        func track(of runId: UUID) async -> [Coordinate] { [] }
        func refresh(_ runId: UUID) async {}
        func gpx(_ runId: UUID) async -> URL? { nil }
        func history() async -> [RunHistoryEntry] { [] }
    }

    private func model(
        driver: Driver = Driver(), access: Access? = Access(), feedback: Feedback = Feedback()
    ) -> RunScreenModel {
        let defaults = UserDefaults(suiteName: "run-screen-tests-\(UUID().uuidString)") ?? .standard
        return RunScreenModel(
            driver: driver, access: access, feedback: feedback,
            makeResult: { RunResultModel(runId: $0, justFinished: true, source: Results()) }, defaults: defaults,
            clock: { Self.start.addingTimeInterval(1_062) })
    }

    private static func state(distance: Double = 3_207, closure: Double? = 137, motion: Bool = true) -> TrackerState {
        var state = TrackerState()
        state.isRunning = true
        state.runId = runId
        state.startedAtMs = StoragePrecision.milliseconds(start.timeIntervalSince1970)
        state.capturesNeedMotion = !motion
        state.stats.distanceMeters = distance
        state.stats.lastAcceptedAtMs = state.startedAtMs.map { $0 + 1_061_000 }
        if let closure {
            state.closureHint = .canClose(
                ClosureTarget(
                    seq: 3, coordinate: Coordinate(latitude: 52.09, longitude: 23.73), distanceMeters: closure,
                    bearingDegrees: 45, estimatedAreaSquareMeters: 12_480, belowServerMinimum: false,
                    tooLarge: false))
        }
        return state
    }

    private static func claimed(_ state: TrackerState, loops: Int) -> TrackerState {
        var state = state
        state.claimedLoops = (0..<loops).map {
            ClaimedLoop(
                claimNo: $0, loop: LoopClaim(startSeq: 0, endSeq: 9, closure: .proximity, estimatedArea: 12_000))
        }
        return state
    }

    private static func report(_ claimNo: Int, applied: Bool) -> SyncReport {
        var claim = PendingClaim(
            runId: runId, claimNo: claimNo,
            loop: LoopClaim(startSeq: 0, endSeq: 9, closure: .proximity, estimatedArea: 12_000))
        claim.outcome =
            applied
            ? ClaimOutcome(status: "applied", areaSquareMeters: 12_480)
            : ClaimOutcome(status: "rejected", rejectCode: "too_small")
        var report = SyncReport()
        report.settledClaims = [SettledClaim(claim)]
        return report
    }

    // MARK: - «Старт»

    @Test("«Старт»: лига «Бег» → подсказка геопозиции → подсказка «Движения» → забег; HUD — когда лист закрылся")
    func startWithPrimers() async {
        let driver = Driver()
        let access = Access()
        let run = model(driver: driver, access: access)

        run.startTapped()
        #expect(run.startSheetShown && run.startStep == .league && run.league == .run)
        await run.begin()
        #expect(run.startStep == .primer(.location) && driver.started.isEmpty)
        await run.primerContinue()
        #expect(access.requested == [.location] && run.startStep == .primer(.motion))
        await run.primerContinue()
        #expect(access.requested == [.location, .motion] && driver.started == [.run])
        #expect(!run.startSheetShown && !run.hudPresented)
        run.startSheetDismissed()
        #expect(run.hudPresented && run.coverShown)
    }

    @Test("Геопозиция запрещена — шаг «Настройки»; без входа — текст, забег не начат")
    func startProblems() async {
        let access = Access()
        access.undetermined = []
        access.locationDenied = true
        let denied = model(access: access)
        denied.startTapped()
        await denied.begin()
        #expect(denied.startStep == .locationDenied)

        let driver = Driver()
        driver.startError = RunController.StartProblem.notSignedIn
        let signedOut = model(driver: driver, access: nil)
        signedOut.startTapped()
        await signedOut.begin()
        #expect(signedOut.startError == "Забег начнётся после входа" && signedOut.startSheetShown)
    }

    @Test("Забег идёт — «Старт» открывает HUD, а не лист")
    func startWhileRunning() {
        let run = model()
        run.receive(Self.state())
        run.startTapped()
        #expect(run.hudPresented && !run.startSheetShown)
    }

    // MARK: - HUD

    @Test("Числа HUD: «3,21» км, «5:31», «До замыкания 140 м»; Live Activity — те же числа строкой, без стрелки")
    func formatting() {
        let run = model()
        run.receive(Self.state())
        #expect(run.readout.distanceText == "3,21" && run.readout.paceText == "5:31")
        #expect(run.readout.closure.meters == 140 && run.readout.closure.caption == "До замыкания")
        #expect(run.ringProgress == 0)

        let content = RunController.activityContent(Self.state(), now: Self.start.timeIntervalSince1970 + 1_062)
        #expect(content.title == "До замыкания 140\(space)м")
        #expect(content.detail == "3,21\(space)км · 5:31\(space)/км · +0,00\(space)га тумана")
    }

    @Test("Без «Движения» «до замыкания» спрятано, сигналов 50/15 м нет — ни вибрации, ни голоса")
    func hiddenWithoutMotion() {
        let feedback = Feedback()
        let run = model(feedback: feedback)
        run.hudPresented = true
        run.receive(Self.state(closure: 80, motion: false))
        run.receive(Self.state(closure: 40, motion: false))
        run.receive(Self.state(closure: 10, motion: false))
        #expect(run.readout.closure == .hidden && run.readout.closure.meters == nil)
        #expect(run.ringProgress == nil && run.arrowAngle == nil)
        #expect(feedback.haptics.isEmpty && feedback.spoken.isEmpty && run.cuePulses == 0)
        #expect(run.readout.warnings.map(\.kind) == [.motionMissing])
    }

    @Test("Сигналы 50/15 м: на экране — вибрация и пульс кольца, в кармане — голос; голос выключается")
    func closureCues() {
        let feedback = Feedback()
        let run = model(feedback: feedback)
        run.hudPresented = true
        run.receive(Self.state(closure: 80))
        run.receive(Self.state(closure: 45))
        #expect(feedback.haptics == [.closure(thresholdMeters: 50)] && feedback.spoken.isEmpty && run.cuePulses == 1)

        run.collapse()
        run.receive(Self.state(closure: 12))
        #expect(feedback.spoken == ["До замыкания 15 метров"] && run.cuePulses == 2)

        run.settings.voice = false
        run.receive(Self.state(closure: 80))
        run.receive(Self.state(closure: 40))
        #expect(feedback.spoken.count == 1)
    }

    @Test("Без компаса стрелка — по курсу движения; компас важнее")
    func arrowWithoutCompass() {
        let run = model()
        run.receive(Self.state())
        #expect(run.arrowAngle == nil)  // следа нет — ни курса, ни компаса
        run.heading = 90
        #expect(run.arrowAngle == 315)
    }

    // MARK: - Церемония

    @Test("Две фазы: петля играет сразу; решение сервера — в той же карточке, без новой церемонии")
    func twoPhases() {
        let feedback = Feedback()
        let run = model(feedback: feedback)
        run.hudPresented = true
        run.receive(Self.claimed(Self.state(), loops: 1))
        #expect(run.stage.playing?.claimNo == 0 && run.stage.playing?.decision == nil)
        #expect(feedback.haptics == [.loopClosed])

        run.receive(Self.report(0, applied: true))
        #expect(run.stage.playing?.decision?.applied == true && run.stage.upcoming.isEmpty && run.decisions == 1)
        #expect(feedback.haptics.last == .decided(applied: true))
        #expect(CeremonyStatus.confirmed(12_480) == "подтверждено · 12\(space)480\(space)м² · 125 соток")

        run.continueRun()
        #expect(run.stage.playing == nil)
        run.receive(Self.claimed(Self.state(), loops: 1))
        #expect(run.stage.playing == nil)  // та же петля второй раз не играет
    }

    @Test("В кармане: петля — оповещение Live Activity и голос; при возвращении — список пропущенных")
    func missedInPocket() {
        let feedback = Feedback()
        let run = model(feedback: feedback)
        run.receive(Self.claimed(Self.state(), loops: 2))
        #expect(run.stage.playing == nil && run.stage.missed.count == 2)
        #expect(feedback.alerts.count == 2 && feedback.spoken.count == 2)
        #expect(feedback.alerts.first == "Петля замкнута · ≈\(space)+1,2\(space)га")

        run.receive(Self.report(1, applied: false))
        #expect(run.stage.missed.last?.status == "не засчитана · Петля меньше 0,25 га")

        run.expand()
        #expect(run.hudPresented && !run.missedShown)  // список — когда HUD уже на экране
        run.hudAppeared()
        #expect(run.missedShown)
        run.dismissMissed()
        #expect(run.stage.missed.isEmpty && !run.missedShown)
    }

    @Test("Приложение в фоне при открытом HUD — тоже «в кармане»: список, когда вернулись")
    func backgroundWithHUD() {
        let run = model()
        run.hudPresented = true
        run.sceneChanged(active: false)
        run.receive(Self.claimed(Self.state(), loops: 1))
        #expect(run.stage.missed.count == 1 && run.stage.playing == nil)
        run.sceneChanged(active: true)
        #expect(run.missedShown)
    }

    // MARK: - «Финиш» и итог

    @Test("«Финиш»: забег закончен, HUD превращается в итог; закрыли — слоя нет")
    func finishShowsResult() async {
        let driver = Driver()
        let run = model(driver: driver)
        run.receive(Self.state())
        run.hudPresented = true
        await run.finish()
        #expect(driver.finished == 1 && !run.hudPresented && run.result?.runId == Self.runId)

        var ended = Self.state()
        ended.isRunning = false
        run.receive(ended)
        #expect(run.coverShown)
        run.coverShown = false
        #expect(run.result == nil && !run.coverShown)
    }

    @Test("Итог до границы публичности — разбивки нет, визиты и туман сервера «позже»; после — есть")
    func resultLater() async {
        let before = RunResultModel(runId: Self.runId, justFinished: true, source: Results())
        await before.load()
        let early = before.readout
        #expect(early?.breakdown == nil && early?.breakdownPending == true)
        #expect(early?.visitedParcels == nil && early?.fogServerText == nil && early?.takenText == "+1,25\(space)га")

        let after = RunResultModel(runId: Self.runId, justFinished: false, source: Results(published: true))
        await after.load()
        #expect(after.readout?.breakdown?.map(\.title) == ["Ничья земля"])
        #expect(after.readout?.visitedParcels == 2 && after.readout?.readiness == .ready)
        #expect(CountText.parcels(2) == "2 участка")
    }
}
