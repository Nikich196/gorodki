import Foundation
import GameCore

/// Логика HUD забега без экрана (docs/architecture/run-hud.md): чистые функции от снимка трекера (`TrackerState`),
/// которые модель экрана в приложении только собирает, — правила здесь, на Linux, под тестами.
///
/// Числа, которых нет в плане, — константы приложения, а не игровой конфиг: сервер их не проверяет (решение 25.09,
/// пункт 11). **Их значения предварительные — выбираются по полевому тесту №1.**
public enum RunHUD {
    /// Пороги сигналов «до замыкания», м (PLAN.md, §6.9: «До замыкания 50/15 м»).
    public static let closureCueThresholdsMeters: [Double] = [50, 15]
    /// Порог сигнала перевзводится, когда расстояние снова выросло выше порога на столько, м (`hud.closureCueHysteresisMeters`).
    /// Значение — по полевому тесту №1 (предварительно).
    public static let closureCueHysteresisMeters = 10.0
    /// «След прервался» держится после первой принятой точки ещё столько, с (`hud.warningHoldSeconds`).
    /// Значение — по полевому тесту №1 (предварительно).
    public static let warningHoldSeconds = 10.0
    /// «Слабый GPS» — если принятых точек нет столько, с (`hud.weakGpsSeconds`). Значение — по полевому тесту №1
    /// (предварительно).
    public static let weakGpsSeconds = 10.0
    /// Темп показывается с этой дистанции, м: раньше он — шум первых точек. Значение — по полевому тесту №1
    /// (предварительно).
    public static let paceMinDistanceMeters = 100.0
    /// Шаг округления «до замыкания» на экране, м. Значение — по полевому тесту №1 (предварительно).
    public static let closureDisplayStepMeters = 5.0

    // MARK: - Три метрики: время, дистанция, средний темп (решение 25.09, пункт 3)

    /// Время от начала забега, с. Живой забег — по часам: GPS может молчать, а время идёт (экран может взять
    /// `Text(timerInterval:)` от `startedAtMs`). Демо-повтор — по времени последней обработанной точки: начало повтора
    /// сдвинуто в прошлое на всю запись (sync.md), и часы с первой секунды показали бы всю её длину.
    public static func elapsedSeconds(_ state: TrackerState, now: Double) -> Double {
        guard let startedAtMs = state.startedAtMs else { return 0 }
        let untilMs =
            state.isReplay ? (state.stats.lastPointAtMs ?? startedAtMs) : StoragePrecision.milliseconds(now)
        return max(0, Double(untilMs - startedAtMs) / 1_000)
    }

    /// Средний темп за забег, с на км: время / дистанция (решение 25.09 — средний, без нового параметра окна). `nil` —
    /// дистанция меньше `paceMinDistanceMeters`.
    public static func averagePaceSecondsPerKilometer(_ state: TrackerState, now: Double) -> Double? {
        let distance = state.stats.distanceMeters
        guard distance >= paceMinDistanceMeters else { return nil }
        return elapsedSeconds(state, now: now) / (distance / 1_000)
    }

    // MARK: - «До замыкания»

    /// Показывать ли «до замыкания» и давать ли сигналы: без разрешения «Движение» — нет (решение 25.09, пункт 10):
    /// сервер откажет каждой петле, число только обманывало бы, а плашка объясняет почему.
    public static func showsClosureHint(_ state: TrackerState) -> Bool { !state.capturesNeedMotion }

    /// «До замыкания» для экрана: вверх до шага `closureDisplayStepMeters` — чтобы «0 м» не появлялось раньше замыкания.
    public static func displayMeters(_ distance: Double) -> Int {
        Int((max(0, distance) / closureDisplayStepMeters).rounded(.up) * closureDisplayStepMeters)
    }
}

/// Сигналы «до замыкания» 50 и 15 м (PLAN.md, §6.9): событие при пересечении порога вниз, одно на порог. Дрожание GPS
/// у порога сигналов не повторяет: порог перевзводится, только когда расстояние снова выросло выше порога на
/// `RunHUD.closureCueHysteresisMeters`. Новая цель (заявка, разрыв, перезапуск) перевзводит оба порога. Без «можно
/// замкнуть» и без разрешения «Движение» сигналов нет. Канал (экран или голос) выбирает приложение.
public struct ClosureCues: Equatable, Sendable {
    /// Пересечён порог, м (из `RunHUD.closureCueThresholdsMeters`).
    public struct Cue: Equatable, Sendable {
        public var thresholdMeters: Double
    }

    private var target: Int?
    /// Пороги, сигнал которых уже был и ещё не перевзведён.
    private var fired: Set<Double> = []

    public init() {}

