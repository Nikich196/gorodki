import Foundation
import GorodkiAPI
import Testing

@testable import Gorodki

/// Социальные экраны (docs/architecture/ios-app.md, «Рейтинги, клан, друзья, лента, входящие»): модели на образцах
/// `contracts/samples` (`SampleSocialSource`) и на подмене, которая помнит запросы и умеет отказывать.
@Suite("Рейтинги, клан, друзья, лента, входящие: модели")
@MainActor
struct SocialModelTests {
    // MARK: - Рейтинги

    @Test("«Захват»: места по порядку, своё 17-е закреплено снизу, сезон идёт — «предварительно»")
    func territoryPinsMine() async {
        let model = LeaderboardsModel(source: SampleSocialSource())
        await model.load()
        #expect(model.rows.map(\.rank) == [1, 2])
        #expect(model.rows.map(\.name) == ["Муха", "Игрок #4771"])
        #expect(!model.mineInTop)
        #expect(model.pinnedRow(mineRowVisible: true)?.rank == 17)
        #expect(model.pinnedRow(mineRowVisible: true)?.me == true)
        #expect(model.isFinal == false)
        #expect(model.statusText == "предварительно")
        #expect(model.caption == "Сезон 0 · срез 20.11")
    }

    @Test("Итог сезона (final) — пометка «итог»")
    func finalBoard() {
        let model = LeaderboardsModel(source: nil)
        model.apply(Self.board(final: true, [("Муха", 900)]))
        #expect(model.isFinal == true)
        #expect(model.statusText == "итог")
    }

    @Test("«Исследование»: своё 7-е место среди первых — закрепляется, только когда строка ушла из виду")
    func explorationMineInTop() async {
        let model = LeaderboardsModel(source: SampleSocialSource())
        model.kind = .exploration
        await model.load()
        #expect(model.mineInTop)
        #expect(model.pinnedRow(mineRowVisible: true) == nil)
        #expect(model.pinnedRow(mineRowVisible: false)?.rank == 7)
        #expect(model.rows.first?.value == "212,40\u{00A0}га")
        #expect(model.statusText == nil)
    }

    @Test("Смена мест: у строк те же id — список переставляет их, а не рисует заново; одинаковые ники не ломают id")
    func stableIdentity() {
        let model = LeaderboardsModel(source: nil)
        model.apply(Self.board([("Муха", 900), ("Лиса-2718", 800), ("Бегун-1234", 700)], me: "Бегун-1234"))
        let before = Set(model.rows.map(\.id))
        model.apply(Self.board([("Бегун-1234", 950), ("Муха", 900), ("Лиса-2718", 800)], me: "Бегун-1234"))
        #expect(Set(model.rows.map(\.id)) == before)
        #expect(model.rows.map(\.id) == ["me", "Муха", "Лиса-2718"])
        model.apply(Self.board([("Игрок #1234", 10), ("Игрок #1234", 5)]))
        #expect(Set(model.rows.map(\.id)).count == 2)
    }

    @Test("Заглушка сервера (500) — «пока не работает на сервере»; «Короли» — без запросов")
    func stubAndKings() async {
        let model = LeaderboardsModel(source: SampleSocialSource(stub: true))
        await model.load()
        #expect(model.failure == .notReady)
        #expect(model.failure?.message.contains("Пока не работает на сервере") == true)
        #expect(SocialFailure.status(500) == .notReady)
        #expect(SocialFailure.status(401) == .signedOut)
        model.kind = .kings
        await model.load()
        #expect(model.failure == nil)
        #expect(model.rows.isEmpty)
    }

    // MARK: - Клан

    @Test(
        "Название клана: 3–24 символа, буквы, цифры, пробел, дефис",
        arguments: [
            ("Бегуны БрГТУ", true), ("  Бег-1 2  ", true), ("abc", true), ("ab", false),
            (String(repeating: "я", count: 24), true), (String(repeating: "я", count: 25), false), ("Клан_1", false),
            ("Клан!", false), ("   ", false),
        ])
    func clanName(_ name: String, _ valid: Bool) {
        #expect((ClanNameRule.problem(name) == nil) == valid)
    }

    @Test("Роли: состав — лидер, офицер, участник; код-приглашение видят лидер и офицер, участник — нет")
    func rolesAndActions() async {
        let model = ClanModel(source: SampleSocialSource(), now: SampleSocialSource.now)
        await model.load()
        #expect(model.clan?.name == "Бегуны БрГТУ")
        #expect(model.members.map(\.role) == [.leader, .officer, .member])
        #expect(model.myRole == .leader)
        #expect(model.showsInvite)
        #expect(Components.Schemas.ClanRole.officer.managesInvites)
        #expect(!Components.Schemas.ClanRole.member.managesInvites)
        let member = FakeSocial()
        await member.setClan(role: .member)
        let asMember = ClanModel(source: member)
        await asMember.load()
        #expect(asMember.myRole == .member)
        #expect(!asMember.showsInvite)
    }

