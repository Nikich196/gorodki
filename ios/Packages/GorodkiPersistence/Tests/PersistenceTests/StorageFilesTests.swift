import Foundation
import GRDB
import Testing

@testable import Persistence

/// Папка на одну проверку — стирается в конце.
private final class TempFolder {
    let url = FileManager.default.temporaryDirectory.appendingPathComponent("persistence-\(UUID().uuidString)")

    init() throws {
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    }

    func write(_ name: String, bytes: Int, modifiedAt: Double? = nil) throws -> URL {
        let file = url.appendingPathComponent(name)
        try FileManager.default.createDirectory(
            at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(repeating: 7, count: bytes).write(to: file)
        if let modifiedAt {
            try FileManager.default.setAttributes(
                [.modificationDate: Date(timeIntervalSince1970: modifiedAt)], ofItemAtPath: file.path)
        }
        return file
    }

    deinit { try? FileManager.default.removeItem(at: url) }
}

@Suite("«Хранилище» и «Файлы»: размеры, выгрузки, автоэкспорт")
struct StorageFilesTests {
    @Test("Выгрузки — новые первыми, при равном времени по имени; папки ещё нет — пусто")
    func exportFiles() throws {
        let folder = try TempFolder()
        let exports = ExportFolder(url: folder.url.appendingPathComponent("Exports"))
        #expect(exports.files().isEmpty)
        try exports.write(Data("a".utf8), named: "b.gpx")
        _ = try folder.write("Exports/old.gpx", bytes: 3, modifiedAt: 1_000)
        _ = try folder.write("Exports/a.gpx", bytes: 5, modifiedAt: 2_000)
        _ = try folder.write("Exports/c.gpx", bytes: 5, modifiedAt: 2_000)
        _ = try folder.write("Exports/nested/skip.gpx", bytes: 1)
        let names = exports.files().map(\.name)
        #expect(names == ["b.gpx", "a.gpx", "c.gpx", "old.gpx"])
        #expect(exports.files().first { $0.name == "old.gpx" }?.bytes == 3)

        try exports.remove(named: "old.gpx")
        #expect(!exports.files().map(\.name).contains("old.gpx"))
        try exports.remove(named: "../Exports")
        #expect(FileManager.default.fileExists(atPath: exports.url.path), "чужое имя ничего не стирает")
    }

    @Test("Размер папки — со вложенными файлами; нет папки — ноль")
    func folderUsage() throws {
        let folder = try TempFolder()
        _ = try folder.write("one.mp4", bytes: 1_000)
        _ = try folder.write("deep/two.mp4", bytes: 24)
        let usage = DiskUsage.of(folder.url)
        #expect(usage.bytes == 1_024)
        #expect(usage.files == 2)
        let missing = DiskUsage.of(folder.url.appendingPathComponent("нет"))
        #expect(missing.bytes == 0 && missing.files == 0)
    }

    @Test("База — вместе с журналом WAL: настоящая база в файле больше нуля")
    func databaseUsage() throws {
        let folder = try TempFolder()
        let file = folder.url.appendingPathComponent("gorodki.sqlite")
        #expect(DiskUsage.database(at: file) == 0)
        let database = try AppDatabase(file: file)
        try database.writer.write { db in
            try db.execute(sql: "INSERT INTO runHistory VALUES ('x', 1, x'00', zeroblob(50000))")
        }
        let bytes = DiskUsage.database(at: file)
        #expect(bytes > 50_000, "\(bytes)")
        let wal = FileManager.default.fileExists(atPath: file.path + "-wal")
        #expect(wal, "в режиме WAL журнал лежит рядом и считается")
    }

    @Test("Автоэкспорт: копия в выбранную папку, повтор заменяет, исходник остаётся")
    func copyIntoFolder() throws {
        let folder = try TempFolder()
        let source = try folder.write("Exports/run.gpx", bytes: 10)
        let target = folder.url.appendingPathComponent("Выбранная папка")
        try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)

        let copy = try FolderCopy.copy(source, into: target)
        #expect(copy.lastPathComponent == "run.gpx")
        #expect(DiskUsage.of(target).bytes == 10)

        try Data(repeating: 1, count: 3).write(to: source)
        try FolderCopy.copy(source, into: target)
        let replaced = DiskUsage.of(target)
        #expect(replaced.bytes == 3 && replaced.files == 1)
        #expect(FileManager.default.fileExists(atPath: source.path))
    }
}