    /// Подсказка для очередной точки. Если за одну точку пересечены оба порога — один сигнал, ближний.
    /// - Parameter hidden: «до замыкания» спрятано (`RunHUD.showsClosureHint` — `false`).
    public mutating func update(_ hint: ClosureHint, hidden: Bool = false) -> Cue? {
        if hint.targetSeq != target {
            target = hint.targetSeq
            fired = []
        }
        guard !hidden, case .canClose(let closure) = hint else { return nil }
        let distance = closure.distanceMeters
        fired = fired.filter { distance <= $0 + RunHUD.closureCueHysteresisMeters }
        let crossed = RunHUD.closureCueThresholdsMeters.filter { distance <= $0 && !fired.contains($0) }
        guard let nearest = crossed.min() else { return nil }
        fired.formUnion(RunHUD.closureCueThresholdsMeters.filter { distance <= $0 })
        return Cue(thresholdMeters: nearest)
    }
}

// MARK: - Плашки-предупреждения

/// Состояние синхронизации для плашек: чем кончился последний проход и что решило расписание.
public struct SyncHealth: Equatable, Sendable {
    /// Почему остановился последний проход (`SyncReport.stop`); `nil` — прошёл.
    public var lastStop: SyncStop?
    /// Когда синхронизировать снова (`SyncScheduler.nextWake`).
    public var wake: SyncWake

    public init(lastStop: SyncStop? = nil, wake: SyncWake = .idle) {
        self.lastStop = lastStop
        self.wake = wake
    }
}

/// Активное предупреждение HUD: вид и с какого момента (мс, если известно). Сколько показывать и как — дизайн.
public struct RunWarning: Equatable, Sendable {
    /// Порядок видов — предлагаемый порядок показа (docs/architecture/run-hud.md: запись → «Движение» → вход и часы →
    /// разрыв → GPS → сервер); это предложение, не правило плана.
    public enum Kind: Int, CaseIterable, Comparable, Sendable {
        /// Нет места для записи (`storageFailed`) — гаснет после записанной точки.
        case storageFailed
        /// Очередь не переживёт перезапуск (`AppDependencies.queueSurvivesRestart == false`).
        case queueNotDurable
        /// Захваты не засчитаются: нет разрешения «Движение» — до конца забега.
        case motionMissing
        /// Нужно войти (`SyncWake.needsSignIn`): заявки не уйдут, пока игрок не войдёт.
        case signInNeeded
        /// Сбиты часы (`SyncWake.blocked(.clockInvalid)`).
        case clockInvalid
        /// Аккаунт удаляется — не гаснет.
        case accountDeleting
        /// След прервался (причина — `issue`): держится, пока после разрыва не пошли принятые точки, и ещё
        /// `RunHUD.warningHoldSeconds`.
        case trackBroken
        /// Слабый GPS: точки отбрасываются по точности или устарели, принятых нет `RunHUD.weakGpsSeconds`.
        case weakGps
        /// Приложение перезапускалось: подсказка начала заново — пока снова не «можно замкнуть».
        case resumed
        /// Сервер недоступен: проход кончился `offline` или `rateLimited` — до следующего удачного прохода.
        case serverUnavailable

        public static func < (a: Kind, b: Kind) -> Bool { a.rawValue < b.rawValue }
    }

    public var kind: Kind
    /// Причина разрыва — для `trackBroken` (коды те же, что `segment_broken:<причина>` у сервера).
    public var issue: TrackIssue?
    public var sinceMs: Int64?

    public init(_ kind: Kind, issue: TrackIssue? = nil, sinceMs: Int64? = nil) {
        self.kind = kind
        self.issue = issue
        self.sinceMs = sinceMs
    }
}

extension RunHUD {
    /// Активные предупреждения по порядку показа.
    /// - Parameters:
    ///   - now: «сейчас», секунды Unix (у повтора — время его последней точки).
    ///   - queueSurvivesRestart: очередь в базе, а не в памяти (`AppDependencies.queueSurvivesRestart`).
    public static func warnings(
        _ state: TrackerState, now: Double, sync: SyncHealth = SyncHealth(), queueSurvivesRestart: Bool = true
    ) -> [RunWarning] {
        let nowMs = StoragePrecision.milliseconds(now)
        let stats = state.stats
        var result: [RunWarning] = []
        if state.storageFailed {
            result.append(RunWarning(.storageFailed))
        }
        if !queueSurvivesRestart {
            result.append(RunWarning(.queueNotDurable))
        }
        if state.capturesNeedMotion {
            result.append(RunWarning(.motionMissing, sinceMs: state.startedAtMs))
        }
        if sync.wake == .needsSignIn || sync.lastStop == .unauthorized {
            result.append(RunWarning(.signInNeeded))
        }
        if sync.wake == .blocked(.clockInvalid) || sync.lastStop == .clockInvalid {
            result.append(RunWarning(.clockInvalid))
        }
        if sync.wake == .blocked(.accountDeleting) || sync.lastStop == .accountDeleting {
            result.append(RunWarning(.accountDeleting))
        }
        if let issue = stats.lastBreak {
            let holdMs = Int64(warningHoldSeconds * 1_000)
            if stats.acceptedAfterBreakAtMs.map({ nowMs - $0 < holdMs }) ?? true {
                result.append(RunWarning(.trackBroken, issue: issue, sinceMs: stats.lastBreakAtMs))
            }
        }
        if let issue = stats.lastIssue, issue == .poorAccuracy || issue == .staleFix,
            let issueMs = stats.lastIssueAtMs, issueMs > stats.lastAcceptedAtMs ?? .min
        {
            let quietSinceMs = stats.lastAcceptedAtMs ?? state.startedAtMs ?? issueMs
            if nowMs - quietSinceMs >= Int64(weakGpsSeconds * 1_000) {
                result.append(RunWarning(.weakGps, issue: issue, sinceMs: quietSinceMs))
            }
        }
        if state.resumedAfterRestart {
            result.append(RunWarning(.resumed))
        }
        if sync.lastStop == .offline || sync.lastStop == .rateLimited {
            result.append(RunWarning(.serverUnavailable))
        }
        return result.sorted { $0.kind < $1.kind }
    }
}

