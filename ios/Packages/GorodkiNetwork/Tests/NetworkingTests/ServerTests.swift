import Foundation
import GorodkiAPI
import HTTPTypes
import OpenAPIRuntime
import Testing

@testable import Networking

@Suite("Адрес сервера из настроек сборки")
struct ServerURLTests {
    @Test(
        "Не задан или задан неверно — «не настроен»",
        arguments: [
            nil, "", "   ",
            // Переменная сборки не подставилась.
            "$(GORODKI_SERVER_URL)",
            "localhost:5080", "gorodki.example", "ftp://gorodki.example", "https://",
            // Логин и пароль в адресе репозиторию не место.
            "https://user:secret@gorodki.example",
            "https://gorodki.example?key=1", "https://gorodki.example#top",
        ] as [String?])
    func notConfigured(_ value: String?) {
        #expect(ServerURL.parse(value) == nil)
    }

    @Test(
        "Адрес без «/» на конце — пути API начинаются с «/»",
        arguments: [
            ("https://gorodki.example", "https://gorodki.example"),
            ("https://gorodki.example/", "https://gorodki.example"),
            ("  https://gorodki.example/api//\n", "https://gorodki.example/api"),
            ("http://localhost:5080", "http://localhost:5080"),
            ("http://192.168.1.10:5080/", "http://192.168.1.10:5080"),
        ])
    func configured(_ value: String, _ expected: String) {
        #expect(ServerURL.parse(value)?.absoluteString == expected)
    }

    @Test("Клиент с таким адресом ходит по правильным путям")
    func requestPath() async throws {
        let transport = RecordingTransport()
        let client = ClientFactory.make(
            serverURL: try #require(ServerURL.parse("https://gorodki.example/api/")),
            tokens: TokenStore(storage: InMemoryTokenStorage()), transport: transport)

        _ = try? await client.getHealth()

        #expect(await transport.urls == ["https://gorodki.example/api/health"])
    }

    /// Запоминает, куда ушёл запрос (как его склеивает URLSessionTransport: адрес + путь), и отвечает 503.
    actor RecordingTransport: ClientTransport {
        private(set) var urls: [String] = []

        func send(
            _ request: HTTPRequest, body: HTTPBody?, baseURL: URL, operationID: String
        ) async throws -> (HTTPResponse, HTTPBody?) {
            urls.append(baseURL.absoluteString + (request.path ?? ""))
            return (HTTPResponse(status: .serviceUnavailable), nil)
        }
    }
}

@Suite("Проверка связи с сервером (Лаборатория)")
struct ServerCheckTests {
    private static let server = URL(string: "https://api.example")!

    struct Answer: ClientTransport {
        struct ConnectionLost: Error, LocalizedError {
            var errorDescription: String? { "Нет соединения" }
        }

        let status: HTTPResponse.Status?
        let body: String

        func send(
            _ request: HTTPRequest, body: HTTPBody?, baseURL: URL, operationID: String
        ) async throws -> (HTTPResponse, HTTPBody?) {
            guard let status else { throw ConnectionLost() }
            var fields = HTTPFields()
            fields[.contentType] = "application/json"
            return (HTTPResponse(status: status, headerFields: fields), HTTPBody(Data(self.body.utf8)))
        }
    }

    private static func check(_ status: HTTPResponse.Status?, _ body: String = "") async -> ServerCheck {
        let tokens = TokenStore(storage: InMemoryTokenStorage())
        await tokens.signIn(AuthTokens(accessToken: "A1-секрет", refreshToken: "R1"))
        let client = ClientFactory.make(
            serverURL: server, tokens: tokens, transport: Answer(status: status, body: body))
        return await ServerCheck.run(client)
    }

    @Test("Сервер ответил — версия и игровой день")
    func online() async {
        let health = #"""
            {"status":"ok","version":"0.1.0","commit":null,"minskTime":"2026-09-23T21:00:00.1234567+03:00","gameDay":"2026-09-23"}
            """#
        #expect(await Self.check(.ok, health) == .online(version: "0.1.0", gameDay: "2026-09-23"))
    }

    @Test("Код не из контракта")
    func unexpected() async {
        #expect(await Self.check(.serviceUnavailable) == .unexpectedStatus(503))
    }

    @Test("Нет связи — понятный текст без запроса и токена")
    func unreachable() async {
        let result = await Self.check(nil)
        #expect(result == .unreachable("Нет соединения"))
    }
}
