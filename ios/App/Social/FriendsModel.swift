import Foundation
import GorodkiAPI
import Observation

/// «Друзья» — только взаимные (PLAN.md, §3.8): свой код, добавить по коду друга, принять заявку, удалить
/// (отклонить, отозвать — тот же `DELETE /friends/{id}`). Контракт #143.
@MainActor
@Observable
final class FriendsModel {
    /// Загрузилось; `nil` — ещё нет или не вышло (`failure`).
    private(set) var myCode: String?
    private(set) var friends: [FriendInfo] = []
    private(set) var failure: SocialFailure?
    private(set) var loaded = false
    /// Поле «Код друга».
    var addCode = ""
    private(set) var busy = false
    /// Ошибка последнего действия — под полем или над списком.
    var actionError: String?

    @ObservationIgnored private let source: (any FriendsSource)?

    init(source: (any FriendsSource)?) {
        self.source = source
    }

    /// Входящие заявки — сверху: их ждут.
    var incoming: [FriendInfo] { friends.filter { $0.status == .incoming } }
    var mutual: [FriendInfo] { friends.filter { $0.status == .friend } }
    var outgoing: [FriendInfo] { friends.filter { $0.status == .outgoing } }

    var canAdd: Bool {
        !busy && !InviteCode.normalized(addCode).isEmpty
    }

    func load() async {
        guard let source else {
            failure = .offline
            return
        }
        do {
            let response = try await source.friends()
            myCode = response.myCode
            friends = response.friends
            failure = nil
            loaded = true
        } catch {
            failure = SocialFailure.from(error)
        }
    }

    /// Добавить по коду. `true` — заявка ушла (или друг уже прислал свою — сразу дружба).
    func add() async -> Bool {
        guard canAdd, let source else { return false }
        busy = true
        defer { busy = false }
        do {
            upsert(try await source.addFriend(code: InviteCode.normalized(addCode)))
            addCode = ""
            actionError = nil
            return true
        } catch {
            actionError = SocialFailure.from(error).message
            return false
        }
    }

    func accept(_ friend: FriendInfo) async {
        guard let source, !busy else { return }
        busy = true
        defer { busy = false }
        do {
            upsert(try await source.acceptFriend(playerId: friend.playerId))
            actionError = nil
        } catch {
            actionError = SocialFailure.from(error).message
        }
    }

    /// Удалить друга, отклонить входящую или отозвать свою заявку.
    func remove(_ friend: FriendInfo) async {
        guard let source, !busy else { return }
        busy = true
        defer { busy = false }
        do {
            try await source.removeFriend(playerId: friend.playerId)
            friends.removeAll { $0.playerId == friend.playerId }
            actionError = nil
        } catch {
            actionError = SocialFailure.from(error).message
        }
    }

    private func upsert(_ friend: FriendInfo) {
        if let index = friends.firstIndex(where: { $0.playerId == friend.playerId }) {
            friends[index] = friend
        } else {
            friends.append(friend)
        }
    }
}
