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
}
