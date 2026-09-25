import Foundation
import GameCore

// Что показывают экраны забега — HUD, церемония, Live Activity, итог (docs/architecture/run-hud.md, «Что нужно
// экрану»). Здесь — готовые значения и тексты из снимка трекера и сохранённого итога: модели экранов в приложении
// только собирают их, а правила и формулировки проверяются тестами на Linux. Тексты — без рода (PLAN.md, §3.17), числа —
// `NumberText` (§6.10). Множественные формы («3 участка») — в приложении: они в каталоге строк (`CountText`).

// MARK: - «До замыкания»

/// «До замыкания» для экрана: состояние подсказки (`ClosureHint`) с числом, шагом экрана и видимостью.
public enum ClosureDisplay: Equatable, Sendable {
    /// Спрятано: нет разрешения «Движение» — сервер откажет каждой петле (решение 25.09, пункт 10).
    case hidden
    /// Следа ещё нет: ждём первую принятую точку.
    case waitingForTrail
    /// Путь от начала петли короче минимума: осталось столько, м (шаг экрана).
    case needsPath(meters: Int)
    /// Прямая или «туда-обратно»: петля к началу почти без площади — нужно свернуть. Стрелки назад нет.
    case needsTurn
    /// Можно замкнуть.
    case canClose(ClosureReadout)

    public init(_ hint: ClosureHint, hidden: Bool) {
        if hidden {
            self = .hidden
            return
        }
        switch hint {
        case .noTrail:
            self = .waitingForTrail
        case .needsPath(_, let remaining):
            self = .needsPath(meters: RunHUD.displayMeters(remaining))
        case .needsTurn:
            self = .needsTurn
        case .canClose(let target):
            self = .canClose(ClosureReadout(target))
        }
    }

    /// Подпись над числом.
    public var caption: String {
        switch self {
        case .hidden: "Захваты выключены"
        case .needsPath: "До петли"
        case .waitingForTrail, .needsTurn, .canClose: "До замыкания"
        }
    }

    /// Число метров (главная цифра HUD); `nil` — вместо числа слово (`headline`).
    public var meters: Int? {
        switch self {
        case .needsPath(let meters): meters
        case .canClose(let readout): readout.meters
        case .hidden, .waitingForTrail, .needsTurn: nil
        }
    }

    /// Крупное слово вместо числа.
    public var headline: String? {
        switch self {
        case .hidden: "Только туман"
        case .waitingForTrail: "Ждём GPS…"
        case .needsTurn: "Сверни"
        case .needsPath, .canClose: nil
        }
    }

    /// Строка под числом: что делать дальше.
    public func hint(limits: CaptureAreaLimits = CaptureAreaLimits()) -> String {
        switch self {
        case .hidden:
            return "Без доступа к «Движению и фитнесу» туман открывается, а петли сервер не засчитает"
        case .waitingForTrail:
            return "Петля начнётся с первой точки следа"
        case .needsPath:
            return "Пробеги ещё — потом петлю можно замкнуть"
        case .needsTurn:
            return "Туда-обратно площади не даёт — поверни к началу петли"
        case .canClose(let readout):
            if readout.tooLarge {
                let km2 = NumberText.decimal(limits.maxAreaSquareMeters / 1_000_000, fractionDigits: 1)
                return "Петля больше \(km2)\(NumberText.unitSeparator)км² — такие не засчитываются"
            }
            if readout.belowServerMinimum {
                let minimum = NumberText.hectares(fromSquareMeters: limits.minAreaSquareMeters, fractionDigits: 2)
                return "Пока петля меньше \(minimum) — сервер её не засчитает"
            }
            return "Замкни петлю — будет " + CeremonyText.estimate(readout.estimatedSquareMeters)
        }
    }

    /// Сигналы 50/15 м и стрелка — только когда можно замкнуть.
    public var readout: ClosureReadout? {
        if case .canClose(let readout) = self { readout } else { nil }
    }
}

