import Foundation
import GameCore
import GorodkiAPI
import Observation

/// Название клана (PLAN.md, §3.6): 3–24 символа — буквы, цифры, пробел, дефис. Уникальность и фильтр мата проверяет
/// сервер (`clan_name_taken`, `clan_name_invalid`); здесь — только то, что видно сразу, пока игрок печатает.
enum ClanNameRule {
    static let length = 3...24

    /// Что не так с названием; `nil` — можно отправлять. Пробелы по краям не считаются — их отрежет `normalized`.
    static func problem(_ typed: String) -> String? {
        let name = normalized(typed)
        if name.count < length.lowerBound {
            return "Не короче \(length.lowerBound) символов."
        }
        if name.count > length.upperBound {
            return "Не длиннее \(length.upperBound) символов."
        }
        let allowed = CharacterSet.letters.union(.decimalDigits).union(CharacterSet(charactersIn: " -"))
        if !name.unicodeScalars.allSatisfy(allowed.contains) {
            return "Только буквы, цифры, пробел и дефис."
        }
        return nil
    }

    static func normalized(_ typed: String) -> String {
        typed.trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

/// Роль в клане (PLAN.md, §3.6: лидер и до 2 офицеров) — подпись и что роли можно.
extension Components.Schemas.ClanRole {
    var title: String {
        switch self {
        case .leader: "Лидер"
        case .officer: "Офицер"
        case .member: "Участник"
        }
    }

    /// Порядок в составе: лидер, офицеры, участники.
    var order: Int {
        switch self {
        case .leader: 0
        case .officer: 1
        case .member: 2
        }
    }

    /// Код-приглашение видят и обновляют лидер и офицеры (вступление — по коду от них, без заявок).
    var managesInvites: Bool {
        self != .member
    }
}

/// «Клан»: свой клан или «не в клане» — создать, вступить по коду; выход и код-приглашение (контракт #135). Роли
/// только показываются: передача ролей, исключение и переименование — следующая волна.
@MainActor
@Observable
final class ClanModel {
    enum State: Equatable, Sendable {
        case loading
        /// Не в клане; `canJoinAtMs` — после выхода (72 ч) вступить можно не раньше.
        case none(canJoinAtMs: Int64?)
        case member(ClanInfo)
        case failed(SocialFailure)
    }

    private(set) var state: State = .loading
    /// Поле «Название» листа «Новый клан».
    var newName = ""
    /// Выбранный оттенок палитры кланов (0–11); `nil` — любой свободный (сервер выберет сам).
    var newHue: Int?
    /// Свободные оттенки (`GET /clans/hues`); пусто — ещё не загрузились.
    private(set) var freeHues: [Int] = []
    /// Поле «Код приглашения».
    var joinCode = ""
    private(set) var busy = false
    /// Ошибка последнего действия (создать, вступить, выйти, новый код) — под кнопкой.
    var actionError: String?

    @ObservationIgnored private let source: (any ClanSource)?
    @ObservationIgnored private let now: @Sendable () -> Date

    init(source: (any ClanSource)?, now: @escaping @Sendable () -> Date = { .now }) {
        self.source = source
        self.now = now
    }

    var clan: ClanInfo? {
        if case .member(let clan) = state { return clan }
        return nil
    }

    /// Состав: лидер, офицеры, участники; внутри роли — как отдал сервер.
    var members: [Components.Schemas.ClanMemberResponse] {
        guard let clan else { return [] }
        return clan.members.enumerated()
            .sorted { ($0.element.role.order, $0.offset) < ($1.element.role.order, $1.offset) }
            .map(\.element)
    }

    var myRole: Components.Schemas.ClanRole? {
        clan?.myRole ?? clan?.members.first { $0.me }?.role
    }

    /// Показывать код-приглашение и «Новый код»: лидеру и офицерам, если сервер отдал код.
    var showsInvite: Bool {
        (myRole?.managesInvites ?? false) && clan?.inviteCode != nil
    }

    /// После выхода вступить (или создать клан) можно не раньше этого момента; `nil` — уже можно.
    var joinBlockedUntil: Date? {
        guard case .none(let canJoinAtMs?) = state else { return nil }
        let date = Date(timeIntervalSince1970: Double(canJoinAtMs) / 1_000)
        return date > now() ? date : nil
    }

    var nameProblem: String? {
        ClanNameRule.problem(newName)
    }

    var canCreate: Bool {
        !busy && nameProblem == nil && joinBlockedUntil == nil
    }

    var canJoin: Bool {
        !busy && !InviteCode.normalized(joinCode).isEmpty && joinBlockedUntil == nil
    }

    func load() async {
        guard let source else {
            state = .failed(.offline)
            return
        }
        do {
            let mine = try await source.myClan()
            state = mine.clan.map(State.member) ?? .none(canJoinAtMs: mine.canJoinAtMs)
        } catch {
            state = .failed(SocialFailure.from(error))
        }
    }

    /// Свободные оттенки для листа «Новый клан»; первый свободный — выбран.
    func loadHues() async {
        guard let source, let hues = try? await source.freeHues() else { return }
        freeHues = hues
        if newHue.map(hues.contains) != true {
            newHue = hues.first
        }
    }

    /// Создать клан. `true` — создан, лист можно закрыть.
    func create() async -> Bool {
        guard canCreate else { return false }
        return await perform { source in
            try await source.createClan(name: ClanNameRule.normalized(self.newName), hue: self.newHue)
        }
    }

    /// Вступить по коду (заглавными, без пробелов по краям — как код приглашения при входе).
    func join() async -> Bool {
        guard canJoin else { return false }
        return await perform { source in
            try await source.joinClan(code: InviteCode.normalized(self.joinCode))
        }
    }

    func renewInviteCode() async {
        _ = await perform { source in try await source.newInviteCode() }
    }

    /// Выйти: земля остаётся у игрока; когда можно вступить снова, скажет `GET /clans/mine`.
    func leave() async {
        guard let source, !busy else { return }
        busy = true
        defer { busy = false }
        do {
            try await source.leaveClan()
            actionError = nil
            state = .none(canJoinAtMs: nil)
            await load()
        } catch {
            actionError = SocialFailure.from(error).message
        }
    }

    private func perform(_ action: (any ClanSource) async throws -> ClanInfo) async -> Bool {
        guard let source, !busy else { return false }
        busy = true
        defer { busy = false }
        do {
            state = .member(try await action(source))
            actionError = nil
            joinCode = ""
            newName = ""
            return true
        } catch {
            actionError = SocialFailure.from(error).message
            return false
        }
    }

    /// «24 сентября, 17:13» по Минску — до какого момента нельзя вступить.
    static func momentText(_ date: Date) -> String {
        var style = Date.FormatStyle(date: .omitted, time: .omitted, locale: NumberText.locale)
        style.timeZone = TimeZone(identifier: "Europe/Minsk") ?? .current
        return date.formatted(style.day().month(.wide).hour(.twoDigits(amPM: .omitted)).minute(.twoDigits))
    }
}
