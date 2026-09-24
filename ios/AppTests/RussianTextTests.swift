import Foundation
import GameCore
import Sync
import Testing

@testable import Gorodki

/// Тексты для игрока — по-русски (PLAN.md, §6.10): формы множественного числа из каталога строк приложения
/// (`Localizable.xcstrings` собирается только Xcode, поэтому проверка — здесь, внутри приложения на симуляторе) и числа
/// с запятой. Язык симулятора CI — английский: по английскому правилу вышло бы «2 петель» и «1.23 км».
@Suite("Тексты по-русски: множественные формы и запятая")
struct RussianTextTests {
    @Test(
        "Петли: 1 петля, 2 петли, 5 петель, 11 петель, 21 петля",
        arguments: [(1, "1 петля"), (2, "2 петли"), (5, "5 петель"), (11, "11 петель"), (21, "21 петля")])
    func loops(_ count: Int, _ expected: String) {
        #expect(CountText.loops(count) == expected)
    }

    @Test("Остальные счётные слова: одна, две и пять штук, и «0» — как «5»")
    func otherCounts() {
        #expect([1, 3, 5, 0].map(CountText.points) == ["1 точка", "3 точки", "5 точек", "0 точек"])
        #expect([1, 3, 5, 0].map(CountText.fogCells) == ["1 клетка", "3 клетки", "5 клеток", "0 клеток"])
        #expect([1, 3, 5, 0].map(CountText.parcels) == ["1 участок", "3 участка", "5 участков", "0 участков"])
        #expect([1, 3, 5, 0].map(CountText.pieces) == ["1 кусок", "3 куска", "5 кусков", "0 кусков"])
        #expect([1, 3, 5, 0].map(CountText.groups) == ["1 группа", "3 группы", "5 групп", "0 групп"])
        #expect([1, 3, 5, 0].map(CountText.tiles) == ["1 тайл", "3 тайла", "5 тайлов", "0 тайлов"])
    }

    @Test("Плашка забега: «1,23 км» и «2 петли · туман 0,12 га», а не «1.23 км» и «Петель 2»")
    func runActivity() {
        var stats = RunStats()
        stats.distanceMeters = 1_234
        stats.loops = 2
        stats.fogAreaSquareMeters = 1_234

        let content = RunController.activityContent(stats)

        #expect(content.title == "Забег · 1,23\u{00A0}км")
        #expect(content.detail == "2 петли · туман 0,12\u{00A0}га")
    }

    @Test("Плашка прогулки: «1,23 км», «21 петля · 5 точек»")
    func walkActivity() {
        var stats = WalkStats(api: .liveUpdates, startedAt: .now)
        stats.distanceMeters = 1_234
        stats.loops = 21
        stats.fixes = 5

        let content = WalkLab.activityContent(stats)

        #expect(content.title == "Прогулка · 1,23\u{00A0}км")
        #expect(content.detail == "21 петля · 5 точек · разрывов >15 с: 0")
    }

    @Test("Запаздывание датчиков в сводке прогулки — с запятой: «медиана 3,1 с»")
    func lagSummary() {
        #expect(
            WalkLab.lagSummary([3.14, 1, 12])
                == "медиана 3,1\u{00A0}с · 99 % — 12,0\u{00A0}с · макс 12,0\u{00A0}с (n = 3)")
    }
}
