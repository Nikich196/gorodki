import Foundation
import GorodkiAPI
import HTTPTypes
import OpenAPIRuntime
import Testing

@testable import Networking

#if canImport(FoundationNetworking)
    import FoundationNetworking
#endif

/// Клиент приложения целиком — сгенерированный `Client`, подпись и обновление — на транспорте без сети, который
/// ведёт себя как сервер: проверяет подпись и меняет refresh-токен на новую пару (docs/architecture/auth.md).
@Suite("Клиент приложения: вход и обновление по контракту")
struct ClientFactoryTests {
    private static let server = URL(string: "https://api.example")!

    /// Сервер входа без сети.
    actor AuthServer: ClientTransport {
        /// Действующий access-токен; `nil` — истёк.
        private var access: String? = "A1"
        private var refresh = "R1"
        private var issued = 1
        /// Ответ на обновление вместо обычного.
        private var refreshAnswer: (status: Int, code: String)?
        /// Запросы по порядку: `операция подпись`.
        private(set) var log: [String] = []
        private(set) var refreshBodies: [Components.Schemas.RefreshRequest] = []

        func expireAccessToken() { access = nil }

        func answerRefresh(status: Int, code: String) { refreshAnswer = (status, code) }

        func send(
            _ request: HTTPRequest, body: HTTPBody?, baseURL: URL, operationID: String
        ) async throws -> (HTTPResponse, HTTPBody?) {
            let authorization = request.headerFields[.authorization]
            log.append("\(operationID) \(authorization ?? "-")")
            guard operationID == "refreshSession" else {
                guard let access, authorization == "Bearer \(access)" else {
                    return (HTTPResponse(status: .unauthorized), nil)
                }
                return Self.json(
                    .ok,
                    #"{"id":"p-1","displayName":"Бегун-1234","colorIndex":7,"role":"player","publicProfile":false}"#)
            }
            let data = try await Data(collecting: try #require(body), upTo: 4096)
            let sent = try JSONDecoder().decode(Components.Schemas.RefreshRequest.self, from: data)
            refreshBodies.append(sent)
            if let (status, code) = refreshAnswer {
                return Self.json(
                    .init(code: status), #"{"title":"…","status":\#(status),"code":"\#(code)"}"#, problem: true)
            }
            guard sent.refreshToken == refresh else {
                return Self.json(.unauthorized, #"{"code":"refresh_invalid"}"#, problem: true)
            }
            issued += 1
            access = "A\(issued)"
            refresh = "R\(issued)"
            return Self.json(
                .ok, #"{"accessToken":"A\#(issued)","refreshToken":"R\#(issued)","expiresIn":900,"isNewUser":false}"#)
        }

        private static func json(
            _ status: HTTPResponse.Status, _ text: String, problem: Bool = false
        ) -> (HTTPResponse, HTTPBody?) {
            var fields = HTTPFields()
            fields[.contentType] = problem ? "application/problem+json" : "application/json"
            return (HTTPResponse(status: status, headerFields: fields), HTTPBody(Data(text.utf8)))
        }
    }

    private static func signedIn() async -> TokenStore {
        let store = TokenStore(storage: InMemoryTokenStorage())
        await store.signIn(AuthTokens(accessToken: "A1", refreshToken: "R1"))
        return store
    }

    @Test("Истёк access-токен: POST /auth/refresh без подписи, с refresh-токеном в теле, и повтор запроса")
    func renewsThroughGeneratedClient() async throws {
        let server = AuthServer()
        let store = await Self.signedIn()
        let client = ClientFactory.make(serverURL: Self.server, tokens: store, transport: server)
        await server.expireAccessToken()

        let me = try await client.getMe().ok.body.json

        #expect(me.displayName == "Бегун-1234")
        #expect(await server.log == ["getMe Bearer A1", "refreshSession -", "getMe Bearer A2"])
        #expect(await server.refreshBodies.map(\.refreshToken) == ["R1"])
        #expect(await store.current() == AuthTokens(accessToken: "A2", refreshToken: "R2"))
    }

    @Test("Сервер отверг refresh-токен: вход стёрт с кодом из ответа, вызывающий видит 401")
    func rejectedThroughGeneratedClient() async throws {
        let server = AuthServer()
        let store = await Self.signedIn()
        let client = ClientFactory.make(serverURL: Self.server, tokens: store, transport: server)
        await server.expireAccessToken()
        await server.answerRefresh(status: 401, code: "refresh_reused")

        let output = try await client.getMe()

        guard case .undocumented(statusCode: 401, _) = output else {
            Issue.record("Ожидался 401, а пришло \(output)")
            return
        }
        #expect(await store.current() == nil)
        var events = store.events.makeAsyncIterator()
        #expect(await events.next() == .signedIn)
        #expect(await events.next() == .signedOut(.sessionExpired(code: "refresh_reused")))
    }

    @Test("Сервер недоступен при обновлении (503): запрос — ошибка, вход сохраняется")
    func refreshServerDown() async throws {
        let server = AuthServer()
        let store = await Self.signedIn()
        let client = ClientFactory.make(serverURL: Self.server, tokens: store, transport: server)
        await server.expireAccessToken()
        await server.answerRefresh(status: 503, code: "unavailable")

        let error = await #expect(throws: ClientError.self) {
            try await client.getMe()
        }

        #expect(error?.underlyingError as? APITokenRefresher.UnexpectedStatus == .init(status: 503))
        #expect(await store.current() == AuthTokens(accessToken: "A1", refreshToken: "R1"))
    }

    @Test("Транспорт по умолчанию — своя сессия URLSession с тайм-аутом 30 секунд")
    func defaultTransport() {
        let session = ClientFactory.urlSessionTransport().configuration.session
        #expect(session !== URLSession.shared)
        #expect(session.configuration.timeoutIntervalForRequest == 30)
    }

    @Test("Выход через основной клиент: запрос не подписан, refresh-токен — в теле")
    func logoutUnsigned() async throws {
        let server = AuthServer()
        let store = await Self.signedIn()
        let client = ClientFactory.make(serverURL: Self.server, tokens: store, transport: server)

        _ = try? await client.logout(body: .json(.init(refreshToken: "R1")))

        #expect(await server.log == ["logout -"])
    }
}
