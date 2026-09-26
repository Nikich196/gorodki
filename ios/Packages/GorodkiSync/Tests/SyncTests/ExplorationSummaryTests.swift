import Foundation
import GameCore
import GorodkiAPI
import Testing

@testable import Sync

@Suite("Статистика «Исследования» и день сезона — из /fog/summary и /seasons (PLAN.md, §5, экран 15)")
struct ExplorationSummaryTests {
    /// Образцы contracts/samples: fog-summary.json и seasons.json.
    private static let summary = """
        {"layers":[
          {"layer":"foot","season":null,"tiles":3,"cellCount":1234,"areaSquareMeters":42580.5},
          {"layer":"foot","season":0,"tiles":1,"cellCount":321,"areaSquareMeters":11074.9},
          {"layer":"bike","season":null,"tiles":1,"cellCount":7,"areaSquareMeters":241.2}
        ]}
        """
    private static let seasons = """
        {"seasons":[
          {"number":0,"name":"Сезон 0 (бета)","startsAtMs":1794776400000,"endsAtMs":1795986000000},
          {"number":1,"name":"Сезон 1","startsAtMs":1795986000000,"endsAtMs":null}
        ],"current":0}
        """

    private static func decode<T: Decodable>(_ json: String, as type: T.Type) throws -> T {
        try JSONDecoder().decode(type, from: Data(json.utf8))
    }

    @Test("Слой «Пешком»: за всё время и текущий сезон с именем из /seasons; процентов нет — «появится позже»")
    func foot() throws {
        let stats = ExplorationSummary(
            summary: try Self.decode(Self.summary, as: Components.Schemas.FogSummaryResponse.self),
            seasons: try Self.decode(Self.seasons, as: Components.Schemas.SeasonsResponse.self))

        #expect(stats.allTime.title == "За всё время")
        #expect(stats.allTime.squareMeters == 42580.5 && stats.allTime.cells == 1234)
        #expect(stats.seasons.map(\.title) == ["Сезон 0 (бета)"])
        #expect(stats.seasons.first?.isCurrent == true)
        #expect(stats.seasons.first?.cells == 321)
        #expect(!stats.hasShares && stats.allTime.districts == nil)
        #expect(ExplorationText.area(stats.allTime.squareMeters) == "4,26\u{00A0}га")
    }

    @Test("Слой «Вело» — свои строки; пустая сводка — нули, а не ошибка; сезон без имени — «Сезон N»")
    func otherLayers() throws {
        let summary = try Self.decode(Self.summary, as: Components.Schemas.FogSummaryResponse.self)
        let bike = ExplorationSummary(summary: summary, seasons: nil, layer: .bike)
        #expect(bike.allTime.cells == 7 && bike.seasons.isEmpty)

        let empty = ExplorationSummary(summary: .init(layers: []), seasons: nil)
        #expect(empty.allTime.squareMeters == 0 && empty.allTime.cells == 0)

        let unnamed = ExplorationSummary(summary: summary, seasons: nil)
        #expect(unnamed.seasons.map(\.title) == ["Сезон 0"])
        #expect(unnamed.seasons.first?.isCurrent == false)
    }

    @Test("Проценты: меньше процента — два знака, дальше — один, за пределами 0–100 — обрезаются")
    func percents() {
        #expect(ExplorationText.percent(4.83) == "4,8\u{00A0}%")
        #expect(ExplorationText.percent(0.35) == "0,35\u{00A0}%")
        #expect(ExplorationText.percent(100.4) == "100,0\u{00A0}%")
        #expect(ExplorationText.percent(-1) == "0,00\u{00A0}%")
        let period = ExplorationSummary.Period(
            season: nil, title: "За всё время", squareMeters: 1, cells: 1, brestPercent: 1.37,
            districts: [.init(key: "arena", name: "Арена БрГТУ", percent: 18.9, proposal: true)])
        #expect(ExplorationSummary(allTime: period).hasShares)
    }

    @Test("День сезона: первый день, «день 10 из 14», последний; до начала и после конца — нет")
    func seasonDay() throws {
        let seasons = try Self.decode(Self.seasons, as: Components.Schemas.SeasonsResponse.self)
        let start: Int64 = 1_794_776_400_000
        let day: Int64 = 86_400_000

        #expect(SeasonProgress(seasons: seasons, nowMs: start)?.text == "день 1 из 14")
        #expect(SeasonProgress(seasons: seasons, nowMs: start + 9 * day + 5)?.text == "день 10 из 14")
        #expect(SeasonProgress(seasons: seasons, nowMs: start + 14 * day - 1)?.day == 14)
        #expect(SeasonProgress(seasons: seasons, nowMs: start - 1) == nil)
        #expect(SeasonProgress(seasons: seasons, nowMs: start + 14 * day) == nil)

        var open = seasons
        open.current = 1
        #expect(SeasonProgress(seasons: open, nowMs: start + 16 * day)?.text == "день 3")
    }
}
