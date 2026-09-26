import DesignSystem
import Foundation
import GameCore
import Observation
import Sync

/// Что умеет забег для экранов — `RunController` в приложении, заглушка в режиме фикстур и тестах.
@MainActor
protocol RunDriving: AnyObject {
    func startRun(league: League) async throws
    func finish() async throws
    /// След идущего (или последнего законченного) забега, по отрезкам.
    func trail() async -> [[Coordinate]]
    /// Кольцо заявленной петли; `nil` — петля не из этого процесса.
    func ring(claimNo: Int) async -> LoopRing?
    /// Приблизительная геопозиция: попросить точную на время забега.
    func requestFullAccuracy() async -> Bool
}

/// Разрешение перед «Стартом» (PLAN.md, §6.6: «в момент надобности»; геопозиция — при «Старте», движение — сразу
/// после; одна кнопка «Продолжить», «Всегда» не просим).
enum RunStartPermission: Sendable {
    case location, motion
}

/// Разрешения глазами «Старта»: спрашивали ли, дали ли. За протоколом — в фикстурах и тестах системы нет.
@MainActor
protocol RunStartAccess: AnyObject {
    /// Разрешение ещё не спрашивали.
    func needsPrimer(_ permission: RunStartPermission) -> Bool
    /// Геопозиция запрещена — включить её можно только в Настройках.
    var locationDenied: Bool { get }
    /// Системный запрос.
    func request(_ permission: RunStartPermission) async
}

/// Голос, вибрация и Live Activity по событиям забега (PLAN.md, §6.9: на экране — вибрация, в кармане — голос).
@MainActor
protocol RunFeedback: AnyObject {
    func haptic(_ event: RunFeedbackEvent)
    func speak(_ text: String)
    /// Live Activity с оповещением (`AlertConfiguration`) — в кармане.
    func alert(title: String, body: String)
}

/// Событие для вибрации.
enum RunFeedbackEvent: Equatable, Sendable {
    /// «До замыкания» 50 м — `.increase`, 15 м — `.levelChange` (§6.9).
    case closure(thresholdMeters: Double)
    /// Петля замкнута.
    case loopClosed
    /// Решение сервера: засчитана или нет.
    case decided(applied: Bool)
    /// Разрыв следа (транспорт) — `.warning`.
    case warning
}

/// Настройки забега: голос и вибрация на сигналах (выключаемые). Хранятся в UserDefaults.
struct RunSettings: Equatable {
    var voice = true
    var haptics = true

    private static let voiceKey = "run.settings.voice"
    private static let hapticsKey = "run.settings.haptics"

    static func load(_ defaults: UserDefaults = .standard) -> RunSettings {
        var settings = RunSettings()
        if defaults.object(forKey: voiceKey) != nil { settings.voice = defaults.bool(forKey: voiceKey) }
        if defaults.object(forKey: hapticsKey) != nil { settings.haptics = defaults.bool(forKey: hapticsKey) }
        return settings
    }

    func save(_ defaults: UserDefaults = .standard) {
        defaults.set(voice, forKey: Self.voiceKey)
        defaults.set(haptics, forKey: Self.hapticsKey)
    }
}

/// Шаг листа «Старт»: лига, подсказка перед разрешением, запрет геопозиции.
enum RunStartStep: Equatable, Sendable {
    /// Выбор лиги и «Начать».
    case league
    /// Подсказка перед системным запросом.
    case primer(RunStartPermission)
    /// Геопозиция запрещена: только Настройки.
    case locationDenied
}

/// Временный контур заявленной петли на карте HUD — до итога сервера, отличим от настоящего участка (решение 25.09,
/// пункт 9): пунктирная кромка и бледная заливка; отказ — контур убирается.
struct LoopContour: Equatable, Identifiable, Sendable {
    var claimNo: Int
    var coordinates: [Coordinate]
    var id: Int { claimNo }
}

/// Экраны забега (PLAN.md, §5, экраны 6–8 и 10): «Старт» с выбором лиги и подсказками разрешений, модальный HUD,
/// плашка свёрнутого забега, церемония захвата в две фазы, список пропущенных, итог. Простые значения — их задают
/// снимки трекера и отчёты синхронизации (или фикстура); правила и тексты — в пакете Sync (`RunScreens.swift`).
@MainActor
@Observable
final class RunScreenModel {
    // MARK: Показ

