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
    public var playerId: String? { Self.subject(of: accessToken) }

    /// Claim `sub` из полезной нагрузки JWT (`заголовок.нагрузка.подпись`, Base64URL без выравнивания).
    static func subject(of jwt: String) -> String? {
        let parts = jwt.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3 else { return nil }
        var base64 = parts[1].replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        base64 += String(repeating: "=", count: (4 - base64.count % 4) % 4)
        guard let data = Data(base64Encoded: base64),
            let claims = try? JSONDecoder().decode(Claims.self, from: data),
            let subject = claims.sub, !subject.isEmpty
        else { return nil }
        return subject
    }

    private struct Claims: Decodable {
        let sub: String?
    }
}

/// Токены не попадают в журналы и сообщения тестов: при печати видно только, чей это вход.
extension AuthTokens: CustomStringConvertible {
    public var description: String { "AuthTokens(player: \(playerId ?? "?"))" }
}
