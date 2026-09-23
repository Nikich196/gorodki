import Foundation
import HTTPTypes
import OpenAPIRuntime
import Testing

@testable import Networking

@Suite("Подпись запросов и обновление токена при 401")
struct AuthMiddlewareTests {
    private static let base = URL(string: "https://api.example")!
    private static let old = AuthTokens(accessToken: "A1", refreshToken: "R1")
    private static let new = AuthTokens(accessToken: "A2", refreshToken: "R2")

    private static func request(_ method: HTTPRequest.Method = .get, _ path: String = "/me") -> HTTPRequest {
        HTTPRequest(method: method, scheme: nil, authority: nil, path: path)
    }

    /// Вошедший игрок со старой парой.
    private static func signedIn(_ storage: InMemoryTokenStorage = .init()) async -> TokenStore {
        let store = TokenStore(storage: storage)
        await store.signIn(old)
        return store
    }

    private static func send(
        _ middleware: AuthMiddleware, to backend: Backend, operation: String = "getMe", body: HTTPBody? = nil
    ) async throws -> HTTPResponse.Status {
        let (response, _) = try await middleware.intercept(
            request(), body: body, baseURL: base, operationID: operation,
            next: { request, body, _ in try await backend.handle(request, body) })
        return response.status
    }

    @Test("Запрос подписан access-токеном")
    func signs() async throws {
        let backend = Backend(valid: "A1")
        let refresher = FakeRefresher(.renewed(Self.new))
        let middleware = AuthMiddleware(tokens: await Self.signedIn(), refresher: refresher)

        #expect(try await Self.send(middleware, to: backend) == .ok)
        #expect(await backend.authorizations == ["Bearer A1"])
        #expect(await refresher.calls.isEmpty)
    }

    @Test("Без входа запрос уходит без подписи, 401 возвращается как есть")
    func unsigned() async throws {
        let backend = Backend(valid: "A1")
        let refresher = FakeRefresher(.renewed(Self.new))
        let middleware = AuthMiddleware(tokens: TokenStore(storage: InMemoryTokenStorage()), refresher: refresher)

        #expect(try await Self.send(middleware, to: backend) == .unauthorized)
        #expect(await backend.authorizations == [nil])
        #expect(await refresher.calls.isEmpty)
    }

    @Test(
        "Запросы входа не подписываются и на 401 не обновляют токен",
        arguments: ["signInWithGoogle", "refreshSession", "logout"])
    func authOperations(_ operation: String) async throws {
        let backend = Backend(valid: nil)
        let refresher = FakeRefresher(.renewed(Self.new))
        let middleware = AuthMiddleware(tokens: await Self.signedIn(), refresher: refresher)

        #expect(try await Self.send(middleware, to: backend, operation: operation) == .unauthorized)
        #expect(await backend.authorizations == [nil])
        #expect(await refresher.calls.isEmpty)
    }

    @Test("401 → одно обновление → повтор с новым токеном; новая пара сохранена")
    func renewsOnce() async throws {
        let storage = InMemoryTokenStorage()
        let store = await Self.signedIn(storage)
        let backend = Backend(valid: "A2")
        let refresher = FakeRefresher(.renewed(Self.new))
        let middleware = AuthMiddleware(tokens: store, refresher: refresher)

        #expect(try await Self.send(middleware, to: backend) == .ok)

        #expect(await backend.authorizations == ["Bearer A1", "Bearer A2"])
        #expect(await refresher.calls == ["R1"])
        #expect(await store.current() == Self.new)
        #expect(await TokenStore(storage: storage).current() == Self.new)

        // Следующие запросы — сразу с новым токеном.
        #expect(try await Self.send(middleware, to: backend) == .ok)
        #expect(await backend.authorizations.last == "Bearer A2")
        #expect(await refresher.calls.count == 1)
    }

    @Test("Одновременные 401 — ровно одно обновление, все запросы повторены с новым токеном")
    func singleFlight() async throws {
        let store = await Self.signedIn()
        let backend = Backend(valid: "A2")
        let refresher = FakeRefresher(.renewed(Self.new))
        await refresher.hold()
        let middleware = AuthMiddleware(tokens: store, refresher: refresher)
        let count = 20

        let statuses = try await withThrowingTaskGroup(of: HTTPResponse.Status.self) { group in
            for _ in 0..<count {
                group.addTask { try await Self.send(middleware, to: backend) }
            }
            // Обновление «думает», пока все запросы не получат 401 со старым токеном.
            try await waitUntil { await backend.rejected == count }
            try await waitUntil { await refresher.calls.count >= 1 }
            try await Task.sleep(for: .milliseconds(50))
            await refresher.release()
            var statuses: [HTTPResponse.Status] = []
            for try await status in group {
                statuses.append(status)
            }
            return statuses
        }

        #expect(statuses == Array(repeating: .ok, count: count))
        #expect(await refresher.calls == ["R1"])
        let authorizations = await backend.authorizations
        #expect(authorizations.filter { $0 == "Bearer A1" }.count == count)
        #expect(authorizations.filter { $0 == "Bearer A2" }.count == count)
        #expect(await store.current() == Self.new)
    }

