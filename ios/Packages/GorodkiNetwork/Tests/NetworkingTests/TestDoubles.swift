import Foundation
import HTTPTypes
import OpenAPIRuntime
import Synchronization
import Testing

@testable import Networking

/// Обновление токенов без сети: запоминает, с каким refresh-токеном его вызвали, и отвечает заданным итогом.
/// Может «задуматься» (`hold`) — пока не отпустят, так запросы успевают получить 401 одновременно.
actor FakeRefresher: TokenRefresher {
    struct NetworkDown: Error {}

    private(set) var calls: [String] = []
    private var answer: Result<RefreshResult, NetworkDown>
    private var held = false
    private var waiting: [CheckedContinuation<Void, Never>] = []
    private var onRefresh: (@Sendable () async -> Void)?

    init(_ answer: RefreshResult) {
        self.answer = .success(answer)
    }

    init(failing: NetworkDown) {
        self.answer = .failure(failing)
    }

    func hold() { held = true }

    func release() {
        held = false
        waiting.forEach { $0.resume() }
        waiting = []
    }

    /// Выполнить это, пока «сервер» думает над обновлением.
    func whileRefreshing(_ action: @escaping @Sendable () async -> Void) { onRefresh = action }

    func refresh(_ refreshToken: String) async throws -> RefreshResult {
        calls.append(refreshToken)
        if let onRefresh {
            await onRefresh()
        }
        if held {
            await withCheckedContinuation { waiting.append($0) }
        }
        return try answer.get()
    }
}

/// Сервер за подписью: пропускает только запросы с действующим access-токеном, остальным — 401 без тела
/// (так отвечает проверка входа ASP.NET). Запоминает заголовок `Authorization` и тело каждого запроса.
actor Backend {
    private(set) var authorizations: [String?] = []
    private(set) var bodies: [Data?] = []
    private(set) var rejected = 0
    private var valid: String?
    private var beforeAnswer: (@Sendable () async -> Void)?

    init(valid: String?) {
        self.valid = valid
    }

    func accept(_ token: String?) { valid = token }

    /// Выполнить это перед ответом на следующий запрос.
    func beforeNextAnswer(_ action: @escaping @Sendable () async -> Void) { beforeAnswer = action }

    func handle(_ request: HTTPRequest, _ body: HTTPBody?) async throws -> (HTTPResponse, HTTPBody?) {
        let authorization = request.headerFields[.authorization]
        authorizations.append(authorization)
        if let body {
            bodies.append(try await Data(collecting: body, upTo: 1 << 20))
        } else {
            bodies.append(nil)
        }
        if let action = beforeAnswer {
            beforeAnswer = nil
            await action()
        }
        guard let valid, authorization == "Bearer \(valid)" else {
            rejected += 1
            return (HTTPResponse(status: .unauthorized), nil)
        }
        return (HTTPResponse(status: .ok), HTTPBody("{}"))
    }
}

/// Хранилище, которое бывает недоступно (Keychain до первой разблокировки телефона).
final class FlakyStorage: TokenStorage {
    struct Unavailable: Error {}

    private let state = Mutex<(data: Data?, available: Bool)>((nil, true))

    func setAvailable(_ available: Bool) { state.withLock { $0.available = available } }

    var stored: Data? { state.withLock { $0.data } }

    func load() throws -> Data? {
        try state.withLock { state in
            guard state.available else { throw Unavailable() }
            return state.data
        }
    }

    func save(_ data: Data) throws {
        try state.withLock { state in
            guard state.available else { throw Unavailable() }
            state.data = data
        }
    }

    func delete() throws {
        try state.withLock { state in
            guard state.available else { throw Unavailable() }
            state.data = nil
        }
    }
}

/// JWT без подписи — телефону нужна только полезная нагрузка.
enum Jwt {
    static func make(_ payload: String) -> String {
        "\(base64URL(#"{"alg":"HS256","typ":"JWT"}"#)).\(base64URL(payload)).c2lnbmF0dXJl"
    }

    static func base64URL(_ text: String) -> String {
        Data(text.utf8).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}

/// Ждёт условия (не дольше 5 секунд): одновременные запросы должны успеть дойти до нужного места.
func waitUntil(_ condition: @Sendable () async -> Bool) async throws {
    for _ in 0..<2_500 {
        if await condition() { return }
        try await Task.sleep(for: .milliseconds(2))
    }
    Issue.record("Условие не выполнилось за 5 секунд")
}
