import Foundation

/// Что осталось в очереди игрока после прохода.
public struct SyncBacklog: Equatable, Sendable {
    /// Законченные на телефоне забеги, которые сервер ещё не подтвердил целиком.
    public var unfinishedDeliveries = 0
    /// Отправленные заявки петель без окончательного итога.
    public var unsettledClaims = 0

    public init(unfinishedDeliveries: Int = 0, unsettledClaims: Int = 0) {
        self.unfinishedDeliveries = unfinishedDeliveries
        self.unsettledClaims = unsettledClaims
    }

    /// Очередь игрока `ownerId` в хранилище.
    public static func of(_ store: any SyncStore, ownerId: String) async throws -> SyncBacklog {
        var backlog = SyncBacklog()
        for run in try await store.runs() where run.ownerId == ownerId && run.serverState != .rejected {
            if run.isFinishedLocally && !run.confirmedComplete {
                backlog.unfinishedDeliveries += 1
            }
            backlog.unsettledClaims += try await store.claims(of: run.id).filter { $0.sent && !$0.isSettled }.count
        }
        return backlog
    }
}

/// Когда синхронизировать снова.
public enum SyncWake: Equatable, Sendable {
    /// Ждать нечего — до следующего события (новый кусок, выход на передний план, сеть).
    case idle
    /// Повторить через столько.
    case after(Duration)
    /// Вход истёк (401) — до нового входа.
    case needsSignIn
    /// Сбиты часы или аккаунт удаляется — до действия игрока (повтор — при выходе приложения на передний план).
    case blocked(SyncStop)
}

/// Правило «когда повторить» по итогу прохода — чистая функция, без таймеров.
///
/// - Нет сети или сервер не ответил — повтор через 15 с, 30 с, 1 мин… до 15 минут (холодный старт сервера на Render Free
///   бывает дольше минуты, но и часами молчать нельзя). Слишком частые запросы — не раньше чем через минуту.
/// - Забег отложен (лимит забегов в сутки, часы) или исчерпан суточный объём — через час: такой забег остаётся
///   недоставленным, но повтор раньше получит тот же отказ.
/// - Сервер попросил дослать точки, забыл забег (404) или ещё не видит завершения — почти сразу.
/// - Недоставленный забег, о котором проход ничего не сказал, — через 15 минут: страховка, чтобы он не ждал до следующего
///   события.
/// - Заявки ждут итога, приложение открыто — опрос раз в 30 с: подсказка «заявка решена» по реальному времени обычно
///   приходит раньше, опрос — страховка.
public struct SyncBackoff: Sendable, Equatable {
    public static let first: Duration = .seconds(15)
    public static let longest: Duration = .seconds(900)
    public static let rateLimited: Duration = .seconds(60)
    public static let claimPoll: Duration = .seconds(30)
    public static let limitRetry: Duration = .seconds(3_600)

    /// Неудачных проходов подряд — пока шаг растёт: на потолке счёт останавливается.
    public private(set) var failures = 0

    public init() {}

    /// Событие, после которого ждать больше нечего (сеть вернулась, приложение открыто, вход выполнен): шаг сначала.
    public mutating func reset() { failures = 0 }

    public mutating func next(after report: SyncReport, backlog: SyncBacklog, appActive: Bool) -> SyncWake {
        switch report.stop {
        case .unauthorized:
            failures = 0
            return .needsSignIn
        case .clockInvalid:
            return .blocked(.clockInvalid)
        case .accountDeleting:
            return .blocked(.accountDeleting)
        case .offline:
            failed()
            return .after(backoff)
        case .rateLimited:
            failed()
            return .after(max(backoff, Self.rateLimited))
        case nil:
            failures = 0
        }

        // Когда продолжать доставку, говорит итог прохода, а не очередь: отложенный забег в ней тоже недоставленный,
        // и по одной очереди его повторяли бы каждые 15 с — до суток отказов 429 и 400.
        var waits: [Duration] = []
        if report.requeuedChunks > 0 || report.forgottenRuns > 0 || report.unconfirmedFinishes > 0 {
            waits.append(Self.first)  // число кругов ограничивает сам SyncEngine
        }
        if report.deferredRuns > 0 || report.storageLimitReached {
            waits.append(Self.limitRetry)
        }
        if waits.isEmpty && backlog.unfinishedDeliveries > 0 {
            waits.append(Self.longest)
        }
        if backlog.unsettledClaims > 0 && appActive {
            waits.append(Self.claimPoll)
        }
        return waits.min().map(SyncWake.after) ?? .idle
    }

