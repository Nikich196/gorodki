import Foundation
import Observation

/// Все адреса социальных экранов — одним значением: сервер (`APISocialSource`) или образцы (`SampleSocialSource`).
typealias SocialBackend = LeaderboardSource & ClanSource & FriendsSource & FeedSource & InboxSource

/// Раздел вкладки «Клан»: «Лента» живёт здесь же (решено 26.09 по делегированию, ios-app.md).
enum ClanSection: String, CaseIterable, Identifiable, Sendable {
    case clan, friends, feed

    var id: String { rawValue }

    var title: String {
        switch self {
        case .clan: "Клан"
        case .friends: "Друзья"
        case .feed: "Лента"
        }
    }
}

/// Модели социальных экранов оболочки: «Рейтинги», «Клан | Друзья | Лента», «Входящие» (колокольчик на «Карте»
/// и строка в «Профиле» — одна модель). Какие листы открыты — тоже здесь: режим фикстур открывает их сразу.
@MainActor
@Observable
final class SocialScreens {
    let leaderboards: LeaderboardsModel
    let clan: ClanModel
    let friends: FriendsModel
    let feed: FeedModel
    let inbox: InboxModel
    var clanSection: ClanSection = .clan
    /// Лист «Новый клан».
    var createClanShown = false
    /// Лист «Добавить друга».
    var addFriendShown = false
    /// Лист «Входящие» с «Карты».
    var inboxShown = false

    /// - Parameter backend: `nil` — нет адреса сервера: экраны скажут «нет связи».
    init(backend: (any SocialBackend)?, now: @escaping @Sendable () -> Date = { .now }) {
        leaderboards = LeaderboardsModel(source: backend)
        clan = ClanModel(source: backend, now: now)
        friends = FriendsModel(source: backend)
        feed = FeedModel(source: backend)
        inbox = InboxModel(source: backend, now: now)
    }
}
