import Foundation
import GRDB
import Sync
import Synchronization

#if canImport(os)
    import os
#endif

/// Очередь синхронизации в базе приложения (`SyncStore` на GRDB): забеги, куски и заявки переживают выгрузку
/// приложения и перезапуск телефона — неотправленный забег уйдёт, когда появится сеть.
///
/// Каждая операция — одна транзакция. `updateRun` читает, меняет и записывает забег внутри одной записи в базу, поэтому
/// запись забега (`RunRecorder`) и синхронизация (`SyncEngine`), меняющие разные поля одновременно, не затирают друг друга.
/// Разрезание куска после 409 (`replaceChunk`) тоже атомарно: точки не теряются, даже если приложение выгрузят посередине.
/// Запечатанный кусок пишется вместе с прогрессом забега (`seal`): куска без прогресса в базе не бывает.
public struct GRDBSyncStore: SyncStore {
    private let writer: any DatabaseWriter

    public init(_ database: AppDatabase) {
        writer = database.writer
    }

    public func runs() async throws -> [LocalRun] {
        try await writer.read { db in
            Self.readable(try RunRow.order(Column("startedAtMs"), Column("id")).fetchAll(db))
        }
    }

    public func insert(_ run: LocalRun) async throws {
        let row = try RunRow(run)
        try await writer.write { db in try row.upsert(db) }
    }

    @discardableResult
    public func updateRun(_ id: UUID, _ change: @Sendable (inout LocalRun) -> Void) async throws -> LocalRun? {
        try await withoutActuallyEscaping(change) { change in
            try await writer.write { db in
                guard let row = try RunRow.fetchOne(db, key: id.uuidString) else { return nil }
                var run = try row.decoded()
                change(&run)
                try RunRow(run).update(db)
                return run
            }
        }
    }

    public func chunks(of runId: UUID) async throws -> [SealedChunk] {
        try await writer.read { db in
            Self.readable(
                try ChunkRow.filter(Column("runId") == runId.uuidString).order(Column("firstSeq")).fetchAll(db))
        }
    }

    public func save(_ chunk: SealedChunk) async throws {
        let row = try ChunkRow(chunk)
        try await writer.write { db in try row.upsert(db) }
    }

    public func seal(_ chunk: SealedChunk, progress: @Sendable (inout LocalRun) -> Void) async throws {
        let row = try ChunkRow(chunk)
        let runKey = chunk.runId.uuidString
        try await withoutActuallyEscaping(progress) { progress in
            try await writer.write { db in
                try row.upsert(db)
                guard let runRow = try RunRow.fetchOne(db, key: runKey) else { return }
                var run = try runRow.decoded()
                progress(&run)
                try RunRow(run).update(db)
            }
        }
    }

    public func replaceChunk(of runId: UUID, firstSeq: Int, with pieces: [SealedChunk]) async throws {
        let rows = try pieces.map(ChunkRow.init)
        try await writer.write { db in
            try ChunkRow.deleteOne(db, key: ChunkRow.key(runId, firstSeq))
            for row in rows {
                try row.upsert(db)
            }
        }
    }

    public func deleteChunk(of runId: UUID, firstSeq: Int) async throws {
        _ = try await writer.write { db in try ChunkRow.deleteOne(db, key: ChunkRow.key(runId, firstSeq)) }
    }

    public func deleteChunks(of runId: UUID) async throws {
        // По столбцу, а не по прочитанным кускам: так стирается и нечитаемая строка.
        _ = try await writer.write { db in try ChunkRow.filter(Column("runId") == runId.uuidString).deleteAll(db) }
    }

    public func claims(of runId: UUID) async throws -> [PendingClaim] {
        try await writer.read { db in
            Self.readable(
                try ClaimRow.filter(Column("runId") == runId.uuidString).order(Column("claimNo")).fetchAll(db))
        }
    }

    public func lastClaimNo(of runId: UUID) async throws -> Int? {
        // По столбцу, а не по прочитанным заявкам: номер нечитаемой тоже занят.
        try await writer.read { db in
            try Int.fetchOne(
                db, sql: "SELECT MAX(claimNo) FROM syncClaim WHERE runId = ?", arguments: [runId.uuidString])
        }
    }

    public func save(_ claim: PendingClaim) async throws {
        let row = try ClaimRow(claim)
        try await writer.write { db in try row.upsert(db) }
    }

