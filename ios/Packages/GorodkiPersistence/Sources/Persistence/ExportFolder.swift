import Foundation

/// Папка выгрузок `Documents/Exports`: игрок видит её в приложении «Файлы» («На iPhone» → «Городки»), потому что
/// в Info.plist включены `UIFileSharingEnabled` и `LSSupportsOpeningDocumentsInPlace` (пункт 6 листика, «внешняя FS»).
/// Сюда пишутся след забега (GPX) и «мои данные» (`GET /me/export`). Выход из аккаунта папку не трогает: это файлы,
/// которые игрок выгрузил сам.
public struct ExportFolder: Sendable {
    public let url: URL

    public init(url: URL) {
        self.url = url
    }

    public static func live() -> ExportFolder {
        let documents =
            FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        return ExportFolder(url: documents.appendingPathComponent("Exports", isDirectory: true))
    }

    /// Записать файл (тот же `name` — заменить). Папка создаётся при первой записи.
    /// - Returns: где лежит файл.
    @discardableResult
    public func write(_ data: Data, named name: String) throws -> URL {
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        let file = url.appendingPathComponent(name, isDirectory: false)
        try data.write(to: file, options: .atomic)
        return file
    }

    /// Файлы папки, новые первыми (экран «Файлы и экспорт»). Папки ещё нет — пусто.
    public func files() -> [StoredFile] {
        StoredFile.list(in: url)
    }

    /// Стереть один файл выгрузки — только из этой папки: имя с «/» или «..» ничего не стирает.
    public func remove(named name: String) throws {
        guard !name.isEmpty, !name.contains("/"), name != ".", name != ".." else { return }
        try FileManager.default.removeItem(at: url.appendingPathComponent(name, isDirectory: false))
    }
}

/// Файл на диске для экранов «Хранилище» и «Файлы и экспорт».
public struct StoredFile: Hashable, Sendable, Identifiable {
    public var url: URL
    public var bytes: Int64
    /// Когда изменён, секунды Unix.
    public var modifiedAt: Double

    public var id: URL { url }
    public var name: String { url.lastPathComponent }

    public init(url: URL, bytes: Int64, modifiedAt: Double) {
        self.url = url
        self.bytes = bytes
        self.modifiedAt = modifiedAt
    }

    /// Обычные файлы прямо в папке (без вложенных), новые первыми; при равном времени — по имени.
    public static func list(in folder: URL) -> [StoredFile] {
        let keys: [URLResourceKey] = [.isRegularFileKey, .fileSizeKey, .contentModificationDateKey]
        let entries =
            (try? FileManager.default.contentsOfDirectory(
                at: folder, includingPropertiesForKeys: keys, options: [.skipsHiddenFiles])) ?? []
        return entries.compactMap { entry -> StoredFile? in
            guard let values = try? entry.resourceValues(forKeys: Set(keys)), values.isRegularFile == true else {
                return nil
            }
            return StoredFile(
                url: entry, bytes: Int64(values.fileSize ?? 0),
                modifiedAt: values.contentModificationDate?.timeIntervalSince1970 ?? 0)
        }
        .sorted { ($0.modifiedAt, $1.name) > ($1.modifiedAt, $0.name) }
    }
}

/// Сколько занимает папка со всеми вложенными файлами (экран «Хранилище», пункт 5 листика).
public enum DiskUsage {
    /// Байты и число обычных файлов; папки нет — ноль.
    public static func of(_ folder: URL) -> (bytes: Int64, files: Int) {
        let keys: [URLResourceKey] = [.isRegularFileKey, .fileSizeKey]
        guard let entries = FileManager.default.enumerator(at: folder, includingPropertiesForKeys: keys) else {
            return (0, 0)
        }
        var bytes: Int64 = 0
        var files = 0
        for case let entry as URL in entries {
            guard let values = try? entry.resourceValues(forKeys: Set(keys)), values.isRegularFile == true else {
                continue
            }
            bytes += Int64(values.fileSize ?? 0)
            files += 1
        }
        return (bytes, files)
    }

    /// База SQLite вместе с журналом WAL и общей памятью (`-wal`, `-shm`): в режиме WAL свежие записи живут в журнале.
    public static func database(at file: URL) -> Int64 {
        [file.path, file.path + "-wal", file.path + "-shm"].reduce(Int64(0)) { total, path in
            let size = (try? FileManager.default.attributesOfItem(atPath: path)[.size] as? NSNumber)?.int64Value
            return total + (size ?? 0)
        }
    }
}

/// Автоэкспорт забега в папку, выбранную игроком в «Файлах» (пункт 6 листика): копия GPX из `Exports/` в эту папку.
/// Доступ к чужой папке (закладка security-scoped, `startAccessingSecurityScopedResource`) открывает приложение —
/// здесь только само копирование, поэтому оно проверяется на Linux.
public enum FolderCopy {
    /// Скопировать `file` в `folder` под тем же именем; такой файл там уже есть — заменить (забег выгружен заново).
    /// - Returns: где лежит копия.
    @discardableResult
    public static func copy(_ file: URL, into folder: URL) throws -> URL {
        let target = folder.appendingPathComponent(file.lastPathComponent, isDirectory: false)
        let manager = FileManager.default
        if manager.fileExists(atPath: target.path) {
            try manager.removeItem(at: target)
        }
        try manager.copyItem(at: file, to: target)
        return target
    }
}