/// «Можно замкнуть» для экрана.
public struct ClosureReadout: Equatable, Sendable {
    /// Метры с шагом экрана (`RunHUD.displayMeters`).
    public var meters: Int
    /// Азимут на цель, градусы от севера.
    public var bearingDegrees: Double
    /// Точка-цель — для карты.
    public var target: Coordinate
    /// Оценка площади петли, если замкнуть сейчас, м².
    public var estimatedSquareMeters: Double
    public var belowServerMinimum: Bool
    public var tooLarge: Bool

    public init(_ target: ClosureTarget) {
        meters = RunHUD.displayMeters(target.distanceMeters)
        bearingDegrees = target.bearingDegrees
        self.target = target.coordinate
        estimatedSquareMeters = target.estimatedAreaSquareMeters
        belowServerMinimum = target.belowServerMinimum
        tooLarge = target.tooLarge
    }
}

/// Кольцо вокруг стрелки: сколько пройдено к цели, 0…1. «Нужен путь» — доля пути от наибольшего остатка, «можно
/// замкнуть» — насколько ближе к цели, чем в самой дальней точке с этой целью. Новая цель или смена состояния
/// начинают кольцо заново. Кольцо — только вид: правил в нём нет.
public struct ClosureProgress: Equatable, Sendable {
    private enum Phase: Equatable, Sendable {
        case path, close
    }

    private var target: Int?
    private var phase: Phase?
    private var farthest = 0.0

    public init() {}

    /// Доля для кольца; `nil` — кольца нет (нет следа, «сверни», спрятано).
    public mutating func update(_ hint: ClosureHint, hidden: Bool = false) -> Double? {
        let current: (phase: Phase, remaining: Double)?
        switch hint {
        case .needsPath(_, let remaining): current = (.path, remaining)
        case .canClose(let target): current = (.close, target.distanceMeters)
        case .noTrail, .needsTurn: current = nil
        }
        guard !hidden, let current else {
            phase = nil
            return nil
        }
        if hint.targetSeq != target || current.phase != phase {
            target = hint.targetSeq
            phase = current.phase
            farthest = 0
        }
        farthest = max(farthest, current.remaining)
        guard farthest > 0 else { return 1 }
        return min(1, max(0, 1 - current.remaining / farthest))
    }
}

// MARK: - Стрелка

extension RunHUD {
    /// Курс по следу берётся не короче стольких метров: ближе — шум GPS.
    public static let courseMinMeters = 10.0

    /// Направление движения по последним точкам отрезка следа, градусы от севера: от последней точки, отстоящей не
    /// меньше чем на `minMeters`, к последней. `nil` — стоим или след короче.
    public static func course(of trail: [Coordinate], minMeters: Double = courseMinMeters) -> Double? {
        guard let last = trail.last else { return nil }
        let plane = LocalTangentPlane(origin: last)
        for point in trail.dropLast().reversed() {
            let offset = plane.project(point)
            let distance = (offset.east * offset.east + offset.north * offset.north).squareRoot()
            guard distance >= minMeters else { continue }
            // Смещение — от последней точки к прежней; курс — обратно.
            return normalizedDegrees(atan2(-offset.east, -offset.north) * 180 / .pi)
        }
        return nil
    }

    /// Угол стрелки на экране, градусы по часовой от «вверх»: азимут цели минус куда смотрит телефон (компас,
    /// решение 25.09, пункт 2), без компаса — минус курс движения. `nil` — ни того ни другого: стрелку не показывать.
    public static func arrowAngle(bearing: Double, heading: Double?, course: Double?) -> Double? {
        guard let reference = heading ?? course else { return nil }
        return normalizedDegrees(bearing - reference)
    }

    static func normalizedDegrees(_ degrees: Double) -> Double {
        let value = degrees.truncatingRemainder(dividingBy: 360)
        return value < 0 ? value + 360 : value
    }
}

// MARK: - HUD целиком