    private mutating func failed() {
        if backoff < Self.longest {
            failures += 1
        }
    }

    /// Шаг после `failures` неудач: 15 с, вдвое больше за каждую следующую, не больше `longest`. Считается удвоением
    /// в `Duration`, а не через секунды: 15 · 2ⁿ с при переводе в `Duration` (Int128 аттосекунд) переполнялось
    /// на 65-й неудаче подряд и роняло приложение ещё до `min` — а неудачи копятся весь забег без связи, проход идёт
    /// на каждый запечатанный кусок.
    private var backoff: Duration {
        var wait = Self.first
        for _ in 1..<max(failures, 1) where wait < Self.longest {
            wait *= 2
        }
        return min(wait, Self.longest)
    }
}

/// Что попросить у iOS, чтобы дослать очередь, когда приложение в фоне (`BGTaskScheduler`).
public struct BackgroundRequest: Equatable, Sendable {
    public enum Kind: Equatable, Sendable {
        /// `BGAppRefreshTask`: короткое пробуждение (около 30 секунд), когда iOS сочтёт уместным.
        case refresh
        /// `BGProcessingTask` с сетью: несколько минут, обычно когда телефоном не пользуются.
        case upload
    }

    public var kind: Kind
    /// Не раньше чем через столько; iOS решает сама и может разбудить позже.
    public var earliest: Duration

    public init(_ kind: Kind, after earliest: Duration) {
        self.kind = kind
        self.earliest = earliest
    }
}

/// Правило «будить ли приложение в фоне» — чистая функция, без `BackgroundTasks`.
///
/// - Недоставленные забеги — оба пробуждения: короткое и длинное с сетью (длинное успевает дослать большой забег).
/// - Только заявки без итога — короткое: забрать итоги.
/// - Вход истёк, часы сбиты, аккаунт удаляется — ничего: без действия игрока проход бесполезен.
/// - Не раньше, чем решило расписание (`SyncWake.after`), и не чаще раза в 15 минут: раньше iOS всё равно не разбудит.
public enum BackgroundSyncPlan {
    public static let minimumDelay: Duration = .seconds(15 * 60)

    public static func requests(after wake: SyncWake, backlog: SyncBacklog) -> [BackgroundRequest] {
        let delay: Duration
        switch wake {
        case .needsSignIn, .blocked:
            return []
        case .idle:
            delay = minimumDelay
        case .after(let wait):
            delay = max(wait, minimumDelay)
        }
        var requests: [BackgroundRequest] = []
        if backlog.unfinishedDeliveries > 0 {
            requests.append(BackgroundRequest(.upload, after: delay))
        }
        if backlog.unfinishedDeliveries > 0 || backlog.unsettledClaims > 0 {
            requests.append(BackgroundRequest(.refresh, after: delay))
        }
        return requests
    }
}

