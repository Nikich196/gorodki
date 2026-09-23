import Foundation

/// Общая папка приложения и расширения (App Group).
///
/// Идентификатор группы не пишем в коде, а берём из Info.plist (ключ `GorodkiAppGroupID`):
/// у сборок Free и Paid он разный и задаётся в `ios/Config/*.xcconfig`.
public enum AppGroup {
    public static var identifier: String? {
        Bundle.main.object(forInfoDictionaryKey: "GorodkiAppGroupID") as? String
    }

    /// Папка группы или `nil`, если подпись не дала доступа к группе.
    public static var containerURL: URL? {
        guard let identifier else { return nil }
        return FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: identifier)
    }
}

/// Названия виджетов: по ним приложение просит WidgetKit обновить нужный виджет.
public enum WidgetKind {
    public static let installCheck = "InstallCheck"
}

/// Отметка «приложение запускалось»: приложение пишет её в App Group, виджет читает.
/// Если виджет показывает время последнего запуска, значит работают и расширение, и App Group.
public struct InstallCheckRecord: Codable, Equatable, Sendable {
    public var lastAppLaunch: Date

    public init(lastAppLaunch: Date) {
        self.lastAppLaunch = lastAppLaunch
    }

    private static let fileName = "install-check.json"

    private static var fileURL: URL? {
        AppGroup.containerURL?.appending(path: fileName)
    }

    public static func load() -> InstallCheckRecord? {
        guard let url = fileURL, let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(InstallCheckRecord.self, from: data)
    }

    /// Сохраняет отметку в App Group.
    /// - Returns: `false`, если App Group недоступна или запись не удалась.
    @discardableResult
    public func save() -> Bool {
        guard let url = Self.fileURL, let data = try? JSONEncoder().encode(self) else { return false }
        do {
            try data.write(to: url, options: .atomic)
            return true
        } catch {
            return false
        }
    }
}