/// HUD забега одним значением: три метрики, «до замыкания», туман, плашки. Строится на каждом снимке трекера (≈1 Гц)
/// и раз в секунду для таймера.
public struct RunHUDReadout: Equatable, Sendable {
    /// Время от начала, с (у демо-повтора — по времени точек).
    public var elapsedSeconds: Double
    /// «17:42».
    public var elapsedText: String
    /// «3,21» — километры без единицы.
    public var distanceText: String
    /// «5:32» — средний темп без единицы; меньше `RunHUD.paceMinDistanceMeters` — «–:––».
    public var paceText: String
    public var closure: ClosureDisplay
    /// «+0,12 га тумана»; туман за всё время известен не везде — «≥ +0,12 га тумана» (решение 25.09, пункт 5).
    public var fogText: String
    public var fogIsLowerBound: Bool
    /// Плашки по порядку показа.
    public var warnings: [RunWarning]
    /// Петли, заявленные за забег.
    public var loops: Int
    public var isReplay: Bool

    /// Темпа ещё нет.
    public static let noPace = "–:––"

    /// - Parameter now: «сейчас», секунды Unix.
    public init(
        _ state: TrackerState, now: Double, sync: SyncHealth = SyncHealth(), queueSurvivesRestart: Bool = true
    ) {
        elapsedSeconds = RunHUD.elapsedSeconds(state, now: now)
        elapsedText = NumberText.clock(seconds: elapsedSeconds)
        distanceText = NumberText.decimal(state.stats.distanceMeters / 1_000, fractionDigits: 2)
        paceText =
            RunHUD.averagePaceSecondsPerKilometer(state, now: now).map { NumberText.pace(secondsPerKilometer: $0) }
            ?? Self.noPace
        closure = ClosureDisplay(state.closureHint, hidden: !RunHUD.showsClosureHint(state))
        fogIsLowerBound = state.stats.fogNewIsLowerBound
        fogText = CeremonyText.fogGain(state.stats.fogNewSquareMeters, lowerBound: fogIsLowerBound) + " тумана"
        let clock = state.isReplay ? Double(state.stats.lastPointAtMs ?? state.startedAtMs ?? 0) / 1_000 : now
        warnings = RunHUD.warnings(state, now: clock, sync: sync, queueSurvivesRestart: queueSurvivesRestart)
        loops = state.stats.loops
        isReplay = state.isReplay
    }
}

// MARK: - Плашки

/// Текст плашки-предупреждения: заголовок и строка «что делать».
public struct WarningText: Equatable, Sendable {
    public var title: String
    public var detail: String
    /// Мешает засчитать захват или сохранить забег — плашка ярче остальных.
    public var isSevere: Bool
}

extension RunWarning {
    public var text: WarningText {
        switch kind {
        case .storageFailed:
            WarningText(
                title: "Нет места для записи", detail: "Освободи место на телефоне — иначе точки забега пропадут",
                isSevere: true)
        case .queueNotDurable:
            WarningText(
                title: "Забег не переживёт перезапуск",
                detail: "Хранилище приложения не открылось — не закрывай приложение до «Финиша»", isSevere: true)
        case .motionMissing:
            WarningText(
                title: "Захваты не засчитаются",
                detail: "Нет доступа к «Движению и фитнесу» — туман откроется, петли сервер не примет", isSevere: true)
        case .signInNeeded:
            WarningText(
                title: "Нужно войти", detail: "Забег записывается и уйдёт на сервер после входа", isSevere: false)
        case .clockInvalid:
            WarningText(
                title: "Сбиты часы телефона", detail: "Включи «Автоматически» в настройках даты и времени",
                isSevere: true)
        case .accountDeleting:
            WarningText(title: "Аккаунт удаляется", detail: "Забег не уйдёт на сервер", isSevere: true)
        case .trackBroken:
            WarningText(
                title: RefusalText.breakTitle(issue), detail: RefusalText.breakDetail(issue), isSevere: true)
        case .weakGps:
            WarningText(title: "Слабый GPS", detail: "Держись открытых улиц — замыкание подождёт", isSevere: false)
        case .resumed:
            WarningText(
                title: "Приложение перезапускалось",
                detail: "Петля, начатая до перезапуска, не заявится — начни новую", isSevere: false)
        case .serverUnavailable:
            WarningText(
                title: "Сервер недоступен", detail: "Забег записывается — «подтверждено» придёт, когда будет связь",
                isSevere: false)
        }
    }
}

