import Foundation
import GRDB
import GameCore
import Sync
import Testing

@testable import Persistence

/// База приложения: очередь переживает перезапуск, схема ведётся миграциями, старая запись читается новой версией.
@Suite("База приложения (GRDB)")
struct AppDatabaseTests {
    @Test("Очередь переживает закрытие и повторное открытие базы в файле")
    func queueSurvivesReopen() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("gorodki-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let file = folder.appendingPathComponent("gorodki.sqlite")
        let run = Sample.run()

        do {
            let store = GRDBSyncStore(try AppDatabase(file: file))
            try await store.insert(run)
            try await store.save(Sample.chunk(run.id, firstSeq: 0))
            try await store.save(Sample.claim(run.id, 0))
        }  // база закрыта — как выгруженное приложение

        let reopened = GRDBSyncStore(try AppDatabase(file: file))
        #expect(try await reopened.runs() == [run])
        #expect(try await reopened.chunks(of: run.id).count == 1)
        #expect(try await reopened.claims(of: run.id).count == 1)
    }

    @Test("Кусок и прогресс забега — одной транзакцией: не записался забег — не записан и кусок")
    func sealIsOneTransaction() async throws {
        let database = try AppDatabase.inMemory()
        let store = GRDBSyncStore(database)
        let run = Sample.run()
        try await store.insert(run)
        // Запись забега падает, как на кончившемся месте: кусок в той же транзакции должен откатиться.
        try await database.writer.write { db in
            try db.execute(
                sql: "CREATE TRIGGER diskFull BEFORE UPDATE ON syncRun BEGIN SELECT RAISE(ABORT, 'нет места'); END")
        }

        await #expect(throws: (any Error).self) {
            try await store.seal(Sample.chunk(run.id, firstSeq: 0)) { $0.recordedThroughSeq = 2 }
        }