// MARK: - Церемония захвата в две фазы (решение 25.09, пункт 6)

/// Вторая фаза церемонии: решение сервера по заявке. Только статус и «взятое» — разбивки по видам здесь нет: до границы
/// публичности она выдала бы чужие скрытые захваты (docs/architecture/run-hud.md, «Приватность итога заявки»).
public struct CeremonyDecision: Equatable, Sendable {
    public var runId: UUID
    public var claimNo: Int
    /// Засчитана (`applied`).
    public var applied: Bool
    /// «Взятое», м²: земля, ставшая твоей после петли (её и так видно на своей карте).
    public var takenSquareMeters: Double
    /// Почему не засчитана: код итога (`too_small`, `segment_broken:vehicle`…), статус без кода (`stale`, `failed`) или
    /// отказ в самой заявке. Текст к коду — `RunHUD.refusalDetail`.
    public var refusalCode: String?
}

/// Очередь церемоний. Первая фаза — «петля замкнута»: сразу, на телефоне, один раз на `(runId, claimNo)` — по номеру
/// заявки, а не по `lastEvent` (две заявки между снимками экрана — две церемонии). Вторая фаза — «решение сервера»: из
/// отчёта прохода синхронизации, только если первая была в этом процессе (заявка, сделанная до перезапуска приложения,
/// обновляет только итог). Сыгранное помнится в памяти: после перезапуска старые петли не повторяются.
///
/// В кармане приложение объявляет новые петли Live Activity и голосом, а пропущенные анимации показывает списком, когда
/// экран включили (решение 25.09, пункт 8). Решение, пришедшее после конца забега, — не церемония, а обновление итога:
/// это решает приложение по `TrackerState.isRunning`.
public struct CeremonyQueue: Sendable {
    private struct Key: Hashable, Sendable {
        var runId: UUID
        var claimNo: Int
    }

    private var shown: Set<Key> = []

    public init() {}

    /// Петли, для которых первой фазы ещё не было, — по порядку заявок; каждая отдаётся один раз.
    public mutating func newLoops(in state: TrackerState) -> [ClaimedLoop] {
        guard let runId = state.runId else { return [] }
        return state.claimedLoops.filter { shown.insert(Key(runId: runId, claimNo: $0.claimNo)).inserted }
    }

    /// Решения для второй фазы из отчёта прохода: только заявки с первой фазой в этом процессе, каждая один раз
    /// (отчёт и так называет решённую заявку ровно в одном проходе).
    public func decisions(in report: SyncReport) -> [CeremonyDecision] {
        report.settledClaims.filter { shown.contains(Key(runId: $0.runId, claimNo: $0.claimNo)) }.map { settled in
            let applied = settled.outcome?.status == "applied"
            return CeremonyDecision(
                runId: settled.runId, claimNo: settled.claimNo, applied: applied,
                takenSquareMeters: applied ? settled.outcome?.areaSquareMeters ?? 0 : 0,
                refusalCode: applied
                    ? nil : settled.refusedCode ?? settled.outcome?.rejectCode ?? settled.outcome?.status)
        }
    }
}

extension RunHUD {
    /// Насколько подробно объяснять отказ (решение 25.09, пункт 14).
    public enum RefusalDetail: Equatable, Sendable {
        /// Одной фразой («не удалось засчитать»).
        case brief
        /// С причиной (§3.2: «игроку объясняется причина»): форма, лимиты, «Движение», разрыв следа, отказ в заявке.
        case withReason
    }

    /// Коды, которые объясняются одной фразой: сбой или устаревание, а не действие игрока.
    public static let briefRefusalCodes: Set<String> = [
        "stale", "failed", "engine_failed", "too_many_attempts", "run_not_found",
    ]

    public static func refusalDetail(_ code: String) -> RefusalDetail {
        briefRefusalCodes.contains(code) ? .brief : .withReason
    }
}