// MARK: - Отказы (решение 25.09, пункт 14)

/// Почему петля не засчитана — по коду сервера или отказа в заявке. Сбои и устаревание — одной фразой
/// (`RunHUD.refusalDetail` — `.brief`), остальное — с причиной (§3.2: «игроку объясняется причина»).
public enum RefusalText {
    /// Одна фраза для сбоев.
    public static let brief = "Не удалось засчитать"

    /// Причина отказа для игрока.
    public static func reason(_ code: String) -> String {
        if RunHUD.refusalDetail(code) == .brief {
            return brief
        }
        if code.hasPrefix("segment_broken:") {
            let issue = TrackIssue(rawValue: String(code.dropFirst("segment_broken:".count)))
            return "След прервался: " + breakReason(issue)
        }
        switch code {
        case "too_few_points": return "В петле слишком мало точек"
        case "too_short": return "Путь петли короче 150 м"
        case "not_closed": return "Петля не замкнулась — концы следа слишком далеко"
        case "too_small": return "Петля меньше 0,25 га"
        case "too_narrow": return "Петля слишком узкая"
        case "too_large": return "Петля больше 3,5 км² — такие не засчитываются"
        case "empty": return "Внутри петли не осталось земли для захвата"
        case "motion_not_authorized": return "Нет доступа к «Движению и фитнесу»"
        case "daily_limit": return "Дневной лимит захватов исчерпан"
        case "device_shared": return "С этого телефона сегодня уже засчитан захват другого аккаунта"
        case "account_frozen": return "Аккаунт заморожен — захваты не применяются"
        case "points_not_received": return "Точки петли не дошли до сервера"
        case "sensors_not_received": return "Данные датчиков не дошли до сервера"
        case "claim_invalid": return "Заявка петли не прошла проверку"
        case "claim_conflict": return "Эта петля уже заявлена"
        case "upload_window_closed": return "Петля отправлена слишком поздно"
        case "claim_limit": return "Слишком много заявок — лимит"
        case PendingClaim.runRejectedCode: return "Забег не принят сервером"
        case "rejected": return "Сервер не засчитал петлю"
        default: return "Сервер не засчитал петлю"
        }
    }

    /// Разрыв следа — заголовок плашки. Транспорт — «захват на паузе» (PLAN.md, §11).
    public static func breakTitle(_ issue: TrackIssue?) -> String {
        switch issue {
        case .vehicle, .tooFast: "Похоже на транспорт — захват на паузе"
        case .cycling: "Похоже на велосипед"
        default: "След прервался: " + breakReason(issue)
        }
    }

    /// Разрыв следа — что это значит.
    public static func breakDetail(_ issue: TrackIssue?) -> String {
        switch issue {
        case .cycling: "В лиге «Бег» велосипед не засчитывается, вело-лига — с Сезона 1. Петля начнётся заново"
        case .vehicle, .tooFast: "Петля начнётся заново, когда движение снова станет бегом или шагом"
        default: "Петля начнётся заново с этого места"
        }
    }

    /// Причина разрыва одним словом-фразой (итог забега, «след прервался: …»).
    public static func breakReason(_ issue: TrackIssue?) -> String {
        switch issue {
        case .vehicle: "транспорт"
        case .tooFast: "слишком быстро для бега"
        case .cycling: "велосипед"
        case .teleport: "скачок GPS"
        case .noSteps: "нет шагов"
        case .strideOutOfRange: "шаги не похожи на бег"
        case .carLaunch: "разгон как у машины"
        case .poorAccuracy: "слабый GPS"
        case .staleFix: "устаревшие точки GPS"
        case .timeWentBackwards: "часы GPS пошли назад"
        case nil: "причина неизвестна"
        }
    }
}

