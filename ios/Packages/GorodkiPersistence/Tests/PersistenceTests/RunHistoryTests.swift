import Foundation
import GRDB
import GameCore
import Sync
import Testing

@testable import Persistence

@Suite("История забегов и выгрузки")
struct RunHistoryTests {
    /// Законченный забег в очереди с двумя кусками, отданными вперемешку.
    private func endedRun(in queue: any SyncStore, startedAtMs: Int64 = 1_790_000_000_000) async throws -> LocalRun {
        var run = Sample.run(startedAtMs: startedAtMs)
        run.endedAtMs = startedAtMs + 60_000
        run.lastSeq = 5
        try await queue.insert(run)
        try await queue.save(Sample.chunk(run.id, firstSeq: 3))
        try await queue.save(Sample.chunk(run.id, firstSeq: 0))
        return run
    }

    @Test("«Финиш»: законченный забег сохраняется с точками по номеру и остаётся, когда очередь стёрла куски")
    func archiveSurvivesQueueCleanup() async throws {
        let database = try AppDatabase.inMemory()
        let queue = GRDBSyncStore(database)
        let history = RunHistory(database)
        let run = try await endedRun(in: queue)
        var going = Sample.run(startedAtMs: 1_790_000_100_000)  // ещё идёт — не сохраняется
        going.recordedThroughSeq = 2
        try await queue.insert(going)

        #expect(try await history.archiveEnded(from: queue) == [run.id])
        try await queue.deleteChunks(of: run.id)  // сервер подтвердил забег
        #expect(try await history.archiveEnded(from: queue).isEmpty)  // повтор не затирает точки пустыми

        let entry = try #require(try await history.entries().first)
        #expect(entry.id == run.id && entry.pointCount == 6 && entry.endedAtMs == run.endedAtMs)
        #expect(try await history.points(of: run.id)?.map(\.seq) == [0, 1, 2, 3, 4, 5])
        #expect(try await history.entries().count == 1)
    }

    @Test("Забег, подтверждённый до сохранения (кусков уже нет), пропускается")
    func confirmedRunSkipped() async throws {
        let queue = InMemorySyncStore()
        let history = RunHistory(try AppDatabase.inMemory())
        let run = try await endedRun(in: queue)
        await queue.updateRun(run.id) { $0.confirmedComplete = true }
        #expect(try await history.archiveEnded(from: queue).isEmpty)
    }

    @Test("Предел хранения — последние по началу; по умолчанию — все (число за Никитой)")
    func retention() async throws {
        #expect(RunHistory.defaultRetention == nil)
        let database = try AppDatabase.inMemory()
        let queue = GRDBSyncStore(database)
        let old = try await endedRun(in: queue, startedAtMs: 1_000)
        let newer = try await endedRun(in: queue, startedAtMs: 2_000)
        let newest = try await endedRun(in: queue, startedAtMs: 3_000)
        try await RunHistory(database, retention: 2).archiveEnded(from: queue)
        #expect(try await RunHistory(database).entries().map(\.id) == [newest.id, newer.id])
        #expect(try await RunHistory(database).points(of: old.id) == nil)
    }

    @Test("Выход из аккаунта: очередь и история стёрты целиком — и нечитаемые строки тоже")
    func wipeClearsAll() async throws {
        let database = try AppDatabase.inMemory()
        let queue = GRDBSyncStore(database)
        let history = RunHistory(database)
        let run = try await endedRun(in: queue)
        try await queue.save(Sample.claim(run.id, 0))
        try await history.archiveEnded(from: queue)
        try await database.writer.write { db in
            try db.execute(
                sql: "INSERT INTO syncRun (id, startedAtMs, payload) VALUES ('broken', 0, ?)",
                arguments: [Data("не JSON".utf8)])
            try db.execute(
                sql: "INSERT INTO runHistory (id, startedAtMs, summary, points) VALUES ('broken', 0, ?, ?)",
                arguments: [Data("не JSON".utf8), Data()])
        }

        try await queue.removeAll()
        try await history.removeAll()

        let left = try await database.writer.read { db in
            try ["syncRun", "syncChunk", "syncClaim", "runHistory"].map { table in
                try Int.fetchOne(db, sql: "SELECT COUNT(*) FROM \(table)") ?? -1
            }
        }
        #expect(left == [0, 0, 0, 0])
    }

    @Test("Выгрузка пишется в папку Exports, тот же файл заменяется")
    func exportFolder() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let folder = ExportFolder(url: root.appendingPathComponent("Exports"))
        try folder.write(Data("1".utf8), named: "a.gpx")
        let file = try folder.write(Data("2".utf8), named: "a.gpx")
        #expect(file.lastPathComponent == "a.gpx")
        #expect(try Data(contentsOf: file) == Data("2".utf8))
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.url.path) == ["a.gpx"])
    }
}
