import Foundation
import HTTPTypes
import OpenAPIRuntime
import Synchronization

#if canImport(FoundationNetworking)
    import FoundationNetworking
#endif

/// Ответ «потерялся»: сервер запрос выполнил, а телефон ответа не получил (обрыв связи).
struct ResponseLost: Error {
    var operationID: String
}

/// Транспорт, который теряет ответы на взведённые операции. Запрос уходит на настоящий сервер и выполняется — так
/// проверяется, что повтор после обрыва не задваивает забег, куски и заявки. Клиент строится через
/// `ClientFactory.make`, то есть тем же путём, что в приложении; SyncEngine видит потерю как `.offline`.
final class LossyTransport: ClientTransport, Sendable {
    private struct State {
        var armed: [String: Int] = [:]
        var requests: [String] = []
        var dropped: [String] = []
    }

    private let base: any ClientTransport
    private let state = Mutex(State())

    init(base: any ClientTransport) {
        self.base = base
    }

    /// Потерять следующие `times` ответов на каждую операцию.
    func arm(_ operations: [String: Int]) {
        state.withLock { state in
            for (operation, times) in operations { state.armed[operation, default: 0] += times }
        }
    }

    /// Все запросы по порядку (operationId).
    var requests: [String] { state.withLock { $0.requests } }
    /// Потерянные ответы по порядку.
    var dropped: [String] { state.withLock { $0.dropped } }

    func count(_ operation: String) -> Int { requests.filter { $0 == operation }.count }

    func send(
        _ request: HTTPRequest, body: HTTPBody?, baseURL: URL, operationID: String
    ) async throws -> (HTTPResponse, HTTPBody?) {
        state.withLock { $0.requests.append(operationID) }
        let (response, responseBody) = try await base.send(
            request, body: body, baseURL: baseURL, operationID: operationID)
        let drop = state.withLock { state -> Bool in
            guard let left = state.armed[operationID], left > 0 else { return false }
            state.armed[operationID] = left - 1
            state.dropped.append(operationID)
            return true
        }
        guard drop else { return (response, responseBody) }
        // Тело дочитываем: сервер должен закончить ответ, как при настоящем обрыве после отправки.
        if let responseBody { _ = try? await Data(collecting: responseBody, upTo: 4 << 20) }
        throw ResponseLost(operationID: operationID)
    }
}