// MARK: - Церемония (решение 25.09, пункты 6–8)

/// Числа церемонии и тумана.
public enum CeremonyText {
    /// «≈ +1,2 га» — оценка телефона, одна цифра после запятой (макет церемонии).
    public static func estimate(_ squareMeters: Double) -> String {
        "≈" + NumberText.unitSeparator + "+" + NumberText.hectares(fromSquareMeters: squareMeters, fractionDigits: 1)
    }

    /// «+1,25 га» — итог сервера, две цифры.
    public static func taken(_ squareMeters: Double) -> String {
        "+" + NumberText.hectares(fromSquareMeters: squareMeters, fractionDigits: 2)
    }

    /// «+0,12 га» или «≥ +0,12 га».
    public static func fogGain(_ squareMeters: Double, lowerBound: Bool) -> String {
        let value = "+" + NumberText.hectares(fromSquareMeters: squareMeters, fractionDigits: 2)
        return lowerBound ? "≥" + NumberText.unitSeparator + value : value
    }

    /// Пока сервер не решил.
    public static let checking = "проверяем петлю на сервере…"
    /// Засчитана — дальше «· 12 480 м² · 125 соток» (сотки — в приложении, `CountText`).
    public static let confirmed = "подтверждено"
    /// Не засчитана — дальше причина.
    public static let refused = "не засчитана"
}

/// Церемония одной петли на экране: первая фаза — оценка телефона, вторая — решение сервера (`decision`).
public struct CeremonyItem: Equatable, Sendable, Identifiable {
    public var runId: UUID
    public var claimNo: Int
    /// Оценка площади телефоном, м² — «≈ +1,2 га».
    public var estimatedSquareMeters: Double
    public var closure: LoopClosure
    /// Решение сервера; `nil` — ещё «проверяем».
    public var decision: CeremonyDecision?

    public var id: String { "\(runId.uuidString)#\(claimNo)" }

    public init(runId: UUID, loop: ClaimedLoop) {
        self.runId = runId
        claimNo = loop.claimNo
        estimatedSquareMeters = loop.loop.estimatedArea
        closure = loop.loop.closure
    }

    /// «Петля 2» — номер заявки с единицы.
    public var title: String { "Петля \(claimNo + 1)" }

    /// «проверяем…», «подтверждено» или «не засчитана · причина».
    public var status: String {
        guard let decision else { return CeremonyText.checking }
        if decision.applied { return CeremonyText.confirmed }
        return CeremonyText.refused + " · " + RefusalText.reason(decision.refusalCode ?? "rejected")
    }
}

/// Где играются церемонии (решение 25.09, пункты 6 и 8). Экран забега виден — первая фаза сразу, две петли подряд —
/// по очереди; не виден (в кармане или свёрнут) — петля идёт в список пропущенных, его показывают, когда экран снова
/// виден. Вторая фаза без повторной анимации: обновляет играющую, ждущую или пропущенную церемонию, а если её карточка
/// уже закрыта — короткой строкой («тост»). Решение пришло после конца забега — не церемония: итог обновится сам.
public struct CeremonyStage: Equatable, Sendable {
    /// Играет сейчас.
    public private(set) var playing: CeremonyItem?
    /// Ждут своей очереди — петли подряд.
    public private(set) var upcoming: [CeremonyItem] = []
    /// Пропущенные, пока экран не был виден, — списком при возвращении.
    public private(set) var missed: [CeremonyItem] = []
    /// Решения по уже закрытым церемониям — короткой строкой, когда экран виден.
    public private(set) var toasts: [CeremonyItem] = []
    /// Закрытые церемонии: вторая фаза для них — тост.
    private var closed: [String: CeremonyItem] = [:]

    public init() {}

    /// Новые петли (`CeremonyQueue.newLoops`).
    /// - Parameter visible: экран забега виден и приложение на переднем плане.
    public mutating func claimed(_ loops: [ClaimedLoop], runId: UUID, visible: Bool) {
        for loop in loops {
            let item = CeremonyItem(runId: runId, loop: loop)
            if !visible {
                missed.append(item)
            } else if playing == nil {
                playing = item
            } else {
                upcoming.append(item)
            }
        }
    }