        #expect(try await store.chunks(of: run.id).isEmpty)
        #expect(try await store.runs().first?.recordedThroughSeq == -1)
    }

    @Test("Нечитаемая запись очереди пропускается и остаётся в базе — остальная очередь читается, «Старт» не падает")
    func unreadableRowsAreSkipped() async throws {
        let database = try AppDatabase.inMemory()
        let store = GRDBSyncStore(database)
        let run = Sample.run()
        try await store.insert(run)
        try await store.save(Sample.chunk(run.id, firstSeq: 3))
        try await store.save(Sample.claim(run.id, 1))
        // Повреждённый файл или несовместимая правка модели: строки есть, но не читаются.
        let broken = Data("не JSON".utf8)
        try await database.writer.write { db in
            try db.execute(
                sql: "INSERT INTO syncRun (id, startedAtMs, payload) VALUES (?, 1, ?)",
                arguments: [UUID().uuidString, broken])
            try db.execute(
                sql: "INSERT INTO syncChunk (runId, firstSeq, payload) VALUES (?, 0, ?)",
                arguments: [run.id.uuidString, broken])
            try db.execute(
                sql: "INSERT INTO syncClaim (runId, claimNo, payload) VALUES (?, 0, ?)",
                arguments: [run.id.uuidString, broken])
        }

        #expect(try await store.runs() == [run])
        #expect(try await store.chunks(of: run.id) == [Sample.chunk(run.id, firstSeq: 3)])
        #expect(try await store.claims(of: run.id) == [Sample.claim(run.id, 1)])
        #expect(try await RunSession.isNewcomer(ownerId: run.ownerId, store: store))
        let rows = try await database.writer.read { db in
            try ["syncRun", "syncChunk", "syncClaim"].map { try Int.fetchOne(db, sql: "SELECT COUNT(*) FROM \($0)") }
        }
        #expect(rows == [2, 2, 2])  // чтение ничего не стирает
    }

    @Test("Нечитаемый кусок стирается вместе с остальными кусками забега; куски других забегов остаются")
    func unreadableChunkIsDeletedWithRun() async throws {
        let database = try AppDatabase.inMemory()
        let store = GRDBSyncStore(database)
        let run = UUID()
        let other = UUID()
        try await store.save(Sample.chunk(run, firstSeq: 3))
        try await store.save(Sample.chunk(other, firstSeq: 0))
        try await database.writer.write { db in
            try db.execute(
                sql: "INSERT INTO syncChunk (runId, firstSeq, payload) VALUES (?, 0, ?)",
                arguments: [run.uuidString, Data("не JSON".utf8)])
        }

        try await store.deleteChunks(of: run)

        let left = try await database.writer.read { db in
            try String.fetchAll(db, sql: "SELECT runId FROM syncChunk")
        }
        #expect(left == [other.uuidString])
    }

    @Test("Продолженный забег: новая заявка не занимает номер нечитаемой и не затирает её строку")
    func newClaimSkipsUnreadableNumber() async throws {
        let database = try AppDatabase.inMemory()
        let store = GRDBSyncStore(database)
        let run = Sample.run()
        try await store.insert(run)
        try await store.save(Sample.claim(run.id, 0))
        // Заявка 1 не читается, но могла уже уйти на сервер: номер 1 занят.
        try await database.writer.write { db in
            try db.execute(
                sql: "INSERT INTO syncClaim (runId, claimNo, payload) VALUES (?, 1, ?)",
                arguments: [run.id.uuidString, Data("не JSON".utf8)])
        }

        let recorder = try #require(try await RunRecorder.resume(runId: run.id, store: store))
        for point in Sample.chunk(run.id, firstSeq: 0, count: 5).points {
            try await recorder.record(point)
        }
        let claimNo = try await recorder.claim(
            LoopClaim(startSeq: 0, endSeq: 4, closure: .crossing, estimatedArea: 5_000))

        #expect(claimNo == 2)
        #expect(try await store.claims(of: run.id).map(\.claimNo) == [0, 2])
        let rows = try await database.writer.read { db in
            try Int.fetchOne(db, sql: "SELECT COUNT(*) FROM syncClaim")
        }
        #expect(rows == 3)
    }

    @Test("О нечитаемой строке журнал пишет один раз, хоть её и читают много раз за проход")
    func unreadableRowIsReportedOnce() async throws {
        let database = try AppDatabase.inMemory()
        let store = GRDBSyncStore(database)
        let run = UUID()
        try await database.writer.write { db in
            try db.execute(
                sql: "INSERT INTO syncChunk (runId, firstSeq, payload) VALUES (?, 0, ?)",
                arguments: [run.uuidString, Data("не JSON".utf8)])
        }
        struct Broken: Error {}

        for _ in 0..<3 {
            #expect(try await store.chunks(of: run).isEmpty)
        }

        // Чтение уже написало об этой строке — повтор не пишет; о другой строке — пишет.
        #expect(!GRDBSyncStore.reportUnreadable(table: "syncChunk", key: "\(run.uuidString)/0", Broken()))
        #expect(GRDBSyncStore.reportUnreadable(table: "syncChunk", key: "\(run.uuidString)/1", Broken()))
    }

    @Test("Схема доведена до последней миграции; повторное открытие ничего не ломает")
    func migrationsAreApplied() async throws {
        let queue = try DatabaseQueue()
        _ = try AppDatabase(queue)
        _ = try AppDatabase(queue)

        let applied = try await queue.read { db in
            try String.fetchAll(db, sql: "SELECT identifier FROM grdb_migrations")
        }
        let tables = try await queue.read { db in
            try String.fetchAll(
                db, sql: "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'sync%' ORDER BY name")
        }

        #expect(applied == ["v1: очередь синхронизации", "v2: история забегов"])
        #expect(tables == ["syncChunk", "syncClaim", "syncRun"])
    }

    /// JSON, каким его пишет версия 1. Если модель очереди изменится так, что старую запись уже не прочитать (новое
    /// обязательное поле без миграции), этот тест упадёт — иначе после обновления приложения очередь пропала бы.
    @Test("Запись очереди версии 1 читается")
    func versionOnePayloadsDecode() async throws {
        let database = try AppDatabase.inMemory()
        let runId = "5F1C1D3E-8B0A-4C39-9E2B-1A2B3C4D5E6F"
        let run = """
            {"id":"\(runId)","ownerId":"player-1","league":"run","source":"live","configVersion":1,\
            "startedAtMs":1790000000000,"deviceId":"0B6A5E2C-3D4F-4A1B-8C9D-0E1F2A3B4C5D","appVersion":"1.0 (1)",\
            "motionAuthorized":true,"recordedThroughSeq":2,"lastPointMs":1790000002000,"sealedSensorsMarkMs":1790000002000,\
            "serverState":{"started":{}},"finishSent":false,"confirmedComplete":false,"resendRounds":0}
            """
        let chunk = """
            {"runId":"\(runId)","firstSeq":0,"points":[{"seq":0,"coordinate":{"latitude":52.09,"longitude":23.68},\
            "timestamp":1790000000,"horizontalAccuracy":5,"speed":3}],"sources":[1],\
            "motion":[{"timestamp":1790000000.5,"activity":"running"}],\
            "steps":[{"start":1790000000,"end":1790000005,"steps":7}],"sensorsCompleteThroughMs":1790000001000,"sent":true}
            """
        let claim = """
            {"runId":"\(runId)","claimNo":0,"loop":{"startSeq":0,"endSeq":9,"closure":"crossing","estimatedArea":5000},\
            "sent":true,"outcome":{"status":"pending","waitingFor":"points","areaSquareMeters":0}}
            """
        try await database.writer.write { db in
            try db.execute(
                sql: "INSERT INTO syncRun (id, startedAtMs, payload) VALUES (?, ?, ?)",
                arguments: [runId, 1_790_000_000_000, Data(run.utf8)])
            try db.execute(
                sql: "INSERT INTO syncChunk (runId, firstSeq, payload) VALUES (?, 0, ?)",
                arguments: [runId, Data(chunk.utf8)])
            try db.execute(
                sql: "INSERT INTO syncClaim (runId, claimNo, payload) VALUES (?, 0, ?)",
                arguments: [runId, Data(claim.utf8)])
        }
        let store = GRDBSyncStore(database)
        let id = try #require(UUID(uuidString: runId))

        let stored = try #require(try await store.runs().first)
        let storedChunk = try #require(try await store.chunks(of: id).first)
        let storedClaim = try #require(try await store.claims(of: id).first)

        #expect(stored.serverState == .started)
        #expect(stored.recordedThroughSeq == 2)
        #expect(stored.endedAtMs == nil)
        #expect(storedChunk.points.first?.coordinate.latitude == 52.09)
        #expect(storedChunk.sources == [.simulated])
        #expect(storedChunk.steps.first?.steps == 7)
        #expect(storedChunk.sent)
        #expect(storedClaim.outcome?.waitingFor == "points")
        #expect(!storedClaim.isSettled)
    }
}