    /// HUD открыт (полноэкранно). Закрыт при идущем забеге — «свёрнут»: плашка над таб-баром.
    var hudPresented = false
    /// Лист «Старт».
    var startSheetShown = false
    var startStep: RunStartStep = .league
    /// Выбранная лига: «Вело» — с Сезона 1 (PLAN.md, §5; D16), выбрать нельзя.
    var league: League = .run
    /// Идёт «Начать»: кнопка ждёт.
    private(set) var starting = false
    /// Забег начался, лист «Старт» закрывается — HUD откроется, когда он закроется (два окна сразу SwiftUI не покажет).
    @ObservationIgnored private var hudAfterSheet = false
    /// Почему не начался забег — текст на листе.
    private(set) var startError: String?
    /// «Финиш»: спросить подтверждение.
    var finishAsked = false
    private(set) var finishError: String?
    /// Настройки забега открыты.
    var settingsShown = false
    var settings: RunSettings {
        didSet { settings.save(defaults) }
    }
    /// Итог только что законченного забега — в том же полноэкранном слое, что HUD.
    var result: RunResultModel?
    /// Список пропущенных церемоний открыт.
    var missedShown = false
    /// Откуда HUD раскрывается zoom-переходом: «Старт» или плашка свёрнутого забега (`RunTransitionID`).
    private(set) var coverSource = "run.start"
    /// Приложение на переднем плане (`scenePhase`).
    private(set) var appActive = true

    // MARK: Данные

    private(set) var state = TrackerState()
    private(set) var readout: RunHUDReadout
    /// Кольцо вокруг стрелки, 0…1; `nil` — кольца нет.
    private(set) var ringProgress: Double?
    /// След по отрезкам — для карты.
    private(set) var trail: [[Coordinate]] = []
    /// Временные контуры петель этого забега.
    private(set) var contours: [LoopContour] = []
    /// Церемонии: играющая, очередь, пропущенные, тосты.
    private(set) var stage = CeremonyStage()
    /// Кольцо играющей церемонии.
    private(set) var ceremonyRing: [Coordinate] = []
    /// Куда смотрит телефон (компас), градусы от севера; `nil` — компаса нет.
    var heading: Double?
    /// Сигнал «до замыкания»: растёт на каждом — по нему кольцо пульсирует.
    private(set) var cuePulses = 0
    /// Решений по церемониям — для хаптики и галочки.
    private(set) var decisions = 0
    private(set) var sync = SyncHealth()
    /// Профиль — из него цвет игрока: «Старт», след, контур.
    let profile: ProfileModel?

    @ObservationIgnored private let driver: (any RunDriving)?
    @ObservationIgnored private let makeResult: ((UUID) -> RunResultModel)?
    @ObservationIgnored private let access: (any RunStartAccess)?
    @ObservationIgnored private let feedback: (any RunFeedback)?
    @ObservationIgnored private let defaults: UserDefaults
    @ObservationIgnored private let queueSurvivesRestart: Bool
    @ObservationIgnored private let clock: () -> Date
    @ObservationIgnored private var queue = CeremonyQueue()
    @ObservationIgnored private var cues = ClosureCues()
    @ObservationIgnored private var progress = ClosureProgress()
    @ObservationIgnored private var activeWarnings: Set<RunWarning.Kind> = []
    @ObservationIgnored private var trailTask: Task<Void, Never>?
    /// Подключение к трекеру и синхронизации (`RunController.onState`, `onReport`) — делает экран, когда оболочка на
    /// экране: модель, созданная и выброшенная при пересоздании вида, не должна перехватывать снимки.
    @ObservationIgnored var connect: (() -> Void)?

