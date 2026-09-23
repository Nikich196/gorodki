import Foundation
import GorodkiAPI
import OpenAPIRuntime

/// `POST /auth/refresh` через сгенерированный клиент. Клиент должен быть без `AuthMiddleware` (см. `ClientFactory`):
/// запрос обновления не подписывается и на свой 401 не обновляет токен сам.
public struct APITokenRefresher: TokenRefresher {
    /// Сервер ответил на обновление не так, как описано в контракте (не 200 и не 401), — «попробовать позже».
    public struct UnexpectedStatus: Error, Equatable {
        public let status: Int
    }

    private let api: any APIProtocol

    public init(api: any APIProtocol) {
        self.api = api
    }

    public func refresh(_ refreshToken: String) async throws -> RefreshResult {
        let output = try await api.refreshSession(body: .json(.init(refreshToken: refreshToken)))
        switch output {
        case .ok(let response):
            return .renewed(AuthTokens(try response.body.json))
        case .undocumented(let status, let payload) where status == 401:
            // `refresh_invalid` или `refresh_reused` — войти заново. Код нужен только для объяснения игроку.
            return .rejected(code: await Self.problemCode(payload.body))
        case .undocumented(let status, _):
            throw UnexpectedStatus(status: status)
        }
    }

    /// Поле `code` из ProblemDetails; тело может быть пустым или не JSON — тогда `nil`.
    static func problemCode(_ body: HTTPBody?) async -> String? {
        guard let body, let data = try? await Data(collecting: body, upTo: 64 * 1024) else { return nil }
        return (try? JSONDecoder().decode(Problem.self, from: data))?.code
    }

    private struct Problem: Decodable {
        let code: String?
    }
}
