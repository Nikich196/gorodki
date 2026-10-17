import DesignSystem
import Foundation
import GorodkiAPI
import Observation

/// Состояние раздела: ещё не загружали, загружено или не вышло (текст — `SocialFailure.message`).
enum Loadable<Value> {
    case idle
    case loaded(Value)
    case failed(SocialFailure)

    var value: Value? {
        if case .loaded(let value) = self { value } else { nil }
    }

    var failure: SocialFailure? {
        if case .failed(let failure) = self { failure } else { nil }
    }

    /// Источник ответил — загрузилось или нет.
    var isSettled: Bool {
        if case .idle = self { false } else { true }
    }
}

/// Игровые разделы (PLAN.md, §3.7, §3.11–§3.15): серия, «Рюкзак», «Коллекция», карточка недели, Зал славы, «Короли
/// участков», дуэли. Каждый грузится сам, когда его экран показан; ошибка одного не мешает другим.
@MainActor
@Observable
final class GameModel {
    private(set) var streak: Loadable<StreakInfo> = .idle
    private(set) var inventory: Loadable<InventoryInfo> = .idle
    private(set) var collection: Loadable<CollectionInfo> = .idle
    private(set) var weekly: Loadable<WeeklyCard> = .idle
    private(set) var hallOfFame: Loadable<HallOfFame> = .idle
    private(set) var segments: Loadable<SegmentList> = .idle
    private(set) var duels: Loadable<[DuelInfo]> = .idle
    /// Лига «Королей»: у «Бега» и «Вело» свои короны.
    var kingsLeague: Components.Schemas.League = .run {
        didSet { if kingsLeague != oldValue { segments = .idle } }
    }
    /// Не вышло действие (активировать фишку, ответить на вызов) — строкой над списком.
    var actionError: String?
    /// Идёт действие — кнопки ждут.
    private(set) var busy = false
    /// Новые награды в очереди показа (`RewardRevealView` поверх приложения) — по одной, каждая один раз.
    private(set) var rewards: [Reward] = []

    @ObservationIgnored let source: (any GameBackend)?

    init(source: (any GameBackend)?) {
        self.source = source
    }

    // MARK: - Загрузка

    private func fetch<Value>(_ load: (any GameBackend) async throws -> Value) async -> Loadable<Value> {
        guard let source else { return .failed(.offline) }
        do {
            return .loaded(try await load(source))
        } catch {
            return .failed(SocialFailure.from(error))
        }
    }

    func loadStreak() async { streak = await fetch { try await $0.streak() } }
    func loadInventory() async { inventory = await fetch { try await $0.inventory() } }
    func loadCollection() async {
        collection = await fetch { try await $0.collection() }
        if let caches = collection.value?.caches {
            offer(caches.compactMap(Self.reward), kind: "caches")
        }
    }
    func loadWeekly() async { weekly = await fetch { try await $0.weekly(week: nil) } }
    func loadHallOfFame() async { hallOfFame = await fetch { try await $0.hallOfFame() } }
    func loadDuels() async { duels = await fetch { try await $0.duels() } }

    func loadSegments() async {
        let league = kingsLeague
        segments = await fetch { try await $0.segments(league: league) }
        if let list = segments.value {
            offer(list.segments.compactMap { Self.crown($0, league: list.league) }, kind: "crowns")
        }
    }

    // MARK: - Награды

    /// Новые награды — в очередь показа; уже показанные и уже стоящие в очереди — нет.
    func offer(_ list: [Reward], kind: String) {
        let fresh = SeenRewards.unseen(list, kind: kind).filter { reward in !rewards.contains { $0.id == reward.id } }
        rewards += fresh
    }

    /// Награду показали и забрали — следующая.
    func rewardShown() {
        guard let first = rewards.first else { return }
        SeenRewards.markSeen(first.id)
        rewards.removeFirst()
    }

