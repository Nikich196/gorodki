import Foundation
import GorodkiAPI

// Данные социальных экранов (PLAN.md, §3.4–§3.8, §3.17): адреса и образцы — контракт #149 (docs/guides/egor-server.md,
// раздел 7.0). Экраны знают только эти протоколы: живые данные — `APISocialSource`, режим фикстур — образцы
// `contracts/samples` (`SampleSocialSource`), тесты — свои подмены. Сервер за большинством адресов пока заглушка Егора
// (500) — экран честно говорит «пока не работает на сервере» (`SocialFailure.notReady`).

typealias TerritoryBoard = Components.Schemas.TerritoryLeaderboardResponse
typealias ExplorationBoard = Components.Schemas.ExplorationLeaderboardResponse
typealias ClanInfo = Components.Schemas.ClanResponse
typealias MyClanInfo = Components.Schemas.MyClanResponse
typealias FriendsInfo = Components.Schemas.FriendsResponse
typealias FriendInfo = Components.Schemas.FriendResponse
typealias FeedPage = Components.Schemas.FeedResponse
typealias InboxPage = Components.Schemas.InboxResponse

/// Рейтинги: территория — очки сезона по лиге (без сезона — текущий), «Исследование» — открытые гектары слоя.
protocol LeaderboardSource: Sendable {
    func territory(league: Components.Schemas.League) async throws -> TerritoryBoard
    /// `layer` — foot, bike или total; сезон не задан — за всё время.
    func exploration(layer: String) async throws -> ExplorationBoard
}

/// Клан: свой, свободные оттенки, создать, вступить по коду, выйти, новый код-приглашение.
protocol ClanSource: Sendable {
    func myClan() async throws -> MyClanInfo
    func freeHues() async throws -> [Int]
    func createClan(name: String, hue: Int?) async throws -> ClanInfo
    func joinClan(code: String) async throws -> ClanInfo
    func leaveClan() async throws
    func newInviteCode() async throws -> ClanInfo
}

/// Друзья — только взаимные: свой код, добавить по коду, принять заявку, удалить (отклонить, отозвать).
protocol FriendsSource: Sendable {
    func friends() async throws -> FriendsInfo
    func addFriend(code: String) async throws -> FriendInfo
    func acceptFriend(playerId: String) async throws -> FriendInfo
    func removeFriend(playerId: String) async throws
}

/// Лента: посты друзей и клана или всех, респект, жалоба, блокировка автора.
protocol FeedSource: Sendable {
    func feed(scope: FeedScope) async throws -> FeedPage
    func respect(postId: String) async throws
    func report(postId: String) async throws
    func block(playerId: String) async throws
}

/// «Входящие»: события и число непрочитанных, «прочитано» — всё не новее момента.
protocol InboxSource: Sendable {
    func inbox() async throws -> InboxPage
    func markRead(upToAtMs: Int64) async throws
}

/// Фильтр ленты (`GET /feed?scope=`): по умолчанию «друзья и клан» (PLAN.md, §3.8).
enum FeedScope: String, CaseIterable, Identifiable, Sendable {
    case friends, all

    var id: String { rawValue }

    var title: String {
        switch self {
        case .friends: "Друзья и клан"
        case .all: "Все"
        }
    }
}

/// Почему не вышло: текст для игрока — `message`.
enum SocialFailure: Error, Equatable, Sendable {
    /// Адрес — ещё заглушка Егора (500): задача на сервере не сделана.
    case notReady
    /// Нет связи с сервером (или адрес сервера не задан в сборке).
    case offline
    /// Нужен вход (401).
    case signedOut
    /// Сервер отказал по существу: код из `application/problem+json`.
    case rejected(code: String?)
    /// Ответ не из контракта.
    case unexpected(Int)

    /// Ответ, не описанный в контракте: 500 от заглушки — «пока не работает», 401 — нужен вход.
    static func status(_ code: Int) -> SocialFailure {
        switch code {
        case 401: .signedOut
        case 500, 501: .notReady
        default: .unexpected(code)
        }
    }

    /// Любая ошибка вызова: наша — как есть, остальное (сеть, разбор) — «нет связи».
    static func from(_ error: any Error) -> SocialFailure {
        error as? SocialFailure ?? .offline
    }

    var message: String {
        switch self {
        case .notReady: "Пока не работает на сервере — скоро появится."
        case .offline: "Нет связи с сервером. Потяни вниз, чтобы попробовать ещё раз."
        case .signedOut: "Войди в аккаунт — без входа здесь пусто."
        case .rejected(let code): Self.text(code: code)
        case .unexpected(let status): "Сервер ответил неожиданно (\(status)). Попробуй позже."
        }
    }

    /// Коды ошибок из описаний адресов (`contracts/openapi.v1.json`).
    private static func text(code: String?) -> String {
        switch code {
        case "clan_name_invalid": "Название — от 3 до 24 символов: буквы, цифры, пробел, дефис, без грубых слов."
        case "clan_name_taken": "Такое название уже занято."
        case "clan_already_member": "Ты уже в клане."
        case "clan_join_cooldown": "После выхода из клана вступить в новый можно через 72 часа."
        case "clan_hue_invalid", "clan_hue_taken": "Этот оттенок уже заняли — выбери другой."
        case "clan_code_invalid": "Такого кода нет — проверь его у лидера или офицера."
        case "clan_full": "В клане уже 12 человек."
        case "clan_not_member": "Ты уже не в клане."
        case "friend_code_invalid": "Такого кода нет — проверь его у друга."
        case "friend_self": "Это твой собственный код."
        case "friend_not_found", "friend_request_not_found": "Заявки уже нет — список обновлён."
        case "post_not_found": "Поста уже нет или он тебе не виден."
        case "block_self": "Себя заблокировать нельзя."
        default: "Сервер отказал. Попробуй ещё раз."
        }
    }
}

