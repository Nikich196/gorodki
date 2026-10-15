import Foundation

/// Снимок профиля для виджета «Моя земля» (дизайн-проход 15.10): приложение пишет его в App Group, когда загрузило
/// профиль, а при выходе стирает; виджет только читает. Без координат и чужих данных — то же, что видно в «Профиле».
public struct TerritorySnapshot: Codable, Equatable, Sendable {
    public var displayName: String
    /// Номер цвета игрока 0–11 (`colorIndex` из `GET /me`).
    public var colorIndex: Int
    public var seasonName: String?
    /// День сезона с 1 и дней в сезоне (`nil` — конец не назначен).
    public var seasonDay: Int?
    public var seasonDays: Int?
    /// Открыто тумана «Пешком» за сезон и за всё время, м².
    public var seasonSquareMeters: Double?
    public var allTimeSquareMeters: Double?
    /// Место в «Кто открыл больше».
    public var explorationRank: Int?
    public var updatedAt: Date

    public init(
        displayName: String, colorIndex: Int, seasonName: String?, seasonDay: Int?, seasonDays: Int?,
        seasonSquareMeters: Double?, allTimeSquareMeters: Double?, explorationRank: Int?, updatedAt: Date
    ) {
        self.displayName = displayName
        self.colorIndex = colorIndex
        self.seasonName = seasonName
        self.seasonDay = seasonDay
        self.seasonDays = seasonDays
        self.seasonSquareMeters = seasonSquareMeters
        self.allTimeSquareMeters = allTimeSquareMeters
        self.explorationRank = explorationRank
        self.updatedAt = updatedAt
    }

    /// Образец для галереи виджетов и превью.
    public static let sample = TerritorySnapshot(
        displayName: "Бегун-1234", colorIndex: 7, seasonName: "Сезон 0", seasonDay: 10, seasonDays: 46,
        seasonSquareMeters: 11_100, allTimeSquareMeters: 42_600, explorationRank: 7, updatedAt: .now)

    private static let fileName = "territory-snapshot.json"

    private static var fileURL: URL? {
        AppGroup.containerURL?.appending(path: fileName)
    }

    public static func load() -> TerritorySnapshot? {
        guard let url = fileURL, let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(TerritorySnapshot.self, from: data)
    }

    /// Сохраняет снимок в App Group. `false` — группа недоступна или запись не удалась.
    @discardableResult
    public func save() -> Bool {
        guard let url = Self.fileURL, let data = try? JSONEncoder().encode(self) else { return false }
        return (try? data.write(to: url, options: .atomic)) != nil
    }

    /// Стереть снимок — при выходе и удалении аккаунта: виджет снова зовёт войти.
    public static func remove() {
        guard let url = fileURL else { return }
        try? FileManager.default.removeItem(at: url)
    }
}
