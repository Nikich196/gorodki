import Foundation

/// Хранилище очереди синхронизации: забеги, куски, заявки. В приложении — GRDB (переживает перезапуск и сбой);
/// в тестах — память. Куски хранятся, пока сервер не подтвердит, что у него все точки забега.
///
/// Запись забега и синхронизация — разные акторы и пишут в хранилище одновременно, поэтому забег меняется только
/// через `updateRun` (атомарно, в GRDB — одной транзакцией): каждый трогает свои поля и не затирает чужие.
public protocol SyncStore: Sendable {
    func runs() async throws -> [LocalRun]
    /// Новый забег в очереди.
    func insert(_ run: LocalRun) async throws
    /// Меняет забег на месте и возвращает новое состояние (`nil` — такого забега нет).
    @discardableResult
    func updateRun(_ id: UUID, _ change: @Sendable (inout LocalRun) -> Void) async throws -> LocalRun?

    /// Куски забега по возрастанию `firstSeq`.
    func chunks(of runId: UUID) async throws -> [SealedChunk]
    /// Сохраняет кусок (ключ — забег и номер первой точки).
    func save(_ chunk: SealedChunk) async throws
    /// Запечатанный кусок и прогресс записи его забега (`RunRecorder`) — одной операцией, в GRDB — одной транзакцией.
    /// Выгрузка приложения между двумя записями оставила бы кусок без прогресса: прерванный забег закрылся бы с последним
    /// номером меньше, чем в куске, и сервер отказал бы в завершении (`last_seq_too_small`).
    func seal(_ chunk: SealedChunk, progress: @Sendable (inout LocalRun) -> Void) async throws
    /// Заменяет кусок другими одной транзакцией (разрезание после 409): точки не теряются, если приложение выгрузят.
    func replaceChunk(of runId: UUID, firstSeq: Int, with pieces: [SealedChunk]) async throws
    func deleteChunk(of runId: UUID, firstSeq: Int) async throws
    /// Стирает все куски забега — и те, что `chunks(of:)` не вернула: в GRDB нечитаемая строка при чтении пропускается,
    /// и стирание по прочитанному оставило бы её в базе навсегда.
    func deleteChunks(of runId: UUID) async throws

    /// Заявки забега по возрастанию номера.
    func claims(of runId: UUID) async throws -> [PendingClaim]
    /// Наибольший номер заявки забега (`nil` — заявок нет), считая и те, что `claims(of:)` не вернула: номер нечитаемой
    /// заявки мог уже уйти на сервер — новая заявка под ним получила бы окончательный отказ (`claim_conflict`), а в базе
    /// молча заменила бы нечитаемую.
    func lastClaimNo(of runId: UUID) async throws -> Int?
    /// Сохраняет заявку (ключ — забег и номер заявки).
    func save(_ claim: PendingClaim) async throws

    /// Стирает забег с его кусками и заявками (и нечитаемыми тоже) одной операцией: пробные забеги «Лаборатории»
    /// (`removeLabRuns`). Такого забега нет — не ошибка.
    func removeRun(_ id: UUID) async throws

    /// Стирает всю очередь — забеги, куски, заявки, и нечитаемые тоже: выход из аккаунта и его удаление.
    func removeAll() async throws
}

/// Очередь в памяти — для тестов и превью.
public actor InMemorySyncStore: SyncStore {
    private var storedRuns: [UUID: LocalRun] = [:]
    private var storedChunks: [UUID: [Int: SealedChunk]] = [:]
    private var storedClaims: [UUID: [Int: PendingClaim]] = [:]

    public init() {}

    public func runs() -> [LocalRun] { storedRuns.values.sorted { $0.startedAtMs < $1.startedAtMs } }

    public func insert(_ run: LocalRun) { storedRuns[run.id] = run }

    @discardableResult
    public func updateRun(_ id: UUID, _ change: @Sendable (inout LocalRun) -> Void) -> LocalRun? {
        guard var run = storedRuns[id] else { return nil }
        change(&run)
        storedRuns[id] = run
        return run
    }

    public func chunks(of runId: UUID) -> [SealedChunk] {
        (storedChunks[runId] ?? [:]).values.sorted { $0.firstSeq < $1.firstSeq }
    }

    public func save(_ chunk: SealedChunk) { storedChunks[chunk.runId, default: [:]][chunk.firstSeq] = chunk }

    public func seal(_ chunk: SealedChunk, progress: @Sendable (inout LocalRun) -> Void) {
        save(chunk)
        updateRun(chunk.runId, progress)
    }

    public func replaceChunk(of runId: UUID, firstSeq: Int, with pieces: [SealedChunk]) {
        storedChunks[runId]?[firstSeq] = nil
        for piece in pieces {
            save(piece)
        }
    }

    public func deleteChunk(of runId: UUID, firstSeq: Int) { storedChunks[runId]?[firstSeq] = nil }

    public func deleteChunks(of runId: UUID) { storedChunks[runId] = nil }

    public func claims(of runId: UUID) -> [PendingClaim] {
        (storedClaims[runId] ?? [:]).values.sorted { $0.claimNo < $1.claimNo }
    }

    public func lastClaimNo(of runId: UUID) -> Int? { storedClaims[runId]?.keys.max() }

    public func save(_ claim: PendingClaim) { storedClaims[claim.runId, default: [:]][claim.claimNo] = claim }

    public func removeRun(_ id: UUID) {
        storedRuns[id] = nil
        storedChunks[id] = nil
        storedClaims[id] = nil
    }

    public func removeAll() {
        storedRuns = [:]
        storedChunks = [:]
        storedClaims = [:]
    }
}
