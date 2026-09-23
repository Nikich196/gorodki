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
/// - Забег отложен (лимит забегов в сутки, часы) или исчерпан суточный объём — через час.
/// - Сервер попросил дослать точки — почти сразу.
/// - Заявки ждут итога, приложение открыто — опрос раз в 30 с: подсказка «заявка решена» по реальному времени обычно
///   приходит раньше, опрос — страховка.
public struct SyncBackoff: Sendable, Equatable {
    public static let first: Duration = .seconds(15)
    public static let longest: Duration = .seconds(900)
    public static let rateLimited: Duration = .seconds(60)
    public static let claimPoll: Duration = .seconds(30)
    public static let limitRetry: Duration = .seconds(3_600)

    /// Неудачных проходов подряд.
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
            failures += 1
            return .after(backoff)
        case .rateLimited:
            failures += 1
            return .after(max(backoff, Self.rateLimited))
        case nil:
            failures = 0
        }

        var waits: [Duration] = []
        if report.requeuedChunks > 0 || backlog.unfinishedDeliveries > 0 {
            waits.append(Self.first)  // досылка; число кругов ограничивает сам SyncEngine
        }
        if report.deferredRuns > 0 || report.storageLimitReached {
            waits.append(Self.limitRetry)
        }
        if backlog.unsettledClaims > 0 && appActive {
            waits.append(Self.claimPoll)
        }
        return waits.min().map(SyncWake.after) ?? .idle
    }

    private var backoff: Duration {
        let seconds = 15.0 * pow(2.0, Double(max(failures - 1, 0)))
        return min(.seconds(seconds), Self.longest)
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
    }

    private let engine: SyncEngine
    private let backlog: @Sendable () async -> SyncBacklog
    private let appActive: @Sendable () async -> Bool
    private let sleep: @Sendable (Duration) async throws -> Void
    private var backoff = SyncBackoff()
    private var wake: SyncWake = .idle
    private var timer: Task<Void, Never>?
    private var running = false
    private var rerun = false

    /// Последнее решение «когда снова» — для экрана «Синхронизация» и проверок.
    public var nextWake: SyncWake { wake }

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

    /// Событие. Возвращает, когда синхронизация решила проснуться снова (после прохода, если он был).
    @discardableResult
    public func trigger(_ reason: Reason) async -> SyncWake {
        switch reason {
        case .appActive, .networkRestored, .signedIn:
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
            return wake
        }

        running = true
        defer { running = false }
        repeat {
            rerun = false
            let report = await engine.syncOnce()
            wake = backoff.next(after: report, backlog: await backlog(), appActive: await appActive())
        } while rerun
        schedule(wake)
        return wake
    }

    /// Остановить таймер (выход из аккаунта).
    public func stop() {
        timer?.cancel()
        timer = nil
        wake = .idle
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