/// Живые данные: сгенерированный клиент API (`AppDependencies.api`).
struct APISocialSource: LeaderboardSource, ClanSource, FriendsSource, FeedSource, InboxSource {
    let api: any APIProtocol

    func territory(league: Components.Schemas.League) async throws -> TerritoryBoard {
        switch try await api.getTerritoryLeaderboard(query: .init(league: league.rawValue)) {
        case .ok(let ok): return try ok.body.json
        case .badRequest: throw SocialFailure.rejected(code: "leaderboard_invalid")
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func exploration(layer: String) async throws -> ExplorationBoard {
        switch try await api.getExplorationLeaderboard(query: .init(layer: layer)) {
        case .ok(let ok): return try ok.body.json
        case .badRequest: throw SocialFailure.rejected(code: "leaderboard_invalid")
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func myClan() async throws -> MyClanInfo {
        switch try await api.getMyClan() {
        case .ok(let ok): return try ok.body.json
        case .notFound: throw SocialFailure.signedOut
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func freeHues() async throws -> [Int] {
        switch try await api.getClanHues() {
        case .ok(let ok): return try ok.body.json.free.map(Int.init)
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func createClan(name: String, hue: Int?) async throws -> ClanInfo {
        switch try await api.createClan(body: .json(.init(name: name, hue: hue.map(Int32.init)))) {
        case .created(let created): return try created.body.json
        case .badRequest(let response):
            throw SocialFailure.rejected(code: try? response.body.application_problem_plus_json.code)
        case .conflict(let response):
            throw SocialFailure.rejected(code: try? response.body.application_problem_plus_json.code)
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func joinClan(code: String) async throws -> ClanInfo {
        switch try await api.joinClan(body: .json(.init(code: code))) {
        case .ok(let ok): return try ok.body.json
        case .notFound(let response):
            throw SocialFailure.rejected(code: try? response.body.application_problem_plus_json.code)
        case .conflict(let response):
            throw SocialFailure.rejected(code: try? response.body.application_problem_plus_json.code)
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func leaveClan() async throws {
        switch try await api.leaveClan() {
        case .noContent: return
        case .notFound: throw SocialFailure.rejected(code: "clan_not_member")
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func newInviteCode() async throws -> ClanInfo {
        switch try await api.newClanCode() {
        case .ok(let ok): return try ok.body.json
        case .forbidden: throw SocialFailure.rejected(code: nil)
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func friends() async throws -> FriendsInfo {
        switch try await api.listFriends() {
        case .ok(let ok): return try ok.body.json
        case .notFound: throw SocialFailure.signedOut
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func addFriend(code: String) async throws -> FriendInfo {
        switch try await api.addFriend(body: .json(.init(code: code))) {
        case .ok(let ok): return try ok.body.json
        case .badRequest(let response):
            throw SocialFailure.rejected(code: try? response.body.application_problem_plus_json.code)
        case .notFound(let response):
            throw SocialFailure.rejected(code: try? response.body.application_problem_plus_json.code)
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func acceptFriend(playerId: String) async throws -> FriendInfo {
        switch try await api.acceptFriend(path: .init(id: playerId)) {
        case .ok(let ok): return try ok.body.json
        case .notFound: throw SocialFailure.rejected(code: "friend_request_not_found")
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func removeFriend(playerId: String) async throws {
        switch try await api.removeFriend(path: .init(id: playerId)) {
        // Уже нет (404) — тоже успех: повтор после обрыва связи не ошибка.
        case .noContent, .notFound: return
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func feed(scope: FeedScope) async throws -> FeedPage {
        switch try await api.getFeed(query: .init(scope: scope.rawValue)) {
        case .ok(let ok): return try ok.body.json
        case .badRequest: throw SocialFailure.rejected(code: "feed_invalid")
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func respect(postId: String) async throws {
        switch try await api.respectPost(path: .init(id: postId)) {
        case .noContent: return
        case .notFound: throw SocialFailure.rejected(code: "post_not_found")
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func report(postId: String) async throws {
        switch try await api.reportPost(path: .init(id: postId), body: .json(.init(reason: nil))) {
        case .noContent: return
        case .badRequest(let response):
            throw SocialFailure.rejected(code: try? response.body.application_problem_plus_json.code)
        case .notFound: throw SocialFailure.rejected(code: "post_not_found")
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func block(playerId: String) async throws {
        switch try await api.blockPlayer(path: .init(id: playerId)) {
        case .noContent: return
        case .badRequest(let response):
            throw SocialFailure.rejected(code: try? response.body.application_problem_plus_json.code)
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func inbox() async throws -> InboxPage {
        switch try await api.getInbox() {
        case .ok(let ok): return try ok.body.json
        case .badRequest: throw SocialFailure.rejected(code: nil)
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }

    func markRead(upToAtMs: Int64) async throws {
        switch try await api.markInboxRead(body: .json(.init(upToAtMs: upToAtMs))) {
        case .noContent: return
        case .undocumented(let status, _): throw SocialFailure.status(status)
        }
    }
}
