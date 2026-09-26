#if DEBUG
    import Foundation
    import GorodkiAPI

    /// Социальные экраны режима фикстур: ответы — образцы `contracts/samples` (`leaderboard-territory`, `clan`,
    /// `clan-mine`, `clan-hues`, `friends`, `feed`, `inbox`) через те же типы API, что у живых данных. Действия
    /// («Вступить», «Респект», «Прочитано») просто удаются. `stub` — каждый адрес отвечает, как заглушка сервера (500):
    /// экран говорит «пока не работает на сервере».
    struct SampleSocialSource: SocialBackend {
        /// Игрок в клане (`clan.json`) или нет (`clan-mine.json`: клана нет, вступить можно после паузы).
        var inClan = true
        var stub = false

        /// «Сейчас» фикстуры: 25.09.2026 18:00 по Минску — после паузы вступления из `clan-mine.json`, через четыре
        /// дня после событий `inbox.json`.
        static let nowMs: Int64 = 1_790_348_400_000
        static let now: @Sendable () -> Date = { Date(timeIntervalSince1970: Double(nowMs) / 1_000) }

        private func sample<Value: Decodable>(_ name: String, as type: Value.Type) throws -> Value {
            guard !stub else { throw SocialFailure.notReady }
            guard let value = Fixtures.sample(name, as: type) else { throw SocialFailure.unexpected(0) }
            return value
        }

        private func succeed() throws {
            guard !stub else { throw SocialFailure.notReady }
        }

        func territory(league: Components.Schemas.League) async throws -> TerritoryBoard {
            try sample("leaderboard-territory", as: TerritoryBoard.self)
        }

        /// Образца «Исследования» в контракте нет (адрес уже работает на сервере): таблица собрана здесь — ники
        /// образцов и `MapFixture`, свои 123,46 га и 7-е место — из `me-stats.json`.
        func exploration(layer: String) async throws -> ExplorationBoard {
            try succeed()
            let names: [(String, Double)] = [
                ("Муха", 212.4), ("Лиса-2718", 188.05), ("Игрок #4771", 170.3), ("Сова-9051", 151.72),
                ("Бегун-0815", 140.11), ("Игрок #4290", 129.9), ("Бегун-1234", 123.46), ("Ёж-5120", 98.2),
                ("Игрок #7342", 76.54), ("Бегун-3306", 51.08),
            ]
            let entries = names.enumerated().map { index, entry in
                Components.Schemas.LeaderboardEntry(
                    rank: Int32(index + 1), name: entry.0, hectares: entry.1, me: entry.0 == "Бегун-1234")
            }
            return ExplorationBoard(
                day: "2026-11-20", layer: layer, season: nil, entries: entries, mine: entries.first { $0.me })
        }

        func myClan() async throws -> MyClanInfo {
            guard inClan else { return try sample("clan-mine", as: MyClanInfo.self) }
            return MyClanInfo(clan: try sample("clan", as: ClanInfo.self), canJoinAtMs: nil)
        }

        func freeHues() async throws -> [Int] {
            try sample("clan-hues", as: Components.Schemas.ClanHuesResponse.self).free.map(Int.init)
        }

        func createClan(name: String, hue: Int?) async throws -> ClanInfo {
            var clan = try sample("clan", as: ClanInfo.self)
            clan.name = name
            clan.hue = Int32(hue ?? Int(clan.hue))
            clan.members = clan.members.filter(\.me)
            clan.full = false
            return clan
        }

        func joinClan(code: String) async throws -> ClanInfo {
            try sample("clan", as: ClanInfo.self)
        }

        func leaveClan() async throws { try succeed() }

        func newInviteCode() async throws -> ClanInfo {
            try sample("clan", as: ClanInfo.self)
        }

        func friends() async throws -> FriendsInfo {
            try sample("friends", as: FriendsInfo.self)
        }

        func addFriend(code: String) async throws -> FriendInfo {
            try succeed()
            return FriendInfo(playerId: MapFixture.rival(3), name: "Бегун-0815", colorIndex: 4, status: .outgoing)
        }

        func acceptFriend(playerId: String) async throws -> FriendInfo {
            var friend = try sample("friends", as: FriendsInfo.self).friends.first { $0.playerId == playerId }
            friend?.status = .friend
            guard let friend else { throw SocialFailure.rejected(code: "friend_request_not_found") }
            return friend
        }

        func removeFriend(playerId: String) async throws { try succeed() }

        func feed(scope: FeedScope) async throws -> FeedPage {
            try sample("feed", as: FeedPage.self)
        }

        func respect(postId: String) async throws { try succeed() }
        func report(postId: String) async throws { try succeed() }
        func block(playerId: String) async throws { try succeed() }

        func inbox() async throws -> InboxPage {
            try sample("inbox", as: InboxPage.self)
        }

        func markRead(upToAtMs: Int64) async throws { try succeed() }
    }
#endif
