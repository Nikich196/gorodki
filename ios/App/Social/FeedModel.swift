import Foundation
import GameCore
import GorodkiAPI
import Observation

/// Карточка ленты: простые значения из поста (`FeedPost`) — только то, что есть в контракте: автор, вид, дата, число.
struct FeedCard: Identifiable, Equatable, Sendable {
    var id: String
    var authorId: String
    var authorName: String
    var colorIndex: Int
    /// «Захват» или «Забег».
    var title: String
    /// «+0,41 га · 41 сотка», «5,23 км».
    var value: String
    /// «20 ноября» — дата без времени (граница публичности: времени чужих действий в ответе нет).
    var day: String
    var respects: Int
    var respectedByMe: Bool
    var mine: Bool

    init(_ post: Components.Schemas.FeedPost) {
        id = post.id
        authorId = post.authorId
        authorName = post.authorName
        colorIndex = Int(post.authorColorIndex)
        switch post.kind {
        case .capture:
            title = post.league == .bike ? "Захват · Вело" : "Захват"
            value =
                post.capturedSquareMeters.map { area in
                    "+" + NumberText.hectares(fromSquareMeters: area, fractionDigits: 2) + " · "
                        + CountText.sotki(Int((area / 100).rounded()))
                } ?? "Новая земля"
        case .run:
            title = post.league == .bike ? "Забег · Вело" : "Забег"
            value = post.distanceMeters.map { NumberText.kilometers(fromMeters: $0, fractionDigits: 2) } ?? "Забег"
        }
        day = Self.dayText(post.day)
        respects = Int(post.respects)
        respectedByMe = post.respectedByMe
        mine = post.mine
    }

    /// «2026-11-20» → «20 ноября».
    static func dayText(_ day: String) -> String {
        let months = [
            "января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября",
            "декабря",
        ]
        let parts = day.split(separator: "-").compactMap { Int($0) }
        guard parts.count == 3, (1...12).contains(parts[1]) else { return day }
        return "\(parts[2]) \(months[parts[1] - 1])"
    }
}

/// «Лента» (PLAN.md, §3.8; контракт #144): посты друзей и клана или всех, респект (один раз — повтор ничего не
/// меняет), жалоба, блокировка автора. Комментариев нет (решено в контракте). Посты заблокированных сервер не отдаёт,
/// а до перезагрузки экран прячет их сам.
@MainActor
@Observable
final class FeedModel {
    var scope: FeedScope = .friends
    private(set) var cards: [FeedCard] = []
    private(set) var failure: SocialFailure?
    private(set) var loaded = false
    /// Что случилось после действия: «Жалоба отправлена», ошибка — строкой над лентой.
    var notice: String?
    /// Заблокированные за этот показ — их посты скрыты сразу, не дожидаясь перезагрузки.
    private(set) var blocked: Set<String> = []
    /// Посты, на которые уже пожаловались: второй раз не предлагать.
    private(set) var reported: Set<String> = []

    @ObservationIgnored private let source: (any FeedSource)?

    init(source: (any FeedSource)?) {
        self.source = source
    }

    /// Что показывать: без постов заблокированных авторов.
    var visibleCards: [FeedCard] {
        cards.filter { !blocked.contains($0.authorId) }
    }

    func load() async {
        guard let source else {
            failure = .offline
            return
        }
        let scope = scope
        do {
            let page = try await source.feed(scope: scope)
            guard scope == self.scope else { return }
            cards = page.posts.map(FeedCard.init)
            failure = nil
            loaded = true
        } catch {
            guard scope == self.scope else { return }
            failure = SocialFailure.from(error)
        }
    }

    /// Респект: сразу на экране, не вышло — назад. Свой пост и уже отмеченный — без запроса.
    func respect(_ card: FeedCard) async {
        guard let source, !card.mine, !card.respectedByMe, let index = cards.firstIndex(where: { $0.id == card.id })
        else { return }
        cards[index].respectedByMe = true
        cards[index].respects += 1
        do {
            try await source.respect(postId: card.id)
        } catch {
            if let index = cards.firstIndex(where: { $0.id == card.id }) {
                cards[index].respectedByMe = false
                cards[index].respects -= 1
            }
            notice = SocialFailure.from(error).message
        }
    }

    func report(_ card: FeedCard) async {
        guard let source, !card.mine, !reported.contains(card.id) else { return }
        do {
            try await source.report(postId: card.id)
            reported.insert(card.id)
            notice = "Жалоба отправлена — пост посмотрит администратор."
        } catch {
            notice = SocialFailure.from(error).message
        }
    }

    /// Заблокировать автора: его посты не видны, дружба и заявки с ним снимаются (так делает сервер).
    func block(_ card: FeedCard) async {
        guard let source, !card.mine else { return }
        do {
            try await source.block(playerId: card.authorId)
            blocked.insert(card.authorId)
            notice = "Блокировка включена — посты «\(card.authorName)» скрыты."
        } catch {
            notice = SocialFailure.from(error).message
        }
    }
}