    @Test("Сервер отверг refresh-токен → выход: токены стёрты, вызывающий получает исходный 401")
    func rejectedRefresh() async throws {
        let storage = InMemoryTokenStorage()
        let store = await Self.signedIn(storage)
        let backend = Backend(valid: "A2")
        let refresher = FakeRefresher(.rejected(code: "refresh_reused"))
        let middleware = AuthMiddleware(tokens: store, refresher: refresher)

        #expect(try await Self.send(middleware, to: backend) == .unauthorized)

        #expect(await backend.authorizations == ["Bearer A1"])
        #expect(await store.current() == nil)
        #expect(storage.load() == nil)
        #expect(
            await authEvents(of: store, count: 2) == [.signedIn, .signedOut(.sessionExpired(code: "refresh_reused"))])

        // Дальше запросы уходят без подписи и обновление не повторяется.
        #expect(try await Self.send(middleware, to: backend) == .unauthorized)
        #expect(await backend.authorizations.last == .some(nil))
        #expect(await refresher.calls == ["R1"])
    }

    @Test("Нет сети при обновлении — ошибка запроса, вход сохраняется; следующий 401 обновляет снова")
    func refreshOffline() async throws {
        let store = await Self.signedIn()
        let backend = Backend(valid: "A2")
        let refresher = FakeRefresher(failing: .init())
        let middleware = AuthMiddleware(tokens: store, refresher: refresher)

        await #expect(throws: FakeRefresher.NetworkDown.self) {
            try await Self.send(middleware, to: backend)
        }
        #expect(await store.current() == Self.old)

        await #expect(throws: FakeRefresher.NetworkDown.self) {
            try await Self.send(middleware, to: backend)
        }
        #expect(await refresher.calls == ["R1", "R1"])
    }

    @Test("Второй 401 после обновления возвращается как есть — без второго обновления")
    func secondUnauthorized() async throws {
        let store = await Self.signedIn()
        let backend = Backend(valid: nil)
        let refresher = FakeRefresher(.renewed(Self.new))
        let middleware = AuthMiddleware(tokens: store, refresher: refresher)

        #expect(try await Self.send(middleware, to: backend) == .unauthorized)

        #expect(await backend.authorizations == ["Bearer A1", "Bearer A2"])
        #expect(await refresher.calls == ["R1"])
        #expect(await store.current() == Self.new)
    }

    @Test("Запрос ушёл со старым токеном, пока пару обновляли, — повтор с новой без второго обновления")
    func staleToken() async throws {
        let store = await Self.signedIn()
        let backend = Backend(valid: "A2")
        let refresher = FakeRefresher(.renewed(Self.new))
        let middleware = AuthMiddleware(tokens: store, refresher: refresher)
        // Пока первый запрос в пути, другой запрос обновил пару.
        await backend.beforeNextAnswer {
            _ = try? await store.renew(after: "A1", using: refresher)
        }

        #expect(try await Self.send(middleware, to: backend) == .ok)

        #expect(await backend.authorizations == ["Bearer A1", "Bearer A2"])
        #expect(await refresher.calls == ["R1"])
    }

    @Test("Игрок вышел и вошёл, пока запрос был в пути, — запрос не повторяется под чужим входом")
    func otherSession() async throws {
        let store = await Self.signedIn()
        let backend = Backend(valid: "B1")
        let refresher = FakeRefresher(.renewed(Self.new))
        let middleware = AuthMiddleware(tokens: store, refresher: refresher)
        await backend.beforeNextAnswer {
            await store.signOut()
            await store.signIn(AuthTokens(accessToken: "B1", refreshToken: "RB1"))
        }

        #expect(try await Self.send(middleware, to: backend) == .unauthorized)

        #expect(await backend.authorizations == ["Bearer A1"])
        #expect(await refresher.calls.isEmpty)
        #expect(await store.current() == AuthTokens(accessToken: "B1", refreshToken: "RB1"))
    }

    @Test("Игрок вышел, пока шло обновление, — ответ сервера не возвращает вход")
    func signOutDuringRefresh() async throws {
        let store = await Self.signedIn()
        let backend = Backend(valid: "A2")
        let refresher = FakeRefresher(.renewed(Self.new))
        await refresher.whileRefreshing { await store.signOut() }
        let middleware = AuthMiddleware(tokens: store, refresher: refresher)

        #expect(try await Self.send(middleware, to: backend) == .unauthorized)

        #expect(await store.current() == nil)
        #expect(await backend.authorizations == ["Bearer A1"])
    }

    @Test("Одноразовое тело (поток) уходит и в повторе — те же байты")
    func replaysBody() async throws {
        let store = await Self.signedIn()
        let backend = Backend(valid: "A2")
        let middleware = AuthMiddleware(tokens: store, refresher: FakeRefresher(.renewed(Self.new)))
        let bytes = Array(#"{"claimNo":0,"startSeq":10}"#.utf8)
        let stream = AsyncStream<ArraySlice<UInt8>> { continuation in
            continuation.yield(ArraySlice(bytes.prefix(8)))
            continuation.yield(ArraySlice(bytes.dropFirst(8)))
            continuation.finish()
        }
        let body = HTTPBody(stream, length: .known(Int64(bytes.count)), iterationBehavior: .single)

        #expect(try await Self.send(middleware, to: backend, operation: "claimLoop", body: body) == .ok)

        #expect(await backend.bodies == [Data(bytes), Data(bytes)])
    }
}
