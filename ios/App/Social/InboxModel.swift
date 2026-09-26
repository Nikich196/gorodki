import Foundation
import GameCore
import GorodkiAPI
import Observation

/// Событие «Входящих»: текст сервера как есть (без координат — граница публичности), вид — значком.
struct InboxRow: Identifiable, Equatable, Sendable {
    var id: String
    var kind: Components.Schemas.InboxKind
    var text: String
    var atMs: Int64
    var read: Bool

    init(_ item: Components.Schemas.InboxItem) {
        id = item.id
        kind = item.kind
        text = item.text
        atMs = item.atMs
        read = item.read
    }

    /// Значок SF Symbols по виду (PLAN.md, §3.17: нападение и осада > свержение > дуэль > рейд > конец сезона >
    /// угасание > серия).
    var symbolName: String {
        switch kind {
        case .attack: "shield.lefthalf.filled.slash"
        case .dethroned: "crown"
        case .duel: "figure.run.circle"
        case .raid: "flag.2.crossed"
        case .seasonEnd: "flag.checkered"
        case .decay: "hourglass"
        case .streak: "flame"
        }
    }
}

/// «Входящие» (PLAN.md, §3.17; контракт #139): события и число непрочитанных — для колокольчика на «Карте»
/// и строки в «Профиле». «Прочитано» — все события не новее самого свежего (`POST /inbox/read {upToAtMs}`).
@MainActor
@Observable
final class InboxModel {
    private(set) var rows: [InboxRow] = []
    /// Непрочитанных — число на колокольчике (сервер считает и то, что ещё не пришло страницей).
    private(set) var unread = 0
    private(set) var failure: SocialFailure?
    private(set) var loaded = false
    /// Не вышло отметить прочитанным — строкой над списком.
    var notice: String?

    @ObservationIgnored private let source: (any InboxSource)?
    /// «Сейчас» — для «5 минут назад» (в фикстурах зафиксировано).
    @ObservationIgnored let now: @Sendable () -> Date

    init(source: (any InboxSource)?, now: @escaping @Sendable () -> Date = { .now }) {
        self.source = source
        self.now = now
    }

    /// Колокольчик — только если есть что читать: «9+» вместо длинных чисел.
    var badge: String? {
        guard unread > 0 else { return nil }
        return unread > 9 ? "9+" : "\(unread)"
    }

    func load() async {
        guard let source else {
            failure = .offline
            return
        }
        do {
            let page = try await source.inbox()
            rows = page.items.map(InboxRow.init)
            unread = Int(page.unread)
            failure = nil
            loaded = true
        } catch {
            failure = SocialFailure.from(error)
        }
    }

    /// Отметить всё прочитанным: сразу на экране, не вышло — вернуть как было.
    func markAllRead() async {
        guard let source, let newest = rows.map(\.atMs).max(), unread > 0 || rows.contains(where: { !$0.read })
        else { return }
        let before = (rows, unread)
        for index in rows.indices {
            rows[index].read = true
        }
        unread = 0
        do {
            try await source.markRead(upToAtMs: newest)
        } catch {
            (rows, unread) = before
            notice = SocialFailure.from(error).message
        }
    }
}
