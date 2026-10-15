import Foundation
import GorodkiAPI

// Данные игровых разделов профиля и рейтинга (PLAN.md, §3.7, §3.11–§3.15): серия, «Рюкзак», «Коллекция», карточка
// недели, Зал славы, «Короли участков», дуэли. Контракт — заготовки C15 (docs/guides/egor-server.md, раздел 7.0);
// с 15.10 их делает Claude (решение Никиты «всё работает сразу»). Экраны знают только эти протоколы: живые данные —
// `APIGameSource`, режим фикстур и демо — образцы `contracts/samples` (`SampleGameSource`).

typealias StreakInfo = Components.Schemas.StreakResponse
typealias InventoryInfo = Components.Schemas.InventoryResponse
typealias InventoryItem = Components.Schemas.InventoryItemResponse
typealias CollectionInfo = Components.Schemas.CollectionResponse
typealias CacheBadge = Components.Schemas.CacheBadgeResponse
typealias WeeklyCard = Components.Schemas.WeeklyCardResponse
typealias HallOfFame = Components.Schemas.HallOfFameResponse
typealias SegmentList = Components.Schemas.SegmentListResponse
typealias SegmentInfo = Components.Schemas.SegmentResponse
typealias SegmentBoard = Components.Schemas.SegmentLeaderboardResponse
typealias DuelInfo = Components.Schemas.DuelResponse

/// Серия, рюкзак, коллекция, карточка недели, Зал славы.
protocol ProgressSource: Sendable {
    func streak() async throws -> StreakInfo
    func inventory() async throws -> InventoryInfo
    func activate(itemId: String) async throws -> InventoryItem
    func collection() async throws -> CollectionInfo
    /// `week` — понедельник недели «yyyy-MM-dd»; `nil` — прошлая полная неделя.
    func weekly(week: String?) async throws -> WeeklyCard
    func hallOfFame() async throws -> HallOfFame
}

/// «Короли участков»: отрезки лиги, отрезок с коронами, таблица времён.
protocol SegmentSource: Sendable {
    func segments(league: Components.Schemas.League) async throws -> SegmentList
    func segment(id: String, league: Components.Schemas.League) async throws -> SegmentInfo
    func board(id: String, league: Components.Schemas.League, season: Int?) async throws -> SegmentBoard
}

/// Дуэли 1×1 — только со взаимными друзьями одной лиги (PLAN.md, §3.14).
protocol DuelSource: Sendable {
    func duels() async throws -> [DuelInfo]
    func createDuel(_ request: Components.Schemas.CreateDuelRequest) async throws -> DuelInfo
    func acceptDuel(id: String) async throws -> DuelInfo
    func declineDuel(id: String) async throws
}

typealias GameBackend = ProgressSource & SegmentSource & DuelSource

extension SocialFailure {
    /// Тексты отказов игровых адресов (`contracts/openapi.v1.json`).
    static func gameText(code: String?) -> String? {
        switch code {
        case "duel_account_too_new": "Дуэли — для аккаунтов старше 48 часов."
        case "duel_invalid": "Так вызвать нельзя: проверь лигу, срок и метрику."
        case "duel_limit": "Не больше двух дуэлей одновременно."
        case "duel_not_found": "Дуэли уже нет — список обновлён."
        case "duel_opponent_not_found": "Вызвать можно только взаимного друга."
        case "duel_pair_week": "С этим другом — не чаще раза в неделю."
        case "duel_stake_invalid": "Эту фишку поставить нельзя."
        case "item_active": "Фишка уже работает."
        case "item_expired": "Срок фишки вышел."
        case "item_not_found": "Фишки уже нет — рюкзак обновлён."
        case "segment_not_found": "Отрезка уже нет."
        default: nil
        }
    }
}

/// Живые данные: сгенерированный клиент API (`AppDependencies.api`).
struct APIGameSource: GameBackend {
    let api: any APIProtocol

