import Foundation
import GorodkiAPI
import OpenAPIRuntime
import OpenAPIURLSession

#if canImport(FoundationNetworking)
    import FoundationNetworking
#endif

/// Сборка клиента API для приложения: транспорт URLSession, подпись запросов и обновление токена при 401.
public enum ClientFactory {
    /// Клиент, которым пользуются синхронизация и экраны. `transport` подменяется в тестах.
    public static func make(
        serverURL: URL, tokens: TokenStore, transport: any ClientTransport = urlSessionTransport()
    ) -> Client {
        // Обновление идёт отдельным клиентом на том же транспорте, но без AuthMiddleware.
        let refresher = APITokenRefresher(
            api: Client(serverURL: serverURL, configuration: GorodkiAPI.configuration, transport: transport))
        return Client(
            serverURL: serverURL, configuration: GorodkiAPI.configuration, transport: transport,
            middlewares: [AuthMiddleware(tokens: tokens, refresher: refresher)])
    }

    /// Транспорт на своей сессии URLSession:
    /// - без cookie и кэша на диске (`ephemeral`): всё состояние — в токенах и очереди синхронизации;
    /// - тайм-аут 30 секунд без данных: «нет сети» распознаётся быстро, очередь дождётся следующего прохода;
    /// - ответы целиком в памяти (`buffered`): они — JSON в килобайты, а после 401 первый ответ просто выбрасывается.
    public static func urlSessionTransport() -> URLSessionTransport {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 30
        return URLSessionTransport(
            configuration: .init(session: URLSession(configuration: configuration), httpBodyProcessingMode: .buffered))
    }
}
