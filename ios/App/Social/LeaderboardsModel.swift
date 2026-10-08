import Foundation
import GameCore
import GorodkiAPI
import Observation

/// Какой рейтинг открыт (PLAN.md, §5, экран 20): «Захват» — очки сезона, «Исследование» — открытый туман, «Короли» —
/// отрезки (экран «Короли участков» — следующая волна, здесь только «скоро»).
enum LeaderboardKind: String, CaseIterable, Identifiable, Sendable {
    case territory, exploration, kings

    var id: String { rawValue }

    var title: String {
        switch self {
        case .territory: "Захват"
        case .exploration: "Исследование"
        case .kings: "Короли"
        }
    }
}

/// Лига рейтинга (PLAN.md, §3.5: «раздельно по лигам»): «Вело» — с Сезона 1 (D16), выбрать пока нельзя.
enum LeaderboardLeague: String, CaseIterable, Identifiable, Sendable {
    case run, bike

    var id: String { rawValue }

    var title: String {
        switch self {
        case .run: "Бег"
        case .bike: "Вело"
        }
    }

    var locked: Bool { self == .bike }

    var api: Components.Schemas.League {
        switch self {
        case .run: .run
        case .bike: .bike
        }
    }

    /// Слой тумана «Исследования» для лиги: «Бег» — «Пешком», «Вело» — «Вело» (решено 26.09, ios-app.md).
    var fogLayer: String {
        switch self {
        case .run: "foot"
        case .bike: "bike"
        }
    }
}

/// Строка рейтинга: простые значения для экрана. `id` — стабильный (ник; свой ряд — «me»): при смене мест строки
/// переезжают анимацией, а не перерисовываются.
struct LeaderboardRow: Identifiable, Equatable, Sendable {
    var id: String
    var rank: Int
    var name: String
    /// «1 240 очков», «12,35 га».
    var value: String
    /// Число для перекатки цифр (`numericText`).
    var number: Double
    var me: Bool
}

/// «Рейтинги»: вид, лига, строки, свой ряд и пометка «итог / предварительно» (PLAN.md, §3.5; контракт #136).
@MainActor
@Observable
final class LeaderboardsModel {
    var kind: LeaderboardKind = .territory
    var league: LeaderboardLeague = .run
    private(set) var rows: [LeaderboardRow] = []
    /// Своё место, если его нет среди первых (сервер отдаёт до 50): закреплено снизу.
    private(set) var mine: LeaderboardRow?
    /// «Сезон 0 · срез 20.11» — над списком.
    private(set) var caption: String?
    /// Итог закрытого сезона (`final`): пометка «итог», иначе «предварительно». У «Исследования» пометки нет.
    private(set) var isFinal: Bool?
    private(set) var failure: SocialFailure?
    private(set) var loading = false
    /// Загружено хоть раз (пустой срез — «рейтинг появится после первого среза»).
    private(set) var loaded = false

    /// Пометка рейтинга сезона: «итог» или «предварительно»; у «Исследования» — нет.
    var statusText: String? {
        isFinal.map { $0 ? "итог" : "предварительно" }
    }

    @ObservationIgnored private let source: (any LeaderboardSource)?

    /// - Parameter source: `nil` — нет сервера или входа: экран скажет об этом.
    init(source: (any LeaderboardSource)?) {
        self.source = source
    }

    /// Свой ряд уже среди первых — закреплять снизу нечего.
    var mineInTop: Bool {
        rows.contains { $0.me }
    }

    /// Показать свой ряд закреплённым: он есть и его нет среди первых. Если он среди первых, но прокручен из виду,
    /// экран закрепляет его сам (`mineRowVisible`).
    func pinnedRow(mineRowVisible: Bool) -> LeaderboardRow? {
        if let mine, !mineInTop { return mine }
        guard !mineRowVisible else { return nil }
        return rows.first { $0.me }
    }

    /// Загрузить выбранный вид и лигу. «Короли» — только заглушка «скоро»: запросов нет.
    func load() async {
        guard kind != .kings else {
            apply(rows: [], mine: nil, caption: nil, isFinal: nil)
            failure = nil
            return
        }
        guard let source else {
            failure = .offline
            return
        }
        let kind = kind
        let league = league
        loading = true
        defer { loading = false }
        // Пока грузилось, вид или лигу сменили — ответ уже не про то, что на экране.
        var current: Bool { kind == self.kind && league == self.league }
        do {
            switch kind {
            case .territory:
                let board = try await source.territory(league: league.api)
                guard current else { return }
                apply(board)
            case .exploration:
                let board = try await source.exploration(layer: league.fogLayer)
                guard current else { return }
                apply(board)
            case .kings:
                return
            }
            failure = nil
            loaded = true
        } catch {
            guard current else { return }
            failure = SocialFailure.from(error)
        }
    }

    func apply(_ board: TerritoryBoard) {
        let row: (Components.Schemas.TerritoryLeaderboardEntry) -> LeaderboardRow = { entry in
            LeaderboardRow(
                id: entry.me ? "me" : entry.name, rank: Int(entry.rank), name: entry.name,
                value: CountText.score(Int(entry.points)), number: Double(entry.points), me: entry.me)
        }
        var parts: [String] = []
        if let season = board.season {
            parts.append("Сезон \(season)")
        }
        if let day = board.day.flatMap(Self.dayText) {
            parts.append("срез " + day)
        }
        apply(
            rows: board.entries.map(row), mine: board.mine.map(row), caption: parts.joined(separator: " · "),
            isFinal: board.final)
    }

    func apply(_ board: ExplorationBoard) {
        let row: (Components.Schemas.LeaderboardEntry) -> LeaderboardRow = { entry in
            LeaderboardRow(
                id: entry.me ? "me" : entry.name, rank: Int(entry.rank), name: entry.name,
                value: NumberText.decimal(entry.hectares, fractionDigits: 2) + NumberText.unitSeparator + "га",
                number: entry.hectares, me: entry.me)
        }
        var parts = [board.season.map { "Сезон \($0)" } ?? "За всё время"]
        if let day = board.day.flatMap(Self.dayText) {
            parts.append("срез " + day)
        }
        apply(
            rows: board.entries.map(row), mine: board.mine.map(row), caption: parts.joined(separator: " · "),
            isFinal: nil)
    }

    private func apply(rows: [LeaderboardRow], mine: LeaderboardRow?, caption: String?, isFinal: Bool?) {
        self.rows = Self.uniqueIDs(rows)
        self.mine = mine
        self.caption = caption
        self.isFinal = isFinal
    }

    /// Два одинаковых ника (например, два «Игрок #1234» — номер из 9 000) не должны сломать `ForEach`: второму —
    /// ник с местом.
    private static func uniqueIDs(_ rows: [LeaderboardRow]) -> [LeaderboardRow] {
        var seen: Set<String> = []
        return rows.map { row in
            var row = row
            if !seen.insert(row.id).inserted {
                row.id += "#\(row.rank)"
            }
            return row
        }
    }

    /// «2026-11-20» → «20.11».
    static func dayText(_ day: String) -> String? {
        let parts = day.split(separator: "-")
        guard parts.count == 3 else { return nil }
        return "\(parts[2]).\(parts[1])"
    }
}