    /// Вторая фаза (`CeremonyQueue.decisions`). Возвращает решённые церемонии — для хаптики и голоса.
    @discardableResult
    public mutating func decided(_ decisions: [CeremonyDecision]) -> [CeremonyItem] {
        var updated: [CeremonyItem] = []
        for decision in decisions {
            let id = CeremonyItem.id(decision.runId, decision.claimNo)
            if playing?.id == id {
                playing?.decision = decision
                updated += playing.map { [$0] } ?? []
            } else if let index = upcoming.firstIndex(where: { $0.id == id }) {
                upcoming[index].decision = decision
                updated.append(upcoming[index])
            } else if let index = missed.firstIndex(where: { $0.id == id }) {
                missed[index].decision = decision
                updated.append(missed[index])
            } else if var item = closed[id] {
                item.decision = decision
                closed[id] = item
                toasts.append(item)
                updated.append(item)
            }
        }
        return updated
    }

    /// «Продолжить забег»: играющая закрывается, следующая из очереди — играет.
    public mutating func finishPlaying() {
        if let playing {
            closed[playing.id] = playing
        }
        playing = upcoming.isEmpty ? nil : upcoming.removeFirst()
    }

    /// Список пропущенных показан и закрыт.
    public mutating func dismissMissed() {
        for item in missed {
            closed[item.id] = item
        }
        missed = []
    }

    /// Тост показан.
    public mutating func dismissToast(_ id: String) {
        toasts.removeAll { $0.id == id }
    }

    /// Забег закончился: церемонии больше не играются, решения — только в итоге.
    public mutating func runEnded() {
        self = CeremonyStage()
    }
}

extension CeremonyItem {
    static func id(_ runId: UUID, _ claimNo: Int) -> String { "\(runId.uuidString)#\(claimNo)" }
}

// MARK: - Live Activity

/// Live Activity забега — те же числа, что HUD, строками; «до замыкания» без стрелки (решение 25.09, пункт 2). Как
/// часто обновлять (не чаще раза в 5 с) — решает приложение.
public enum RunActivityText {
    /// Первая строка: разрыв следа (транспорт — главное, что нужно знать в кармане), иначе «до замыкания».
    public static func title(_ readout: RunHUDReadout) -> String {
        if let broken = readout.warnings.first(where: { $0.kind == .trackBroken }) {
            return broken.text.title
        }
        if let meters = readout.closure.meters {
            return readout.closure.caption + " " + NumberText.meters(meters)
        }
        return readout.closure.headline.map { "Забег · " + $0 } ?? "Забег"
    }

    /// Вторая строка: дистанция, темп, туман.
    public static func detail(_ readout: RunHUDReadout) -> String {
        var parts = [readout.distanceText + NumberText.unitSeparator + "км"]
        if readout.paceText != RunHUDReadout.noPace {
            parts.append(readout.paceText + NumberText.unitSeparator + NumberText.paceUnit)
        }
        parts.append(readout.fogText)
        return parts.joined(separator: " · ")
    }
}

// MARK: - Голос (PLAN.md, §6.9)

/// Что сказать голосом — в кармане и по желанию на экране. Тексты — без рода (§3.17).
public enum RunVoice {
    /// Порог «до замыкания» 50 или 15 м.
    public static func closure(_ cue: ClosureCues.Cue) -> String {
        "До замыкания \(Int(cue.thresholdMeters)) метров"
    }

    /// Петля замкнута (первая фаза).
    public static func loopClosed(_ item: CeremonyItem) -> String {
        "Петля замкнута. Примерно "
            + NumberText.decimal(AreaUnits.hectares(fromSquareMeters: item.estimatedSquareMeters), fractionDigits: 1)
            + " гектара"
    }