    @Test("Не в клане: до конца паузы после выхода вступить и создать нельзя, после — можно")
    func joinCooldown() async {
        let canJoinAt = Date(timeIntervalSince1970: 1_790_259_200)
        let early = ClanModel(source: SampleSocialSource(inClan: false), now: { canJoinAt.addingTimeInterval(-60) })
        await early.load()
        early.joinCode = "k7m2-9qxa"
        early.newName = "Бегуны"
        #expect(early.joinBlockedUntil == canJoinAt)
        #expect(!early.canJoin)
        #expect(!early.canCreate)
        let later = ClanModel(source: SampleSocialSource(inClan: false), now: SampleSocialSource.now)
        await later.load()
        later.joinCode = "k7m2-9qxa"
        #expect(later.joinBlockedUntil == nil)
        #expect(later.canJoin)
        #expect(await later.join())
        #expect(later.clan != nil)
    }

    @Test("Создать: название без пробелов по краям, первый свободный оттенок; код вступления — заглавными")
    func createAndJoinSendNormalized() async {
        let fake = FakeSocial()
        let model = ClanModel(source: fake, now: SampleSocialSource.now)
        await model.load()
        await model.loadHues()
        #expect(model.newHue == 3)
        model.newName = "  Совы  "
        #expect(await model.create())
        #expect(await fake.created?.name == "Совы")
        #expect(await fake.created?.hue == 3)
        #expect(model.clan?.name == "Совы")
        let joining = ClanModel(source: fake, now: SampleSocialSource.now)
        await joining.load()
        await fake.setFailure(.rejected(code: "clan_code_invalid"))
        joining.joinCode = " k7m2-9qxa "
        #expect(await joining.join() == false)
        #expect(await fake.joinedCode == "K7M2-9QXA")
        #expect(joining.actionError?.contains("Такого кода нет") == true)
        #expect(joining.clan == nil)
    }

    // MARK: - Друзья

    @Test("Друзья: заявка сверху, «Принять» делает другом, «Удалить» убирает")
    func friends() async {
        let model = FriendsModel(source: SampleSocialSource())
        await model.load()
        #expect(model.myCode == "R4T8-KD2M")
        #expect(model.incoming.map(\.name) == ["Игрок #4771"])
        #expect(model.mutual.map(\.name) == ["Муха"])
        let request = model.incoming[0]
        await model.accept(request)
        #expect(model.incoming.isEmpty)
        #expect(model.mutual.count == 2)
        await model.remove(request)
        #expect(model.mutual.map(\.name) == ["Муха"])
        model.addCode = "x9"
        #expect(await model.add())
        #expect(model.outgoing.count == 1)
    }

    // MARK: - Лента

    @Test("Лента: блокировка скрывает все посты автора; жалоба — один раз")
    func blockHidesAuthor() async {
        let model = FeedModel(source: SampleSocialSource())
        await model.load()
        #expect(model.visibleCards.count == 2)
        let muha = model.visibleCards[0]
        #expect(muha.authorName == "Муха")
        #expect(muha.value.hasPrefix("+0,41\u{00A0}га · 41"))
        await model.block(muha)
        #expect(model.visibleCards.allSatisfy { $0.authorId != muha.authorId })
        #expect(model.visibleCards.count == 1)
        let mine = model.visibleCards[0]
        await model.block(mine)
        #expect(model.visibleCards.count == 1)
        await model.report(muha)
        #expect(model.reported == [muha.id])
    }

    @Test("Респект: +1 сразу, свой пост и уже отмеченный — без запроса, отказ сервера — откат")
    func respect() async {
        let fake = FakeSocial()
        let model = FeedModel(source: fake)
        await model.load()
        let other = model.cards[0]
        let mine = model.cards[1]
        #expect(!other.mine && !other.respectedByMe && mine.mine)
        await model.respect(other)
        #expect(model.cards[0].respects == other.respects + 1)
        #expect(model.cards[0].respectedByMe)
        await model.respect(model.cards[0])
        await model.respect(mine)
        #expect(await fake.respected == [other.id])
        let failing = FeedModel(source: fake)
        await failing.load()
        await fake.setFailure(.notReady)
        await failing.respect(failing.cards[0])
        #expect(failing.cards[0].respects == other.respects)
        #expect(!failing.cards[0].respectedByMe)
        #expect(failing.notice != nil)
    }

