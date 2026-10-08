import Foundation

/// Что зашито в QR-код игры (пункт 13 листика; PLAN.md, §5: друзья — по QR и ссылке, поиска нет). Свой QR показывает
/// «Мой QR», сканер камеры читает чужой. Ссылка — схемы `gorodki://`: её читает сканер игры; открывать её из системной
/// камеры приложение пока не умеет (схема не зарегистрирована).
public enum FriendLink: Hashable, Sendable {
    /// Игрок: номер с сервера (`GET /me`, `id`) и ник — сканер сразу показывает, кого нашёл.
    case player(id: String, name: String?)
    /// Код приглашения закрытой регистрации (`XXXX-XXXX`, выдаёт админ): друг введёт его на онбординге.
    case invite(code: String)

    public static let scheme = "gorodki"

    /// Текст для QR: `gorodki://friend?id=…&name=…` или `gorodki://invite?code=ABCD-2345`.
    public var url: String {
        var components = URLComponents()
        components.scheme = Self.scheme
        switch self {
        case .player(let id, let name):
            components.host = "friend"
            components.queryItems =
                [URLQueryItem(name: "id", value: id)] + (name.map { [URLQueryItem(name: "name", value: $0)] } ?? [])
        case .invite(let code):
            components.host = "invite"
            components.queryItems = [URLQueryItem(name: "code", value: code)]
        }
        return components.string ?? "\(Self.scheme)://"
    }

    /// Разобрать прочитанный QR. Чужой код (ссылка на сайт, текст) — `nil`: сканер покажет его как есть.
    public static func parse(_ text: String) -> FriendLink? {
        guard let components = URLComponents(string: text.trimmingCharacters(in: .whitespacesAndNewlines)),
            components.scheme?.lowercased() == scheme
        else { return nil }
        func value(_ name: String) -> String? {
            components.queryItems?.first { $0.name == name }?.value.flatMap { $0.isEmpty ? nil : $0 }
        }
        switch components.host?.lowercased() {
        case "friend":
            guard let id = value("id") else { return nil }
            return .player(id: id, name: value("name"))
        case "invite":
            guard let code = value("code").map(InviteText.normalizedCode), InviteText.isWellFormed(code) else {
                return nil
            }
            return .invite(code: code)
        default:
            return nil
        }
    }
}

/// Приглашение друга (пункт 7 листика: контакты и приглашение): текст сообщения и проверка кода.
public enum InviteText {
    /// Код, как его набрал игрок: без пробелов по краям, заглавными — коды выдаются заглавными (`XXXX-XXXX`).
    public static func normalizedCode(_ typed: String) -> String {
        typed.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
    }

    /// Похож на код приглашения: две группы по четыре латинские буквы или цифры через дефис.
    public static func isWellFormed(_ code: String) -> Bool {
        let parts = code.split(separator: "-", omittingEmptySubsequences: false)
        return parts.count == 2
            && parts.allSatisfy { part in
                part.count == 4
                    && part.unicodeScalars.allSatisfy { $0.isASCII && CharacterSet.alphanumerics.contains($0) }
            }
    }

    /// Сообщение другу. Имя — как в контакте («Привет, Аня!»), без имени — просто «Привет!». Код — только правильного
    /// вида: неверный код друг всё равно не введёт.
    public static func message(friendName: String?, inviteCode: String?, senderName: String?) -> String {
        let name = friendName?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        var lines = [name.isEmpty ? "Привет!" : "Привет, \(name)!"]
        lines.append("Зову в «Городки» — захватываем Брест шагами: обегаешь квартал, и он твой.")
        if let code = inviteCode.map(normalizedCode), isWellFormed(code) {
            lines.append("Код приглашения: \(code)")
        } else {
            lines.append("Регистрация по приглашениям — код пришлю, как получу.")
        }
        if let sender = senderName?.trimmingCharacters(in: .whitespacesAndNewlines), !sender.isEmpty {
            lines.append("Меня в игре зовут \(sender).")
        }
        return lines.joined(separator: "\n")
    }
}
