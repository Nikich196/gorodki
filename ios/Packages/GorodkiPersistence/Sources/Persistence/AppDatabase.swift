import Foundation
import GRDB

/// База приложения (PLAN.md, D5): один файл SQLite в Application Support, режим WAL — чтение не ждёт записи.
///
/// Схема меняется только миграциями (`DatabaseMigrator`): новая версия приложения дописывает шаги, старые не правятся.
/// Так очередь, записанная прошлой версией, переживает обновление.
public struct AppDatabase: Sendable {
    public let writer: any DatabaseWriter

    /// Открывает (или создаёт) базу и доводит схему до текущей версии.
    public init(_ writer: any DatabaseWriter) throws {
        self.writer = writer
        try Self.migrator.migrate(writer)
    }

    /// База приложения: `Application Support/gorodki.sqlite`. Каталог создаётся при первом запуске.
    public static func live(fileManager: FileManager = .default) throws -> AppDatabase {
        let folder = try fileManager.url(
            for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
        return try AppDatabase(file: folder.appendingPathComponent("gorodki.sqlite"))
    }

    /// База в файле (WAL): для приложения и проверок «пережила перезапуск».
    public init(file url: URL) throws {
        try self.init(DatabasePool(path: url.path))
    }

    /// База в памяти: для тестов и превью.
    public static func inMemory() throws -> AppDatabase {
        try AppDatabase(DatabaseQueue())
    }

    /// Все шаги схемы по порядку. Имена шагов не меняются никогда: по ним база помнит, что уже сделано.
    static var migrator: DatabaseMigrator {
        var migrator = DatabaseMigrator()
        migrator.registerMigration("v1: очередь синхронизации") { db in
            // Ключи и порядок — столбцами (по ним ищут и сортируют), сама запись — JSON: модель очереди живёт
            // в пакете GorodkiSync, и хранилище не должно повторять каждое её поле.
            try db.create(table: "syncRun") { t in
                t.primaryKey("id", .text)
                t.column("startedAtMs", .integer).notNull()
                t.column("payload", .blob).notNull()
            }
            try db.create(table: "syncChunk") { t in
                t.column("runId", .text).notNull().indexed()
                t.column("firstSeq", .integer).notNull()
                t.column("payload", .blob).notNull()
                t.primaryKey(["runId", "firstSeq"])
            }
            try db.create(table: "syncClaim") { t in
                t.column("runId", .text).notNull().indexed()
                t.column("claimNo", .integer).notNull()
                t.column("payload", .blob).notNull()
                t.primaryKey(["runId", "claimNo"])
            }
        }
        return migrator
    }
}