    /// - Parameter makeResult: итог законченного забега (живой — из очереди и истории); `nil` — итога нет.
    init(
        profile: ProfileModel? = nil, driver: (any RunDriving)? = nil, access: (any RunStartAccess)? = nil,
        feedback: (any RunFeedback)? = nil, makeResult: ((UUID) -> RunResultModel)? = nil,
        defaults: UserDefaults = .standard, queueSurvivesRestart: Bool = true, clock: @escaping () -> Date = { Date() }
    ) {
        self.profile = profile
        self.driver = driver
        self.makeResult = makeResult
        self.access = access
        self.feedback = feedback
        self.defaults = defaults
        self.queueSurvivesRestart = queueSurvivesRestart
        self.clock = clock
        settings = RunSettings.load(defaults)
        readout = RunHUDReadout(TrackerState(), now: clock().timeIntervalSince1970)
    }

    var isRunning: Bool { state.isRunning }
    var player: PlayerColor { profile?.playerColor ?? .blue }

    /// Полноэкранный слой забега открыт: HUD, а после конца забега — его итог (HUD «превращается» в итог, без второго
    /// окна поверх закрывающегося). Закрыли — забег сворачивается или итог закрывается.
    var coverShown: Bool {
        get { hudPresented || result != nil }
        set {
            guard !newValue else { return }
            hudPresented = false
            if !state.isRunning { result = nil }
        }
    }
    /// Церемонию можно играть: HUD открыт и приложение на экране. Иначе — в список пропущенных (решение 25.09, п. 8).
    var ceremonyVisible: Bool { hudPresented && appActive }
    /// Угол стрелки на экране: по компасу, без него — по курсу движения (решение 25.09, пункт 2).
    var arrowAngle: Double? {
        guard let readout = readout.closure.readout else { return nil }
        return RunHUD.arrowAngle(
            bearing: readout.bearingDegrees, heading: heading, course: RunHUD.course(of: trail.last ?? []))
    }
    /// Последняя точка следа — «я» на карте.
    var position: Coordinate? { trail.last?.last }

    // MARK: - «Старт»

    /// «Старт» на карте: забег идёт — открыть HUD; нет — лист с лигой.
    func startTapped() {
        coverSource = "run.start"
        if state.isRunning {
            hudPresented = true
            return
        }
        league = .run
        startStep = .league
        startError = nil
        startSheetShown = true
    }

    /// «Начать» на листе: сначала подсказки к разрешениям, которых ещё не спрашивали, потом — забег.
    func begin() async {
        guard !starting else { return }
        if let access {
            if access.needsPrimer(.location) {
                startStep = .primer(.location)
                return
            }
            if access.locationDenied {
                startStep = .locationDenied
                return
            }
            if access.needsPrimer(.motion) {
                startStep = .primer(.motion)
                return
            }
        }
        await launch()
    }

    /// «Продолжить» на подсказке: системный запрос, затем следующий шаг «Начать».
    func primerContinue() async {
        guard case .primer(let permission) = startStep, let access else { return }
        await access.request(permission)
        startStep = .league
        await begin()
    }

    private func launch(retried: Bool = false) async {
        guard let driver else {
            startError = "Забег начнётся после входа"
            return
        }
        starting = true
        defer { starting = false }
        do {
            try await driver.startRun(league: league)
            startError = nil
            stage.runEnded()
            contours = []
            result = nil
            coverSource = "run.start"
            if startSheetShown {
                hudAfterSheet = true
                startSheetShown = false
            } else {
                hudPresented = true
            }
        } catch RunController.StartProblem.reducedAccuracy where !retried {
            if await driver.requestFullAccuracy() {
                starting = false
                await launch(retried: true)
            } else {
                startError = "Нужна точная геопозиция: без неё точки не пройдут проверку"
            }
        } catch {
            startError = Self.startFailure(error)
            if error as? RunController.StartProblem == .locationDenied {
                startStep = .locationDenied
            }
        }
    }

    /// Лист «Старт» закрылся: забег начался — открыть HUD.
    func startSheetDismissed() {
        guard hudAfterSheet else { return }
        hudAfterSheet = false
        hudPresented = true
    }