/// Когда запускать синхронизацию (PLAN.md, §7.2: SyncEngine, фоновая выгрузка): по событиям приложения и по таймеру
/// из `SyncBackoff`. Проходы не накладываются: событие во время прохода запускает ещё один проход после него.
public actor SyncScheduler {
    /// Что случилось.
    public enum Reason: Sendable, Equatable {
        /// Приложение вышло на передний план.
        case appActive
        /// Сеть появилась.
        case networkRestored
        /// Игрок вошёл.
        case signedIn
        /// В очереди новое: кусок, заявка, завершение забега.
        case recorded
        /// Подсказка реального времени (итог заявки, изменение карты).
        case hint
        /// Сработал таймер повтора.
        case timer
        /// Приложение уходит в фон или iOS разбудила его фоновой задачей: время ограничено, шаг повторов сначала.
        /// Истёкший вход и сбитые часы не обходит — в отличие от выхода на передний план.
        case backgroundTask
    }

    private let engine: SyncEngine
    private let backlog: @Sendable () async -> SyncBacklog
    private let appActive: @Sendable () async -> Bool
    private let sleep: @Sendable (Duration) async throws -> Void
    private var backoff = SyncBackoff()
    private var wake: SyncWake = .idle
    private var lastBacklog = SyncBacklog()
    private var timer: Task<Void, Never>?
    private var running = false
    private var rerun = false
    /// Ждут конца идущего прохода (и следующего за ним, который учтёт их событие).
    private var waiters: [CheckedContinuation<Void, Never>] = []
    /// Был ли хоть один проход (или выход): до него очередь неизвестна.
    private var knowsBacklog = false
    /// Проверкам: событие встало ждать идущего прохода — гонку проверяют порядком событий, а не сном.
    private var waitingForPass: (@Sendable () -> Void)?

    /// Последнее решение «когда снова» — для экрана «Синхронизация» и проверок.
    public var nextWake: SyncWake { wake }

    /// Какие фоновые пробуждения попросить у iOS после последнего прохода (`BackgroundSyncPlan`). `nil` — прохода ещё
    /// не было и очередь неизвестна: прежние заявки трогать нельзя (пустой список их бы снял).
    public var backgroundRequests: [BackgroundRequest]? {
        knowsBacklog ? BackgroundSyncPlan.requests(after: wake, backlog: lastBacklog) : nil
    }

    public init(
        engine: SyncEngine,
        backlog: @escaping @Sendable () async -> SyncBacklog,
        appActive: @escaping @Sendable () async -> Bool,
        sleep: @escaping @Sendable (Duration) async throws -> Void = { try await Task.sleep(for: $0) }
    ) {
        self.engine = engine
        self.backlog = backlog
        self.appActive = appActive
        self.sleep = sleep
    }

    /// Событие. Возвращает, когда синхронизация решила проснуться снова (после прохода, если он был). Фоновая задача,
    /// пришедшая во время прохода, ждёт его и следующего (он учтёт её событие): пробуждения у iOS просятся по итогу
    /// прохода, а не по состоянию до него.
    @discardableResult
    public func trigger(_ reason: Reason) async -> SyncWake {
        switch reason {
        case .appActive, .networkRestored, .signedIn, .backgroundTask:
            backoff.reset()
        case .recorded, .hint, .timer:
            break
        }

        // Пока вход истёк, запросы бессмысленны — ждём входа (или выхода на передний план: вдруг токен обновился).
        if case .needsSignIn = wake, reason != .signedIn, reason != .appActive {
            return wake
        }
        if case .blocked = wake, reason != .appActive, reason != .signedIn {
            return wake
        }

        if running {
            rerun = true
            if reason == .backgroundTask {
                await withCheckedContinuation {
                    waiters.append($0)
                    waitingForPass?()
                }
            }
            return wake
        }

        running = true
        repeat {
            rerun = false
            let report = await engine.syncOnce()
            lastBacklog = await backlog()
            wake = backoff.next(after: report, backlog: lastBacklog, appActive: await appActive())
            knowsBacklog = true
        } while rerun
        running = false
        schedule(wake)
        let done = waiters
        waiters = []
        done.forEach { $0.resume() }
        return wake
    }

    /// Проверкам: `action` зовётся, когда событие встало ждать идущего прохода.
    func onWaitingForPass(_ action: @escaping @Sendable () -> Void) { waitingForPass = action }

    /// Остановить таймер (выход из аккаунта).
    public func stop() {
        timer?.cancel()
        timer = nil
        wake = .idle
        lastBacklog = SyncBacklog()
        knowsBacklog = true
    }

    private func schedule(_ wake: SyncWake) {
        timer?.cancel()
        guard case .after(let delay) = wake else {
            timer = nil
            return
        }
        let sleep = self.sleep
        timer = Task { [weak self] in
            do {
                try await sleep(delay)
            } catch {
                return  // таймер отменён
            }
            // Проход — в своей задаче: новый таймер отменит эту, а отмена не должна оборвать чтение очереди и запросы.
            guard let self else { return }
            Task { await self.trigger(.timer) }
        }
    }
}
