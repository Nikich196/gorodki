import Foundation
import GameCore
import Sync
import Testing

@testable import Gorodki

/// Пробный забег «Лаборатории» (`ProbeRun`): счёт, переживающий перезапуск приложения, сводка и плашка — по-русски.
/// Сам путь забега (трекер, продолжение, очередь, стирание) проверяют тесты пакета Sync на Linux; что настоящий «Старт»
/// ждёт пробный — `RunControllerTests`.
@Suite("Пробный забег «Лаборатории»: счёт, сводка, плашка")
struct ProbeRunTests {
    @Test("Счёт: отброшенные судьёй — записанные минус принятые; после перезапуска счёт до него прибавляется")
    func counters() {
        var stats = RunStats()
        stats.points = 120
        stats.acceptedPoints = 100
        stats.loops = 1
        stats.fogCells = 40
        stats.distanceMeters = 150

        let current = ProbeCounters(stats)
        let total = current + current

        #expect(current.accepted == 100 && current.rejected == 20 && current.loops == 1 && current.fogCells == 40)
        #expect(total.accepted == 200 && total.rejected == 40 && total.loops == 2 && total.fogCells == 80)
        #expect(total.distanceMeters == 300)
        #expect(ProbeCounters() + current == current)
    }

    @Test("Сводка: числа по-русски, без координат; пока кусков нет — без строк очереди")
    func summary() {
        let record = ProbeRecord(runId: UUID(), startedAt: .now, relaunches: 1)
        var counters = ProbeCounters()
        counters.accepted = 1_234
        counters.rejected = 12
        counters.loops = 2
        counters.fogCells = 21
        counters.distanceMeters = 1_234
        var queued = QueuedRunSummary()
        queued.chunks = 5
        queued.points = 1_246
        queued.claims = 2
        queued.gapsOverLimit = 1
        queued.longestGapSeconds = 17
        queued.recordedSeconds = 34 * 60 + 5
        queued.sensorLagSeconds = 12.3

        let text = ProbeRun.summaryText(record, counters: counters, queued: queued)
        let head = [
            "Пробный забег · 34 мин · перезапусков: 1",
            "Точки: принято \(NumberText.integer(1_234)), отброшено судьёй 12",
            "Петель: 2 · туман на телефоне: 21 клетка",
            "Дистанция: 1,23\u{00A0}км",
        ]
        #expect(
            text.split(separator: "\n").map(String.init) == head + [
                "Разрывов GPS > 15 с: 1 · самый длинный: 17\u{00A0}с",
                "Отметка датчиков отстаёт на 12,3\u{00A0}с",
                "В очереди: 5 кусков · \(NumberText.integer(1_246)) точек · заявок: 2",
            ])

        var early = head
        early[0] = "Пробный забег · 0 мин · перезапусков: 1"
        #expect(ProbeRun.summaryText(record, counters: counters, queued: nil) == early.joined(separator: "\n"))
        #expect(ProbeRun.summaryText(nil, counters: ProbeCounters(), queued: nil) == "Пробного забега ещё не было.")
    }

    @Test("Плашка пробного забега: «Пробный забег · 1,23 км», «2 петли · принято 5 точек»")
    func activity() {
        var counters = ProbeCounters()
        counters.distanceMeters = 1_234
        counters.loops = 2
        counters.accepted = 5

        let content = ProbeRun.activityContent(counters)

        #expect(content.title == "Пробный забег · 1,23\u{00A0}км")
        #expect(content.detail == "2 петли · принято 5 точек")
    }
}
