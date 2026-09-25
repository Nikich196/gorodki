import Foundation
import GorodkiAPI

/// Пара токенов входа (docs/architecture/auth.md): access — JWT на 15 минут, им подписывается каждый запрос;
/// refresh — одноразовый ключ на 30 дней, при каждом обновлении сервер меняет его на новый.
public struct AuthTokens: Codable, Equatable, Sendable {
    public var accessToken: String
    public var refreshToken: String

    public init(accessToken: String, refreshToken: String) {
        self.accessToken = accessToken
        self.refreshToken = refreshToken
    }

    /// Из ответа `POST /auth/google` или `POST /auth/refresh`.
    public init(_ session: Components.Schemas.SessionResponse) {
        self.init(accessToken: session.accessToken, refreshToken: session.refreshToken)
    }

    /// Вошедший игрок — claim `sub` access-токена (сервер пишет туда id игрока). Подпись здесь не проверяется: её
    /// проверяет сервер, а телефону id нужен, чтобы синхронизировать только свои забеги (`SyncEngine.ownerId`).
    public var playerId: String? {
        guard let subject = Self.claims(of: accessToken)?.sub, !subject.isEmpty else { return nil }
        return subject
    }

    /// Роль игрока — claim `role` access-токена (`player`, `demo` или `admin`): по ней приложение показывает отладочное
    /// меню (PLAN.md, §5). Как и `playerId`, без проверки подписи: права на каждый запрос всё равно проверяет сервер.
    public var role: String? {
        guard let role = Self.claims(of: accessToken)?.role, !role.isEmpty else { return nil }
        return role
    }

    /// Когда истекает access-токен — claim `exp`; `nil`, если его нет. Нужен соединениям, где 401 не исправить
    /// повтором запроса (реальное время: сервер закрывает соединение, когда токен истекает).
    public var accessExpiresAt: Date? {
        Self.claims(of: accessToken)?.exp.map { Date(timeIntervalSince1970: $0) }
    }

    /// Когда выдан access-токен — claim `iat` (по часам сервера); `nil`, если его нет.
    public var accessIssuedAt: Date? {
        Self.claims(of: accessToken)?.iat.map { Date(timeIntervalSince1970: $0) }
    }

    /// Полезная нагрузка JWT (`заголовок.нагрузка.подпись`, Base64URL без выравнивания).
    static func claims(of jwt: String) -> Claims? {
        let parts = jwt.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3 else { return nil }
        var base64 = parts[1].replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        base64 += String(repeating: "=", count: (4 - base64.count % 4) % 4)
        guard let data = Data(base64Encoded: base64) else { return nil }
        return try? JSONDecoder().decode(Claims.self, from: data)
    }

    /// Нужные телефону claims. Каждое читается само по себе: неверный тип одного не прячет другое.
    struct Claims: Decodable {
        let sub: String?
        let role: String?
        let exp: Double?
        let iat: Double?

        init(from decoder: any Decoder) throws {
            let container = try decoder.container(keyedBy: CodingKeys.self)
            sub = try? container.decode(String.self, forKey: .sub)
            role = try? container.decode(String.self, forKey: .role)
            exp = try? container.decode(Double.self, forKey: .exp)
            iat = try? container.decode(Double.self, forKey: .iat)
        }

        private enum CodingKeys: String, CodingKey {
            case sub, role, exp, iat
        }
    }
}

/// Токены не попадают в журналы и сообщения тестов: при печати видно только, чей это вход.
extension AuthTokens: CustomStringConvertible {
    public var description: String { "AuthTokens(player: \(playerId ?? "?"))" }
}