    // MARK: - Входящие

    @Test("Входящие: одно непрочитанное на колокольчике, «прочитано» — до самого свежего, отказ — как было")
    func inboxUnread() async {
        let fake = FakeSocial()
        let model = InboxModel(source: fake)
        await model.load()
        #expect(model.unread == 1)
        #expect(model.badge == "1")
        await model.markAllRead()
        #expect(model.unread == 0)
        #expect(model.badge == nil)
        #expect(!model.rows.contains { !$0.read })
        #expect(await fake.readUpTo == 1_790_001_500_000)
        let failing = InboxModel(source: fake)
        await failing.load()
        await fake.setFailure(.offline)
        await failing.markAllRead()
        #expect(failing.unread == 1)
        #expect(failing.rows.contains { !$0.read })
        #expect(failing.notice != nil)
    }

    // MARK: - Подмены

    private static func board(
        final: Bool = false, _ entries: [(String, Int32)], me: String? = nil
    ) -> TerritoryBoard {
        let rows = entries.enumerated().map { index, entry in
            Components.Schemas.TerritoryLeaderboardEntry(
                rank: Int32(index + 1), name: entry.0, points: entry.1, me: entry.0 == me)
        }
        return TerritoryBoard(day: "2026-11-20", league: .run, season: 0, final: final, entries: rows, mine: nil)
    }
}

/// Подмена сервера: ответы — образцы, запросы запоминаются, `failure` — отказ каждого действия.
actor FakeSocial: SocialBackend {
    private let samples = SampleSocialSource()
    private(set) var failure: SocialFailure?
    private var clanRole: Components.Schemas.ClanRole?
    private(set) var created: (name: String, hue: Int?)?
    private(set) var joinedCode: String?
    private(set) var respected: [String] = []
    private(set) var readUpTo: Int64?

    func setFailure(_ failure: SocialFailure?) { self.failure = failure }
    func setClan(role: Components.Schemas.ClanRole) { clanRole = role }

    private func check() throws {
        if let failure { throw failure }
    }

    func territory(league: Components.Schemas.League) async throws -> TerritoryBoard {
        try check()
        return try await samples.territory(league: league)
    }

    func exploration(layer: String) async throws -> ExplorationBoard {
        try check()
        return try await samples.exploration(layer: layer)
    }

    func myClan() async throws -> MyClanInfo {
        try check()
        guard let clanRole else { return try await SampleSocialSource(inClan: false).myClan() }
        var clan = try await samples.joinClan(code: "")
        clan.myRole = clanRole
        if !clanRole.managesInvites {
            clan.inviteCode = nil
        }
        return MyClanInfo(clan: clan, canJoinAtMs: nil)
    }

    func freeHues() async throws -> [Int] {
        try check()
        return [3, 5, 6]
    }

    func createClan(name: String, hue: Int?) async throws -> ClanInfo {
        created = (name, hue)
        try check()
        return try await samples.createClan(name: name, hue: hue)
    }

    func joinClan(code: String) async throws -> ClanInfo {
        joinedCode = code
        try check()
        return try await samples.joinClan(code: code)
    }

    func leaveClan() async throws { try check() }

    func newInviteCode() async throws -> ClanInfo {
        try check()
        return try await samples.newInviteCode()
    }

    func friends() async throws -> FriendsInfo {
        try check()
        return try await samples.friends()
    }

    func addFriend(code: String) async throws -> FriendInfo {
        try check()
        return try await samples.addFriend(code: code)
    }

    func acceptFriend(playerId: String) async throws -> FriendInfo {
        try check()
        return try await samples.acceptFriend(playerId: playerId)
    }

    func removeFriend(playerId: String) async throws { try check() }

    /// Лента образца, но чужой пост ещё без своего респекта — чтобы было что отмечать.
    func feed(scope: FeedScope) async throws -> FeedPage {
        try check()
        var page = try await samples.feed(scope: scope)
        page.posts = page.posts.map { post in
            var post = post
            if !post.mine && post.respectedByMe {
                post.respectedByMe = false
                post.respects -= 1
            }
            return post
        }
        return page
    }

    func respect(postId: String) async throws {
        try check()
        respected.append(postId)
    }

    func report(postId: String) async throws { try check() }
    func block(playerId: String) async throws { try check() }

    func inbox() async throws -> InboxPage {
        try check()
        return try await samples.inbox()
    }

    func markRead(upToAtMs: Int64) async throws {
        try check()
        readUpTo = upToAtMs
    }
}