    /// Текст отказа «Старта».
    static func startFailure(_ error: any Error) -> String {
        switch error {
        case RunController.StartProblem.notSignedIn: "Забег начнётся после входа"
        case RunController.StartProblem.locationNotDetermined, RunController.StartProblem.locationDenied:
            "Нужен доступ к геопозиции на время забега"
        case RunController.StartProblem.reducedAccuracy: "Нужна точная геопозиция: без неё точки не пройдут проверку"
        case TrackerError.alreadyRunning: "Сначала закончи пробный забег в «Лаборатории»"
        default: "Не получилось начать забег — попробуй ещё раз"
        }
    }

    // MARK: - HUD

    /// «Свернуть»: HUD уходит в плашку над таб-баром.
    func collapse() {
        hudPresented = false
    }

    /// Плашка свёрнутого забега: открыть HUD. Пропущенные за это время петли — списком, когда HUD появится
    /// (`hudAppeared`): два окна сразу SwiftUI не покажет.
    func expand() {
        coverSource = "run.accessory"
        hudPresented = true
    }

    /// HUD на экране — пропущенные петли списком.
    func hudAppeared() {
        showMissedIfAny()
    }

    /// «Финиш» (после подтверждения или удержания).
    func finish() async {
        finishAsked = false
        guard let driver else { return }
        let runId = state.runId
        do {
            try await driver.finish()
            finishError = nil
            ended(runId)
        } catch {
            finishError = "Не получилось закончить забег — попробуй ещё раз"
        }
    }

    /// «Продолжить забег» на карточке церемонии.
    func continueRun() {
        stage.finishPlaying()
        loadCeremonyRing()
    }

    /// Список пропущенных закрыт.
    func dismissMissed() {
        stage.dismissMissed()
        missedShown = false
    }

    func dismissToast(_ id: String) {
        stage.dismissToast(id)
    }

    /// Приложение ушло в фон или вернулось (`scenePhase`).
    func sceneChanged(active: Bool) {
        appActive = active
        if active, hudPresented {
            showMissedIfAny()
        }
    }

    private func showMissedIfAny() {
        if !stage.missed.isEmpty {
            missedShown = true
        }
    }

    // MARK: - Снимки трекера и отчёты синхронизации

    /// Новый снимок трекера (≈1 Гц).
    func receive(_ fresh: TrackerState) {
        let wasRunning = state.isRunning
        state = fresh
        refreshReadout()
        let hidden = !RunHUD.showsClosureHint(fresh)
        ringProgress = progress.update(fresh.closureHint, hidden: hidden)
        if fresh.isRunning, let cue = cues.update(fresh.closureHint, hidden: hidden) {
            cued(cue)
        }
        warningsChanged()
        if let runId = fresh.runId, fresh.isRunning {
            let loops = queue.newLoops(in: fresh)
            if !loops.isEmpty {
                let visible = ceremonyVisible
                let wasPlaying = stage.playing?.id
                stage.claimed(loops, runId: runId, visible: visible)
                announce(loops, runId: runId, visible: visible)
                if stage.playing?.id != wasPlaying {
                    loadCeremonyRing()
                }
                loadContours(loops.map(\.claimNo))
            }
        }
        if wasRunning, !fresh.isRunning {
            ended(fresh.runId)  // в том числе конец по пределу длины
        }
        loadTrail()
    }

    /// Забег закончился: церемоний больше нет, решения — только в итоге; HUD превращается в итог.
    private func ended(_ runId: UUID?) {
        stage.runEnded()
        hudPresented = false
        if let runId, result?.runId != runId, let makeResult {
            result = makeResult(runId)
        }
    }

    /// Отчёт прохода синхронизации: «сервер недоступен», вторая фаза церемоний, свежий итог.
    func receive(_ report: SyncReport) {
        sync.lastStop = report.stop
        refreshReadout()
        guard state.isRunning else {
            if report.settledClaims.contains(where: { $0.runId == result?.runId }) {
                Task { await result?.reload() }
            }
            return
        }
        let decided = stage.decided(queue.decisions(in: report))
        for item in decided {
            decisions += 1
            if item.decision?.applied == false {
                contours.removeAll { $0.claimNo == item.claimNo }
            }
            if settings.haptics, ceremonyVisible {
                feedback?.haptic(.decided(applied: item.decision?.applied == true))
            }
            if settings.voice, !ceremonyVisible {
                feedback?.speak(RunVoice.decided(item))
            }
        }
    }