    func streak() async throws -> StreakInfo {
        switch try await api.getStreak() {
        case .ok(let ok): return try ok.body.json
        case .notFound: throw SocialFailure.signedOut
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func inventory() async throws -> InventoryInfo {
        switch try await api.getInventory() {
        case .ok(let ok): return try ok.body.json
        case .notFound: throw SocialFailure.signedOut
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func activate(itemId: String) async throws -> InventoryItem {
        switch try await api.activateItem(path: .init(id: itemId)) {
        case .ok(let ok): return try ok.body.json
        case .notFound(let response):
            throw SocialFailure.rejected(code: try? response.body.application_problem_plus_json.code)
        case .conflict(let response):
            throw SocialFailure.rejected(code: try? response.body.application_problem_plus_json.code)
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func collection() async throws -> CollectionInfo {
        switch try await api.getCollection() {
        case .ok(let ok): return try ok.body.json
        case .notFound: throw SocialFailure.signedOut
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func weekly(week: String?) async throws -> WeeklyCard {
        switch try await api.getWeeklyCard(query: .init(week: week)) {
        case .ok(let ok): return try ok.body.json
        case .badRequest: throw SocialFailure.rejected(code: "weekly_invalid")
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func hallOfFame() async throws -> HallOfFame {
        switch try await api.getHallOfFame() {
        case .ok(let ok): return try ok.body.json
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func segments(league: Components.Schemas.League) async throws -> SegmentList {
        switch try await api.listSegments(query: .init(league: league.rawValue)) {
        case .ok(let ok): return try ok.body.json
        case .badRequest: throw SocialFailure.rejected(code: "segment_invalid")
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func segment(id: String, league: Components.Schemas.League) async throws -> SegmentInfo {
        switch try await api.getSegment(path: .init(id: id), query: .init(league: league.rawValue)) {
        case .ok(let ok): return try ok.body.json
        case .badRequest: throw SocialFailure.rejected(code: "segment_invalid")
        case .notFound: throw SocialFailure.rejected(code: "segment_not_found")
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func board(id: String, league: Components.Schemas.League, season: Int?) async throws -> SegmentBoard {
        switch try await api.getSegmentLeaderboard(
            path: .init(id: id), query: .init(league: league.rawValue, season: season.map(Int32.init)))
        {
        case .ok(let ok): return try ok.body.json
        case .badRequest: throw SocialFailure.rejected(code: "segment_invalid")
        case .notFound: throw SocialFailure.rejected(code: "segment_not_found")
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func duels() async throws -> [DuelInfo] {
        switch try await api.listDuels() {
        case .ok(let ok): return try ok.body.json.duels
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func createDuel(_ request: Components.Schemas.CreateDuelRequest) async throws -> DuelInfo {
        switch try await api.createDuel(body: .json(request)) {
        case .created(let created): return try created.body.json
        case .badRequest(let response):
            throw SocialFailure.rejected(code: try? response.body.application_problem_plus_json.code)
        case .notFound(let response):
            throw SocialFailure.rejected(code: try? response.body.application_problem_plus_json.code)
        case .conflict(let response):
            throw SocialFailure.rejected(code: try? response.body.application_problem_plus_json.code)
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func acceptDuel(id: String) async throws -> DuelInfo {
        // Ставка в ответ — по желанию (PLAN.md, §3.14); пока без неё: «Рюкзак» на сервере ещё не принимает ставки.
        switch try await api.acceptDuel(path: .init(id: id), body: .json(.init(stakeItemId: nil))) {
        case .ok(let ok): return try ok.body.json
        case .notFound: throw SocialFailure.rejected(code: "duel_not_found")
        case .conflict(let response):
            throw SocialFailure.rejected(code: try? response.body.application_problem_plus_json.code)
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func declineDuel(id: String) async throws {
        switch try await api.declineDuel(path: .init(id: id)) {
        case .noContent: return
        case .notFound: throw SocialFailure.rejected(code: "duel_not_found")
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }
}

/// Образцы контракта (`contracts/samples`) — режим фикстур, «Посмотреть демо» и гость.
struct SampleGameSource: GameBackend {
    private func sample<Value: Decodable>(_ name: String, as type: Value.Type) throws -> Value {
        guard let value = Fixtures.sample(name, as: type) else { throw SocialFailure.unexpected(0) }
        return value
    }

    func streak() async throws -> StreakInfo { try sample("streak", as: StreakInfo.self) }
    func inventory() async throws -> InventoryInfo { try sample("inventory", as: InventoryInfo.self) }

    func activate(itemId: String) async throws -> InventoryItem {
        guard var item = try sample("inventory", as: InventoryInfo.self).items.first(where: { $0.id == itemId }) else {
            throw SocialFailure.rejected(code: "item_not_found")
        }
        item.active = true
        return item
    }

    func collection() async throws -> CollectionInfo { try sample("collection", as: CollectionInfo.self) }
    func weekly(week: String?) async throws -> WeeklyCard { try sample("weekly", as: WeeklyCard.self) }
    func hallOfFame() async throws -> HallOfFame { try sample("hall-of-fame", as: HallOfFame.self) }

    func segments(league: Components.Schemas.League) async throws -> SegmentList {
        try sample("segments", as: SegmentList.self)
    }

    func segment(id: String, league: Components.Schemas.League) async throws -> SegmentInfo {
        try sample("segment", as: SegmentInfo.self)
    }

    func board(id: String, league: Components.Schemas.League, season: Int?) async throws -> SegmentBoard {
        try sample("segment-leaderboard", as: SegmentBoard.self)
    }

    func duels() async throws -> [DuelInfo] { try sample("duels", as: Components.Schemas.DuelsResponse.self).duels }

    func createDuel(_ request: Components.Schemas.CreateDuelRequest) async throws -> DuelInfo {
        guard var duel = try await duels().first else { throw SocialFailure.unexpected(0) }
        duel.status = .pending
        duel.metric = request.metric
        duel.days = request.days
        return duel
    }

    func acceptDuel(id: String) async throws -> DuelInfo {
        guard var duel = try await duels().first(where: { $0.id == id }) else {
            throw SocialFailure.rejected(code: "duel_not_found")
        }
        duel.status = .active
        return duel
    }

    func declineDuel(id: String) async throws {}
}
