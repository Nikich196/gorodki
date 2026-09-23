import Foundation
import GorodkiAPI
import HTTPTypes
import OpenAPIRuntime
import Testing

@testable import Sync

/// Движок на настоящем сгенерированном клиенте: ответы без тела (ограничитель частоты, проверка входа) и обрыв сети.
@Suite("Синхронизация через настоящий клиент: почему остановились")
struct ClientStopTests {
    /// Транспорт без сети: на любой запрос — заданный код без тела или ошибка соединения.
    struct BareTransport: ClientTransport {
        struct ConnectionLost: Error {}
        let status: Int?

        func send(
            _ request: HTTPRequest, body: HTTPBody?, baseURL: URL, operationID: String
        ) async throws -> (HTTPResponse, HTTPBody?) {
            guard let status else { throw ConnectionLost() }
            return (HTTPResponse(status: .init(code: status)), nil)
        }
    }

    @Test(
        "Код ответа без тела → причина остановки",
        arguments: [(429, SyncStop.rateLimited), (401, .unauthorized), (500, .offline), (nil, .offline)]
            as [(Int?, SyncStop)])
    func stops(_ status: Int?, _ expected: SyncStop) async throws {
        let store = InMemorySyncStore()
        try await Fixture.record(Fixture.run(), points: 5, into: store)
        let client = Client(
            serverURL: URL(string: "https://api.example")!, configuration: GorodkiAPI.configuration,
            transport: BareTransport(status: status))

        let report = await SyncEngine(store: store, api: client, ownerId: Fixture.owner, now: { Fixture.start })
            .syncOnce()

        #expect(report.stop == expected)
        #expect(try await store.runs().first?.serverState == .unknown)
    }
}