    /// Раз в секунду — таймер повтора и «гаснущие» плашки.
    func tick() {
        refreshReadout()
    }

    /// Задать сразу (режим фикстур): снимок, след, контуры, церемония.
    func present(
        _ fresh: TrackerState, trail: [[Coordinate]], contours: [LoopContour] = [], ceremony: CeremonyItem? = nil,
        ring: [Coordinate] = [], missed: [ClaimedLoop] = []
    ) {
        state = fresh
        refreshReadout()
        ringProgress = progress.update(fresh.closureHint, hidden: !RunHUD.showsClosureHint(fresh))
        self.trail = trail
        self.contours = contours
        if let ceremony, let runId = fresh.runId {
            let loop = ClaimedLoop(
                claimNo: ceremony.claimNo,
                loop: LoopClaim(
                    startSeq: 0, endSeq: 0, closure: ceremony.closure, estimatedArea: ceremony.estimatedSquareMeters))
            stage.claimed([loop], runId: runId, visible: true)
            if let decision = ceremony.decision {
                stage.decided([decision])
            }
            ceremonyRing = ring
        }
        if let runId = fresh.runId, !missed.isEmpty {
            stage.claimed(missed, runId: runId, visible: false)
        }
    }

    private func refreshReadout() {
        readout = RunHUDReadout(
            state, now: clock().timeIntervalSince1970, sync: sync, queueSurvivesRestart: queueSurvivesRestart)
    }

    // MARK: - Сигналы

    private func cued(_ cue: ClosureCues.Cue) {
        cuePulses += 1
        if settings.haptics, ceremonyVisible {
            feedback?.haptic(.closure(thresholdMeters: cue.thresholdMeters))
        }
        if settings.voice, !ceremonyVisible {
            feedback?.speak(RunVoice.closure(cue))
        }
    }

    private func announce(_ loops: [ClaimedLoop], runId: UUID, visible: Bool) {
        for loop in loops {
            let item = CeremonyItem(runId: runId, loop: loop)
            if visible {
                if settings.haptics { feedback?.haptic(.loopClosed) }
            } else {
                // В кармане — Live Activity с оповещением и голос (§6.9); анимация — списком, когда экран включат.
                feedback?.alert(title: "Петля замкнута", body: CeremonyText.estimate(item.estimatedSquareMeters))
                if settings.voice { feedback?.speak(RunVoice.loopClosed(item)) }
            }
        }
    }

    /// Разрыв следа появился — вибрация на экране, голос в кармане (§6.9: «Транспорт, отказ»).
    private func warningsChanged() {
        let kinds = Set(readout.warnings.map(\.kind))
        defer { activeWarnings = kinds }
        guard state.isRunning, kinds.contains(.trackBroken), !activeWarnings.contains(.trackBroken) else { return }
        let issue = readout.warnings.first { $0.kind == .trackBroken }?.issue
        if settings.haptics, ceremonyVisible {
            feedback?.haptic(.warning)
        }
        if settings.voice, !ceremonyVisible {
            feedback?.speak(RunVoice.broken(issue))
        }
    }

    // MARK: - Координаты

    private func loadTrail() {
        guard let driver, trailTask == nil else { return }
        trailTask = Task {
            let fresh = await driver.trail()
            trail = fresh
            trailTask = nil
        }
    }

    private func loadCeremonyRing() {
        ceremonyRing = []
        guard let playing = stage.playing, let driver else { return }
        Task {
            let ring = await driver.ring(claimNo: playing.claimNo)
            if stage.playing?.id == playing.id {
                ceremonyRing = ring?.coordinates ?? []
            }
        }
    }

    private func loadContours(_ claims: [Int]) {
        guard let driver else { return }
        Task {
            for claimNo in claims {
                guard let ring = await driver.ring(claimNo: claimNo), ring.coordinates.count >= 3 else { continue }
                contours.removeAll { $0.claimNo == claimNo }
                contours.append(LoopContour(claimNo: claimNo, coordinates: ring.coordinates))
            }
        }
    }
}
