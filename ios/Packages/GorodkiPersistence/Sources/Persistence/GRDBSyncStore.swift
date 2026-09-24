import Foundation
import GRDB
import Sync

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
            try RunRow.order(Column("startedAtMs"), Column("id")).fetchAll(db).map { try $0.decoded() }
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
            try ChunkRow.filter(Column("runId") == runId.uuidString).order(Column("firstSeq")).fetchAll(db)
                .map { try $0.decoded() }
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

    public func claims(of runId: UUID) async throws -> [PendingClaim] {
        try await writer.read { db in
            try ClaimRow.filter(Column("runId") == runId.uuidString).order(Column("claimNo")).fetchAll(db)
                .map { try $0.decoded() }
        }
    }

    public func save(_ claim: PendingClaim) async throws {
        let row = try ClaimRow(claim)
        try await writer.write { db in try row.upsert(db) }
    }
}

// MARK: - Строки таблиц

/// Запись очереди — JSON модели из GorodkiSync. Новое поле модели должно читаться из старого JSON (необязательное или
/// с миграцией, дописывающей значение): иначе очередь, записанная прошлой версией приложения, не прочитается.
/// Это проверяет тест на «JSON версии 1».
private enum Payload {
    static func encode(_ value: some Encodable) throws -> Data { try JSONEncoder().encode(value) }

    static func decode<T: Decodable>(_ type: T.Type, from data: Data) throws -> T {
        try JSONDecoder().decode(type, from: data)
    }
}

private struct RunRow: Codable, FetchableRecord, PersistableRecord {
    static let databaseTableName = "syncRun"

    var id: String
    var startedAtMs: Int64
    var payload: Data

    init(_ run: LocalRun) throws {
        id = run.id.uuidString
        startedAtMs = run.startedAtMs
        payload = try Payload.encode(run)
    }

    func decoded() throws -> LocalRun { try Payload.decode(LocalRun.self, from: payload) }
}

private struct ChunkRow: Codable, FetchableRecord, PersistableRecord {
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

    func decoded() throws -> SealedChunk { try Payload.decode(SealedChunk.self, from: payload) }
}

private struct ClaimRow: Codable, FetchableRecord, PersistableRecord {
    static let databaseTableName = "syncClaim"

    var runId: String
    var claimNo: Int
    var payload: Data

    init(_ claim: PendingClaim) throws {
        runId = claim.runId.uuidString
        claimNo = claim.claimNo
        payload = try Payload.encode(claim)
    }

    func decoded() throws -> PendingClaim { try Payload.decode(PendingClaim.self, from: payload) }
}