    private static func reward(_ cache: CacheBadge) -> Reward? {
        guard cache.foundAtMs != nil else { return nil }
        return Reward(
            id: "cache:\(cache.id)", symbol: cache.rarity.symbol, color: cache.rarity.color,
            label: cache.rarity.title.uppercased() + " ТАЙНИК", title: cache.name ?? "Тайник",
            subtitle: cache.goldFrame
                ? "Ты нашёл его первым — золотая рамка" : cache.finderNumber.map { "№ \($0) нашедших" } ?? "Найден")
    }

    private static func crown(_ segment: Components.Schemas.SegmentSummary, league: Components.Schemas.League)
        -> Reward?
    {
        guard let crown = segment.seasonCrown, crown.me else { return nil }
        return Reward(
            id: "crown:\(segment.id):\(league.rawValue)", symbol: "crown.fill", color: Brand.amber.day.color,
            label: "КОРОНА СЕЗОНА", title: segment.name,
            subtitle: "Лучшее время: " + SegmentTime.text(milliseconds: crown.timeMs))
    }

    /// Всё для строк раздела «Игра» в профиле — параллельно.
    func loadOverview() async {
        async let streak: Void = loadStreak()
        async let inventory: Void = loadInventory()
        async let collection: Void = loadCollection()
        async let duels: Void = loadDuels()
        async let segments: Void = loadSegments()
        _ = await (streak, inventory, collection, duels, segments)
    }

    // MARK: - Действия

    private func perform(_ action: (any GameBackend) async throws -> Void) async {
        guard let source, !busy else { return }
        busy = true
        defer { busy = false }
        do {
            try await action(source)
            actionError = nil
        } catch {
            actionError = SocialFailure.from(error).message
        }
    }

    /// Активировать фишку рюкзака (PLAN.md, §3.11: только стоя или перед «Стартом» — забег идёт, кнопки нет).
    func activate(_ item: InventoryItem) async {
        await perform { source in
            let updated = try await source.activate(itemId: item.id)
            if var info = inventory.value, let index = info.items.firstIndex(where: { $0.id == updated.id }) {
                info.items[index] = updated
                inventory = .loaded(info)
            }
        }
    }

    func accept(_ duel: DuelInfo) async {
        await perform { source in
            let updated = try await source.acceptDuel(id: duel.id)
            replace(updated)
        }
    }

    func decline(_ duel: DuelInfo) async {
        await perform { source in
            try await source.declineDuel(id: duel.id)
            if var list = duels.value {
                list.removeAll { $0.id == duel.id }
                duels = .loaded(list)
            }
        }
    }

    /// Вызов на дуэль: `true` — отправлен.
    func challenge(_ request: Components.Schemas.CreateDuelRequest) async -> Bool {
        var sent = false
        await perform { source in
            let duel = try await source.createDuel(request)
            duels = .loaded([duel] + (duels.value ?? []).filter { $0.id != duel.id })
            sent = true
        }
        return sent
    }

    private func replace(_ duel: DuelInfo) {
        guard var list = duels.value else { return }
        if let index = list.firstIndex(where: { $0.id == duel.id }) {
            list[index] = duel
        } else {
            list.insert(duel, at: 0)
        }
        duels = .loaded(list)
    }

    // MARK: - Подписи строк профиля

    /// «2 фишки из 12» — для строки «Рюкзак».
    var inventorySummary: String? {
        inventory.value.map { "\($0.items.count) из \($0.slots) ячеек" }
    }

    /// «Найдено 1 из 2».
    var collectionSummary: String? {
        collection.value.map { info in
            "Найдено \(info.caches.filter { $0.foundAtMs != nil }.count) из \(info.caches.count)"
        }
    }

    /// «Идёт: 1 · ждёт ответа: 1».
    var duelsSummary: String? {
        guard let list = duels.value else { return nil }
        let active = list.filter { $0.status == .active }.count
        let pending = list.filter { $0.status == .pending }.count
        if active == 0 && pending == 0 { return "Вызови друга 1×1" }
        return [active > 0 ? "идёт: \(active)" : nil, pending > 0 ? "ждёт ответа: \(pending)" : nil]
            .compactMap(\.self).joined(separator: " · ")
    }
}