    /// Решение сервера (вторая фаза).
    public static func decided(_ item: CeremonyItem) -> String {
        guard let decision = item.decision else { return "" }
        return decision.applied ? "\(item.title) подтверждена" : "\(item.title) не засчитана"
    }

    /// Разрыв следа: транспорт и другие причины.
    public static func broken(_ issue: TrackIssue?) -> String {
        RefusalText.breakTitle(issue)
    }
}

// MARK: - Итог забега

/// Итог забега для экранов «Итог» и «Детали» (docs/architecture/run-hud.md, «Итог забега»): что показать и в каком
/// состоянии каждое число — оценка телефона («≈»), итог сервера или «позже».
public struct RunResultReadout: Equatable, Sendable {
    /// Строка «название — значение».
    public struct Row: Equatable, Sendable, Identifiable {
        public var title: String
        public var value: String
        public var id: String { title }
    }

    /// Заявка петли в итоге.
    public struct ClaimRow: Equatable, Sendable, Identifiable {
        public enum State: Equatable, Sendable {
            case waiting, applied, refused
        }

        public var claimNo: Int
        /// «Петля 1».
        public var title: String
        /// «≈ +1,2 га».
        public var estimate: String
        /// «подтверждено · +1,25 га», «не засчитана · причина», «проверяется…», «ждёт сети».
        public var status: String
        public var state: State
        /// «Взятое», м² — для «подтверждено · 12 480 м²» в приложении.
        public var takenSquareMeters: Double?
        public var id: Int { claimNo }
    }

    public var runId: UUID
    /// «Бег» — лига.
    public var leagueTitle: String
    public var isReplay: Bool
    public var endedAtLimit: Bool
    /// «3,21».
    public var distanceText: String
    /// «17:42».
    public var durationText: String
    /// «5:32» или «–:––».
    public var paceText: String
    /// Петель заявлено.
    public var loops: Int
    /// «≈ +1,2 га» — сумма оценок петель телефоном.
    public var estimateText: String
    /// «+1,25 га» — «взятое» по засчитанным; `nil` — сервер ещё не решил ни одной.
    public var takenText: String?
    /// «≈ +0,12 га» или «≥ +0,12 га» тумана — замороженная оценка.
    public var fogEstimateText: String
    /// «+0,10 га» тумана сервера; `nil` — «позже».
    public var fogServerText: String?
    /// Визиты — участков освежено; `nil` — «позже» (не раньше 20 минут после конца забега).
    public var visitedParcels: Int?
    public var claims: [ClaimRow]
    /// Площадь по видам — только после границы публичности; до неё — `nil` и строка «позже».
    public var breakdown: [Row]?
    /// Часть засчитанных петель ещё без разбивки — «остальное позже».
    public var breakdownPending: Bool
    /// Разрывы следа: причина — сколько раз.
    public var breaks: [Row]
    /// Забег отвергнут сервером — вместо площадей.
    public var rejection: String?
    public var readiness: RunResult.Readiness
    /// «ждёт сети», «сервер считает», «готово».
    public var readinessText: String

    /// Слово вместо числа, которого ещё нет.
    public static let later = "позже"

