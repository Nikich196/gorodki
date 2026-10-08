import Foundation
import GameCore
import GorodkiAPI

/// Статистика «Исследования» (PLAN.md, §5, экран 15; §3.10, «Статистика»): сколько открыто — за всё время и за сезон,
/// гектары и клетки; «% Бреста» и районы. Собирается из `GET /fog/summary` и `GET /seasons` — чистая функция, её
/// проверяют тесты на Linux; экран только рисует.
///
/// Проценты сервер считает от «достижимой» площади набора OSM и отдаёт необязательными полями (контракт E9,
/// `brestPercent` и `districts`). Набора OSM нет — поля `null`, экран пишет «появится позже».
public struct ExplorationSummary: Equatable, Sendable {
    /// Слой тумана: «Пешком» и «Вело» (вело — с Сезона 1).
    public enum Layer: String, Equatable, Sendable {
        case foot, bike

        public var title: String {
            switch self {
            case .foot: "Пешком"
            case .bike: "Вело"
            }
        }
    }

    /// Доля района (или Арены) от его «достижимой» площади.
    public struct Share: Equatable, Sendable, Identifiable {
        public var key: String
        public var name: String
        public var percent: Double
        /// Граница — предложение, ещё не утверждённая (Арена БрГТУ, docs/decisions/osm-questions.md).
        public var proposal: Bool

        public var id: String { key }

        public init(key: String, name: String, percent: Double, proposal: Bool = false) {
            self.key = key
            self.name = name
            self.percent = percent
            self.proposal = proposal
        }
    }

    /// За всё время или за один сезон.
    public struct Period: Equatable, Sendable, Identifiable {
        /// Номер сезона; `nil` — за всё время.
        public var season: Int?
        /// «За всё время», «Сезон 0 (бета)».
        public var title: String
        public var squareMeters: Double
        public var cells: Int
        /// Сезон идёт сейчас.
        public var isCurrent: Bool
        /// «% Бреста»; `nil` — сервер ещё не считает (нет набора OSM или старый контракт).
        public var brestPercent: Double?
        /// Районы и Арена; `nil` — сервер ещё не считает.
        public var districts: [Share]?

        public var id: String { season.map { "season-\($0)" } ?? "all" }

        public init(
            season: Int?, title: String, squareMeters: Double, cells: Int, isCurrent: Bool = false,
            brestPercent: Double? = nil, districts: [Share]? = nil
        ) {
            self.season = season
            self.title = title
            self.squareMeters = squareMeters
            self.cells = cells
            self.isCurrent = isCurrent
            self.brestPercent = brestPercent
            self.districts = districts
        }
    }

    public var layer: Layer
    public var allTime: Period
    /// Сезоны из сводки — новые сверху (сервер отдаёт только текущий).
    public var seasons: [Period]

    public init(layer: Layer = .foot, allTime: Period, seasons: [Period] = []) {
        self.layer = layer
        self.allTime = allTime
        self.seasons = seasons
    }

    /// Сводка сервера → статистика слоя `layer`. Нет строки «за всё время» — ноль: игрок ещё ничего не открыл.
    public init(
        summary: Components.Schemas.FogSummaryResponse, seasons: Components.Schemas.SeasonsResponse?,
        layer: Layer = .foot
    ) {
        let kind: Components.Schemas.FogLayerKind = layer == .foot ? .foot : .bike
        let rows = summary.layers.filter { $0.layer == kind }
        let current = seasons?.current.map(Int.init)
        func period(_ row: Components.Schemas.FogLayerSummary?, season: Int?) -> Period {
            let title: String
            if let season {
                title = seasons?.seasons.first { Int($0.number) == season }?.name ?? "Сезон \(season)"
            } else {
                title = "За всё время"
            }
            // Проценты (E9) — необязательные поля сводки: нет набора OSM — `null`, пустой список районов — как без него.
            let districts = row?.districts.flatMap { list in list.isEmpty ? nil : list }
            return Period(
                season: season, title: title, squareMeters: row?.areaSquareMeters ?? 0,
                cells: Int(row?.cellCount ?? 0), isCurrent: season != nil && season == current,
                brestPercent: row?.brestPercent,
                districts: districts?.map { district in
                    Share(
                        key: district.key, name: district.name, percent: district.percent,
                        proposal: district.proposal)
                })
        }
        self.layer = layer
        self.allTime = period(rows.first { $0.season == nil }, season: nil)
        self.seasons = rows.compactMap { row in row.season.map { Int($0) } }
            .sorted(by: >)
            .map { season in period(rows.first { $0.season.map(Int.init) == season }, season: season) }
    }

    /// Сервер уже считает «% Бреста».
    public var hasShares: Bool { allTime.brestPercent != nil }
}

/// Тексты статистики «Исследования» (PLAN.md, §6.10): «4,26 га», «4,8 %», «0,35 %».
public enum ExplorationText {
    /// Вместо процентов, пока сервер их не считает.
    public static let later = "появится позже"

    /// «4,8 %»: до процента — два знака («0,35 %»), дальше — один. Неразрывный пробел перед знаком.
    public static func percent(_ value: Double) -> String {
        let clamped = min(max(value, 0), 100)
        return NumberText.decimal(clamped, fractionDigits: clamped < 1 ? 2 : 1) + NumberText.unitSeparator + "%"
    }

    /// «4,26 га» — два знака, как в профиле.
    public static func area(_ squareMeters: Double) -> String {
        NumberText.hectares(fromSquareMeters: squareMeters, fractionDigits: 2)
    }
}

/// Какой сейчас день сезона — «день 10 из 14» в профиле (docs/design/tokens.md, §8, `SeasonCard`).
public struct SeasonProgress: Equatable, Sendable {
    public var number: Int
    public var name: String
    /// День сезона с 1.
    public var day: Int
    /// Дней в сезоне; `nil` — конец не назначен.
    public var days: Int?

    public init(number: Int, name: String, day: Int, days: Int?) {
        self.number = number
        self.name = name
        self.day = day
        self.days = days
    }

    /// Текущий сезон на момент `nowMs`; `nil` — сезона нет, он ещё не начался или уже кончился.
    public init?(seasons: Components.Schemas.SeasonsResponse, nowMs: Int64) {
        let dayMs: Int64 = 86_400_000
        guard let current = seasons.current,
            let season = seasons.seasons.first(where: { $0.number == current }),
            nowMs >= season.startsAtMs, season.endsAtMs.map({ nowMs < $0 }) ?? true
        else { return nil }
        self.number = Int(season.number)
        self.name = season.name
        self.day = Int((nowMs - season.startsAtMs) / dayMs) + 1
        self.days = season.endsAtMs.map { Int(($0 - season.startsAtMs + dayMs - 1) / dayMs) }
    }

    /// «день 10 из 14»; без конца — «день 3».
    public var text: String {
        days.map { "день \(day) из \($0)" } ?? "день \(day)"
    }
}
