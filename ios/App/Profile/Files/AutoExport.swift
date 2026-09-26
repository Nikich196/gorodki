import Foundation
import Persistence

/// Автоэкспорт каждого забега в папку, выбранную в «Файлах» (пункт 6 листика: внешняя FS). Папка — закладкой
/// security-scoped в UserDefaults: доступ к ней переживает перезапуск, пока игрок её не сменит. После «Финиша»
/// (`AppDependencies.archiveFinishedRuns`) GPX забега пишется в `Exports/` и копируется в эту папку.
@MainActor
final class AutoExport {
    static let shared = AutoExport(defaults: .standard)

    /// Итог последнего автоэкспорта — для экрана «Файлы и экспорт».
    struct Record: Codable, Equatable, Sendable {
        var fileName: String
        var atMs: Int64
        /// Текст ошибки; `nil` — копия в папке.
        var error: String?
    }

    static let bookmarkKey = "autoExport.bookmark"
    static let folderNameKey = "autoExport.folderName"
    static let recordKey = "autoExport.last"

    private let defaults: UserDefaults

    init(defaults: UserDefaults) {
        self.defaults = defaults
    }

    /// Имя выбранной папки; `nil` — автоэкспорт выключен.
    var folderName: String? { defaults.string(forKey: Self.folderNameKey) }

    var lastRecord: Record? {
        defaults.data(forKey: Self.recordKey).flatMap { try? JSONDecoder().decode(Record.self, from: $0) }
    }

    /// Запомнить папку из `fileImporter` (`.folder`): закладка создаётся, пока открыт доступ security-scoped.
    func choose(folder url: URL) throws {
        let accessing = url.startAccessingSecurityScopedResource()
        defer {
            if accessing { url.stopAccessingSecurityScopedResource() }
        }
        let bookmark = try url.bookmarkData(
            options: .minimalBookmark, includingResourceValuesForKeys: nil, relativeTo: nil)
        defaults.set(bookmark, forKey: Self.bookmarkKey)
        defaults.set(url.lastPathComponent, forKey: Self.folderNameKey)
    }

    func turnOff() {
        defaults.removeObject(forKey: Self.bookmarkKey)
        defaults.removeObject(forKey: Self.folderNameKey)
    }

    /// Выгрузить забеги в GPX (`Exports/`) и скопировать в выбранную папку. Папка не выбрана — ничего.
    func export(runs ids: [UUID], using dependencies: AppDependencies = .shared) async {
        guard !ids.isEmpty, defaults.data(forKey: Self.bookmarkKey) != nil else { return }
        for id in ids {
            guard let file = try? await dependencies.exportRunGPX(id) else { continue }
            record(copy(file))
        }
    }

    /// Скопировать файл в выбранную папку; итог — для экрана.
    func copy(_ file: URL) -> Record {
        let now = Int64(Date.now.timeIntervalSince1970 * 1_000)
        guard let folder = resolveFolder() else {
            return Record(fileName: file.lastPathComponent, atMs: now, error: "Папка недоступна — выбери её заново.")
        }
        let accessing = folder.startAccessingSecurityScopedResource()
        defer {
            if accessing { folder.stopAccessingSecurityScopedResource() }
        }
        do {
            try FolderCopy.copy(file, into: folder)
            return Record(fileName: file.lastPathComponent, atMs: now, error: nil)
        } catch {
            return Record(fileName: file.lastPathComponent, atMs: now, error: error.localizedDescription)
        }
    }

    private func record(_ record: Record) {
        defaults.set(try? JSONEncoder().encode(record), forKey: Self.recordKey)
    }

    /// Папка по закладке. Устаревшая закладка (папку переместили) обновляется.
    private func resolveFolder() -> URL? {
        guard let bookmark = defaults.data(forKey: Self.bookmarkKey) else { return nil }
        var stale = false
        guard let url = try? URL(resolvingBookmarkData: bookmark, bookmarkDataIsStale: &stale) else { return nil }
        if stale {
            try? choose(folder: url)
        }
        return url
    }
}