    public init(_ result: RunResult) {
        runId = result.runId
        leagueTitle = Self.leagueTitle(result.league)
        isReplay = result.isReplay
        endedAtLimit = result.endedAtLimit
        distanceText = NumberText.decimal(result.distanceMeters / 1_000, fractionDigits: 2)
        durationText = NumberText.clock(seconds: result.durationSeconds)
        paceText =
            result.distanceMeters >= RunHUD.paceMinDistanceMeters
            ? NumberText.pace(secondsPerKilometer: result.durationSeconds / (result.distanceMeters / 1_000))
            : RunHUDReadout.noPace
        loops = result.claims.count
        estimateText = CeremonyText.estimate(result.estimatedLoopSquareMeters)
        let decided = result.claims.contains { $0.outcome?.status == "applied" }
        takenText = decided ? CeremonyText.taken(result.takenSquareMeters) : nil
        fogEstimateText = CeremonyText.fogGain(
            result.fogEstimateSquareMeters, lowerBound: result.fogEstimateIsLowerBound)
        fogServerText = result.fogNewSquareMeters.map { CeremonyText.fogGain($0, lowerBound: false) }
        visitedParcels = result.visitedParcels
        claims = result.claims.map { Self.claimRow($0, readiness: result.readiness) }
        breakdown = result.areaByOutcome.map { byOutcome in
            byOutcome.filter { $0.value > 0 }.sorted { Self.outcomeOrder($0.key) < Self.outcomeOrder($1.key) }.map {
                Row(title: Self.outcomeTitle($0.key), value: NumberText.squareMeters($0.value))
            }
        }
        breakdownPending = result.areaByOutcomePending
        breaks = result.breaks.filter { $0.value > 0 }.sorted { $0.key < $1.key }.map {
            Row(title: RefusalText.breakReason(TrackIssue(rawValue: $0.key)), value: "×\($0.value)")
        }
        rejection = result.rejectCode.map(Self.rejectionText)
        readiness = result.readiness
        readinessText =
            switch result.readiness {
            case .waitingForNetwork: "ждёт сети — отправится само"
            case .computing: "сервер считает"
            case .ready: "итог готов"
            }
    }

    public static func leagueTitle(_ league: League) -> String {
        switch league {
        case .run: "Бег"
        case .bike: "Вело"
        }
    }

    static func claimRow(_ claim: RunResult.Claim, readiness: RunResult.Readiness) -> ClaimRow {
        let title = "Петля \(claim.claimNo + 1)"
        let estimate = CeremonyText.estimate(claim.estimatedSquareMeters)
        if let outcome = claim.outcome, outcome.status == "applied" {
            return ClaimRow(
                claimNo: claim.claimNo, title: title, estimate: estimate,
                status: CeremonyText.confirmed + " · " + CeremonyText.taken(outcome.areaSquareMeters),
                state: .applied, takenSquareMeters: outcome.areaSquareMeters)
        }
        if claim.isSettled {
            let code = claim.refusedCode ?? claim.outcome?.rejectCode ?? claim.outcome?.status ?? "rejected"
            return ClaimRow(
                claimNo: claim.claimNo, title: title, estimate: estimate,
                status: CeremonyText.refused + " · " + RefusalText.reason(code), state: .refused,
                takenSquareMeters: nil)
        }
        let waiting = readiness == .waitingForNetwork ? "ждёт сети" : "проверяется…"
        return ClaimRow(
            claimNo: claim.claimNo, title: title, estimate: estimate, status: waiting, state: .waiting,
            takenSquareMeters: nil)
    }

    /// Виды площади (`areaByOutcome`, camelCase имён `PieceOutcome`) — по-русски; незнакомый ключ — как есть.
    public static func outcomeTitle(_ key: String) -> String {
        switch key {
        case "claimedNeutral": "Ничья земля"
        case "transferred": "Перешла от соперника"
        case "cracked": "Трещина у соперника"
        case "refreshed": "Своя освежена"
        case "refreshedForClanMate": "Освежена земля клана"
        case "shielded": "Под щитом"
        case "lossLimited": "Лимит снятия уровней"
        case "superseded": "Владелец был здесь позже"
        case "newAccountLimited": "Новый аккаунт чужие уровни не снимает"
        case "contested": "Спорная зона"
        default: key
        }
    }

    static func outcomeOrder(_ key: String) -> Int {
        let order = [
            "claimedNeutral", "transferred", "cracked", "refreshed", "refreshedForClanMate", "contested", "shielded",
            "lossLimited", "superseded", "newAccountLimited",
        ]
        return order.firstIndex(of: key) ?? order.count
    }

    static func rejectionText(_ code: String) -> String {
        switch code {
        case "replay_forbidden": "Забег не принят: повтор записи доступен только демо-аккаунтам"
        case "run_invalid": "Забег не принят: сервер нашёл в нём ошибку"
        default: "Забег не принят сервером"
        }
    }
}
