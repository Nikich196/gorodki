import Foundation
import Synchronization

/// Итог одного прохода синхронизации — для экрана «Резервная копия» (пункт 2 листика): когда телефон последний раз
/// говорил с сервером и чем это кончилось.
public struct SyncPassRecord: Codable, Equatable, Sendable {
    public enum Outcome: String, Codable, Sendable {
        case ok, offline, unauthorized, clockInvalid, accountDeleting, rateLimited
    }

    /// Когда закончился проход, мс Unix.
    public var atMs: Int64
    public var outcome: Outcome
    /// Сколько кусков точек и завершённых забегов ушло за проход.
    public var uploadedChunks: Int
    public var finishedRuns: Int

    public init(atMs: Int64, outcome: Outcome, uploadedChunks: Int = 0, finishedRuns: Int = 0) {
        self.atMs = atMs
        self.outcome = outcome
        self.uploadedChunks = uploadedChunks
        self.finishedRuns = finishedRuns
    }

    public init(_ report: SyncReport, atMs: Int64) {
        let outcome: Outcome =
            switch report.stop {
            case nil: .ok
            case .offline: .offline
            case .unauthorized: .unauthorized
            case .clockInvalid: .clockInvalid
            case .accountDeleting: .accountDeleting
            case .rateLimited: .rateLimited
            }
        self.init(
            atMs: atMs, outcome: outcome, uploadedChunks: report.uploadedChunks, finishedRuns: report.finishedRuns)
    }

    /// Чем кончился проход — для игрока, без рода.
    public var summary: String {
        switch outcome {
        case .ok: "Синхронизировано"
        case .offline: "Нет связи с сервером — телефон повторит сам"
        case .unauthorized: "Вход истёк — нужно войти заново"
        case .clockInvalid: "Часы телефона сбиты — включи автоматическое время"
        case .accountDeleting: "Аккаунт удаляется — данные больше не отправляются"
        case .rateLimited: "Сервер попросил подождать — повтор через минуту"
        }
    }
}

/// Последний проход синхронизации и последний удачный — переживают перезапуск: хранилище даёт приложение
/// (UserDefaults), в проверках — память. Пишет расписание (`SyncScheduler`, отчёт каждого прохода), читает экран.
public final class SyncLog: Sendable {
    public struct State: Codable, Equatable, Sendable {
        public var last: SyncPassRecord?
        /// Последний проход без остановки, мс Unix.
        public var lastSuccessAtMs: Int64?

        public init(last: SyncPassRecord? = nil, lastSuccessAtMs: Int64? = nil) {
            self.last = last
            self.lastSuccessAtMs = lastSuccessAtMs
        }
    }

    private let save: @Sendable (Data?) -> Void
    private let state: Mutex<State>

    /// - Parameters:
    ///   - load: сохранённое состояние (JSON); `nil` или нечитаемое — проходов не было.
    ///   - save: сохранить состояние; `nil` — стереть.
    public init(load: @Sendable () -> Data?, save: @escaping @Sendable (Data?) -> Void) {
        self.save = save
        let saved = load().flatMap { try? JSONDecoder().decode(State.self, from: $0) }
        state = Mutex(saved ?? State())
    }

    /// Журнал в памяти — для проверок и режима фикстур.
    public static func inMemory(_ initial: State = State()) -> SyncLog {
        let data = try? JSONEncoder().encode(initial)
        return SyncLog(load: { data }, save: { _ in })
    }

    public var current: State { state.withLock { $0 } }

    /// Записать отчёт прохода, законченного в `atMs`.
    public func record(_ report: SyncReport, atMs: Int64) {
        let record = SyncPassRecord(report, atMs: atMs)
        let updated = state.withLock { state -> State in
            state.last = record
            if record.outcome == .ok {
                state.lastSuccessAtMs = atMs
            }
            return state
        }
        save(try? JSONEncoder().encode(updated))
    }

    /// Забыть всё — выход из аккаунта (`wipeLocalData`): время чужой синхронизации новому игроку не нужно.
    public func clear() {
        state.withLock { $0 = State() }
        save(nil)
    }
}
