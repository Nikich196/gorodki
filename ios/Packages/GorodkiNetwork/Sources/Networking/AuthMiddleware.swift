import Foundation
import HTTPTypes
import OpenAPIRuntime

/// Подпись запросов access-токеном и переживание его истечения (docs/architecture/auth.md).
///
/// На 401 — обновление пары через `TokenStore` (одно на все одновременные запросы) и один повтор с новым токеном.
/// Если входа больше нет, вызывающий получает исходный 401 (синхронизация остановится с `unauthorized`), а `TokenStore`
/// сообщает о выходе. Если обновление не удалось из-за сети, запрос завершается этой ошибкой — вход сохраняется.
public struct AuthMiddleware: ClientMiddleware {
    /// Запросы входа не подписываются и не обновляются: ключ у них в теле, а 401 от `/auth/refresh` не должен запускать
    /// обновление по кругу.
    static let unsignedOperations: Set<String> = ["signInWithGoogle", "refreshSession", "logout"]

    /// Предел тела, которое приходится читать в память ради повтора. Тела клиента — JSON в килобайты (кусок трека —
    /// до 120 точек), они и так в памяти; предел — страховка от бесконечного потока.
    static let maxReplayableBody = 8 * 1024 * 1024

    private let tokens: TokenStore
    private let refresher: any TokenRefresher

    public init(tokens: TokenStore, refresher: any TokenRefresher) {
        self.tokens = tokens
        self.refresher = refresher
    }

    public func intercept(
        _ request: HTTPRequest, body: HTTPBody?, baseURL: URL, operationID: String,
        next: @Sendable (HTTPRequest, HTTPBody?, URL) async throws -> (HTTPResponse, HTTPBody?)
    ) async throws -> (HTTPResponse, HTTPBody?) {
        guard !Self.unsignedOperations.contains(operationID), let signed = await tokens.current() else {
            // Без входа запрос уходит как есть: сервер ответит 401, если адрес закрыт.
            return try await next(request, body, baseURL)
        }
        let body = try await Self.replayable(body)
        let (response, responseBody) = try await next(Self.authorized(request, signed.accessToken), body, baseURL)
        guard response.status == .unauthorized else { return (response, responseBody) }
        guard let renewed = try await tokens.renew(after: signed.accessToken, using: refresher) else {
            return (response, responseBody)
        }
        // Второй 401 возвращается как есть: ещё одно обновление уже ничего не изменит.
        return try await next(Self.authorized(request, renewed.accessToken), body, baseURL)
    }

    static func authorized(_ request: HTTPRequest, _ accessToken: String) -> HTTPRequest {
        var request = request
        request.headerFields[.authorization] = "Bearer \(accessToken)"
        return request
    }

    /// Тело, которое можно отправить второй раз. Тела сгенерированного клиента уже такие (данные в памяти), одноразовое
    /// (поток) читается в память.
    static func replayable(_ body: HTTPBody?) async throws -> HTTPBody? {
        guard let body, body.iterationBehavior == .single else { return body }
        return HTTPBody(try await Data(collecting: body, upTo: maxReplayableBody))
    }
}