    /// Записи, которые читаются. Нечитаемая (файл повреждён или модель изменилась несовместимо) пропускается: иначе одна
    /// строка роняла бы всю очередь — «Старт» (новичок ли игрок), продолжение забега и каждый проход синхронизации,
    /// навсегда. Её данные считаются потерянными: чтение строку не стирает, но и не доставит её никто. Нечитаемый кусок
    /// стирается вместе с остальными, когда забег подтверждён или отвергнут (`deleteChunks`); номер нечитаемой заявки
    /// новая не займёт (`lastClaimNo`).
    private static func readable<Row: QueueRow>(_ rows: [Row]) -> [Row.Value] {
        rows.compactMap { row in
            do {
                return try row.decoded()
            } catch {
                reportUnreadable(table: Row.databaseTableName, key: row.logKey, error)
                return nil
            }
        }
    }

    /// Нечитаемые строки, о которых уже написано в журнал (таблица и ключ). `chunks(of:)` за один проход зовётся много
    /// раз — без этого одна повреждённая строка заливала бы журнал одной и той же записью.
    private static let reportedUnreadable = Mutex<Set<String>>([])

    /// Строка в системный журнал о нечитаемой записи — одна за запуск приложения на каждую строку.
    /// - Returns: написано сейчас; `false` — об этой строке уже писали.
    @discardableResult
    static func reportUnreadable(table: String, key: String, _ error: any Error) -> Bool {
        guard reportedUnreadable.withLock({ $0.insert("\(table) \(key)").inserted }) else { return false }
        #if canImport(os)
            let reason = String(describing: error)
            Logger(subsystem: Bundle.main.bundleIdentifier ?? "Gorodki", category: "SyncStore").error(
                "Пропущена нечитаемая запись: \(table, privacy: .public) \(key, privacy: .public), \(reason, privacy: .private)"
            )
        #endif
        return true
    }
}

// MARK: - Строки таблиц

/// Строка очереди: запись модели и ключ — для журнала.
private protocol QueueRow: TableRecord {
    associatedtype Value
    var logKey: String { get }
    func decoded() throws -> Value
}

/// Запись очереди — JSON модели из GorodkiSync. Новое поле модели должно читаться из старого JSON (необязательное или
/// с миграцией, дописывающей значение): иначе очередь, записанная прошлой версией приложения, не прочитается.
/// Это проверяет тест на «JSON версии 1».
private enum Payload {
    static func encode(_ value: some Encodable) throws -> Data { try JSONEncoder().encode(value) }

    static func decode<T: Decodable>(_ type: T.Type, from data: Data) throws -> T {
        try JSONDecoder().decode(type, from: data)
    }
}

private struct RunRow: Codable, FetchableRecord, PersistableRecord, QueueRow {
    static let databaseTableName = "syncRun"

    var id: String
    var startedAtMs: Int64
    var payload: Data

    init(_ run: LocalRun) throws {
        id = run.id.uuidString
        startedAtMs = run.startedAtMs
        payload = try Payload.encode(run)
    }

    var logKey: String { id }

    func decoded() throws -> LocalRun { try Payload.decode(LocalRun.self, from: payload) }
}

private struct ChunkRow: Codable, FetchableRecord, PersistableRecord, QueueRow {
    static let databaseTableName = "syncChunk"

    var runId: String
    var firstSeq: Int
    var payload: Data

    init(_ chunk: SealedChunk) throws {
        runId = chunk.runId.uuidString
        firstSeq = chunk.firstSeq
        payload = try Payload.encode(chunk)
    }

    static func key(_ runId: UUID, _ firstSeq: Int) -> [String: any DatabaseValueConvertible] {
        ["runId": runId.uuidString, "firstSeq": firstSeq]
    }

    var logKey: String { "\(runId)/\(firstSeq)" }

    func decoded() throws -> SealedChunk { try Payload.decode(SealedChunk.self, from: payload) }
}

private struct ClaimRow: Codable, FetchableRecord, PersistableRecord, QueueRow {
    static let databaseTableName = "syncClaim"

    var runId: String
    var claimNo: Int
    var payload: Data

    init(_ claim: PendingClaim) throws {
        runId = claim.runId.uuidString
        claimNo = claim.claimNo
        payload = try Payload.encode(claim)
    }

    var logKey: String { "\(runId)/\(claimNo)" }

    func decoded() throws -> PendingClaim { try Payload.decode(PendingClaim.self, from: payload) }
}
