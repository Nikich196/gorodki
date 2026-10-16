import Foundation
import GorodkiAPI
import Testing

@testable import Gorodki

/// Игровые разделы (App/Game): модель на образцах `contracts/samples` (`SampleGameSource`) и без источника.
@Suite("Игровые разделы: серия, рюкзак, коллекция, дуэли, «Короли»")
@MainActor
struct GameModelTests {
    @Test("Обзор профиля: серия, рюкзак, коллекция и дуэли грузятся вместе, подписи строк — по-русски")
    func overview() async {
        let model = GameModel(source: SampleGameSource())
        await model.loadOverview()
        #expect(model.streak.value?.days == 5)
        #expect(model.inventorySummary == "2 из 12 ячеек")
        #expect(model.collectionSummary == "Найдено 1 из 2")
        #expect(model.duelsSummary == "идёт: 1 · ждёт ответа: 1")
    }

    @Test("Без сервера — «нет связи», а не пустой экран")
    func offline() async {
        let model = GameModel(source: nil)
        await model.loadStreak()
        await model.loadSegments()
        #expect(model.streak.failure == .offline)
        #expect(model.segments.failure == .offline)
    }

    @Test("Активация фишки обновляет её в рюкзаке, ответ на вызов — дуэль в списке")
    func actions() async {
        let model = GameModel(source: SampleGameSource())
        await model.loadInventory()
        await model.loadDuels()
        guard let freeze = model.inventory.value?.items.first(where: { $0.kind == .streakFreeze }) else {
            Issue.record("В образце нет «Заморозки»")
            return
        }
        await model.activate(freeze)
        #expect(model.inventory.value?.items.first(where: { $0.id == freeze.id })?.active == true)
        guard let pending = model.duels.value?.first(where: { $0.status == .pending }) else {
            Issue.record("В образце нет вызова")
            return
        }
        #expect(pending.isIncoming)
        await model.accept(pending)
        #expect(model.duels.value?.first(where: { $0.id == pending.id })?.status == .active)
        await model.decline(pending)
        #expect(model.duels.value?.contains(where: { $0.id == pending.id }) == false)
        #expect(model.actionError == nil)
    }

    @Test("«Короли»: смена лиги сбрасывает отрезки — экран перезапросит свою лигу")
    func kingsLeague() async {
        let model = GameModel(source: SampleGameSource())
        await model.loadSegments()
        #expect(model.segments.value?.segments.count == 2)
        model.kingsLeague = .bike
        if case .idle = model.segments {} else { Issue.record("Отрезки прежней лиги остались") }
    }

    @Test("Время отрезка — минуты, секунды и десятые; метрики дуэли — в своих единицах")
    func formats() {
        #expect(SegmentTime.text(milliseconds: 71_400) == "1:11,4")
        #expect(SegmentTime.text(milliseconds: 9_960) == "0:10,0")
        #expect(Components.Schemas.DuelMetric.distance.format(8_420.5) == "8,42\u{00A0}км")
        #expect(Components.Schemas.DuelMetric.capturedArea.format(12_500) == "1,25\u{00A0}га")
        #expect(Components.Schemas.DuelMetric.segmentTime.format(nil) == "—")
    }

    @Test("Код друга без дефиса — дефис подставляется (сервер #143 ищет XXXX-XXXX)")
    func friendCode() {
        #expect(FriendCode.normalized(" r4t8kd2m ") == "R4T8-KD2M")
        #expect(FriendCode.normalized("R4T8-KD2M") == "R4T8-KD2M")
        #expect(FriendCode.normalized("abc") == "ABC")
    }

    @Test("Достижения: прогресс из чисел профиля и разделов, неизвестное — закрыто без ложного прогресса")
    func achievements() {
        let unknown = Achievements.evaluate(AchievementInputs())
        #expect(unknown.allSatisfy { !$0.unlocked })
        #expect(unknown.first { $0.id == "ten-km" }?.progressText == "—")
        let some = Achievements.evaluate(
            AchievementInputs(
                runs: 12, distanceMeters: 3_200, exploredSquareMeters: 42_600, brestPercent: 1.4, arenaPercent: 18.9,
                streakDays: 5, crowns: 0, cachesFound: 1, duelWins: 0, inClan: true))
        let byId = Dictionary(uniqueKeysWithValues: some.map { ($0.id, $0) })
        #expect(byId["first-run"]?.unlocked == true)
        #expect(byId["ten-km"]?.progressText == "3,2 из 10\u{00A0}км")
        #expect(byId["hectare"]?.unlocked == true)
        #expect(byId["brest-1"]?.unlocked == true)
        #expect(byId["arena-100"]?.unlocked == false)
        #expect(byId["streak-7"]?.progressText == "5 из 7")
        #expect(byId["cache"]?.unlocked == true && byId["king"]?.unlocked == false)
        #expect(byId["clan"]?.unlocked == true)
    }
}
