import Foundation
import GameCore

/// Точка «Дом» (PLAN.md, §3.10, «Старт „от дома“»): **только на телефоне** — сервер её не знает, в статистику она
/// не идёт. Файл JSON в Application Support рядом с правилами; стирается при выходе и удалении аккаунта
/// (`AppDependencies.wipeLocalData`) — это данные игрока.
public struct HomeStore: Sendable {
    public let url: URL

    public init(url: URL) {
        self.url = url
    }

    /// Файл приложения: `Application Support/home.json`.
    public static func live() -> HomeStore {
        let support =
            FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        return HomeStore(url: support.appendingPathComponent("home.json", isDirectory: false))
    }

    /// Сохранённый «Дом»; нет файла, он испорчен или точка негодная — `nil`.
    public func load() -> Coordinate? {
        guard let data = try? Data(contentsOf: url),
            let home = try? JSONDecoder().decode(Coordinate.self, from: data), home.isValid
        else { return nil }
        return home
    }

    /// Поставить «Дом» (заменить прежний).
    public func save(_ home: Coordinate) throws {
        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try JSONEncoder().encode(home).write(to: url, options: .atomic)
    }

    /// Убрать «Дом». Файла нет — не ошибка.
    public func remove() throws {
        guard FileManager.default.fileExists(atPath: url.path) else { return }
        try FileManager.default.removeItem(at: url)
    }
}
