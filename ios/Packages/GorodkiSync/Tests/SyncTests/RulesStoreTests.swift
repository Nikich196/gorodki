import Foundation
import GameCore
import GorodkiAPI
import HTTPTypes
import OpenAPIRuntime
import Testing

@testable import Sync

@Suite("Правила для забега: GET /config и последняя известная версия")
struct RulesStoreTests {
    /// Сервер конфига без сети: отдаёт образец `contracts/samples/config.json` с нужной версией или ошибку.
    actor ConfigServer: ClientTransport {
        private var version = 1
        private var maxRunHours: Double = 4
        private var status = 200
        private(set) var requests = 0

        func answer(version: Int, maxRunHours: Double) {
            self.version = version
            self.maxRunHours = maxRunHours
        }

        func fail(status: Int) { self.status = status }

        func send(
            _ request: HTTPRequest, body: HTTPBody?, baseURL: URL, operationID: String
        ) async throws -> (HTTPResponse, HTTPBody?) {
            requests += 1
            guard status == 200 else { return (HTTPResponse(status: .init(code: status)), nil) }
            var sample = try JSONSerialization.jsonObject(with: RulesStoreTests.sample()) as! [String: Any]
            sample["version"] = version
            var rules = sample["rules"] as! [String: Any]
            var capture = rules["capture"] as! [String: Any]
            capture["maxRunHours"] = maxRunHours
            rules["capture"] = capture
            sample["rules"] = rules
            var fields = HTTPFields()
            fields[.contentType] = "application/json"
            return (
                HTTPResponse(status: .ok, headerFields: fields),
                HTTPBody(try JSONSerialization.data(withJSONObject: sample))
            )
        }
    }

    static func sample() throws -> Data {
        var directory = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
        for _ in 0..<10 {
            let candidate = directory.appendingPathComponent("contracts/samples/config.json")
            if FileManager.default.fileExists(atPath: candidate.path) {
                return try Data(contentsOf: candidate)
            }
            directory.deleteLastPathComponent()
        }
        throw CocoaError(.fileNoSuchFile)
    }

    private let server = ConfigServer()

    private func store(_ storage: any RulesStorage = InMemoryRulesStorage()) -> RulesStore {
        let api = Client(
            serverURL: URL(string: "https://api.example")!, configuration: GorodkiAPI.configuration, transport: server)
        return RulesStore(api: api, storage: storage)
    }

    @Test("Конфига ещё не было — версия 1, с которой собрано приложение, без запроса к серверу")
    func bundledUntilFetched() async {
        let store = store()

        #expect(await store.current() == .bundled)
        #expect(await server.requests == 0)
    }

    @Test("Образец ответа сервера разбирается в те же числа, с которыми собрано приложение")
    func serverSampleIsVersionOne() async throws {
        let fetched = try await store().refresh()

        #expect(fetched.version == 1)
        #expect(fetched.rules == .version1)
    }

    @Test("Новая версия с сервера — действует сразу и переживает перезапуск приложения (файл)")
    func newVersionPersists() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let storage = FileRulesStorage(url: directory.appendingPathComponent("rules.json"))
        await server.answer(version: 2, maxRunHours: 3)

        let fresh = try await store(storage).refresh()
        #expect(fresh.version == 2 && fresh.rules.maxRunHours == 3)

        let relaunched = store(storage)
        #expect(await relaunched.current() == fresh)
        #expect(await server.requests == 1)
    }

    @Test("Нет сети или ошибка сервера — ошибка, прежняя версия остаётся")
    func failureKeepsPrevious() async throws {
        let store = store()
        await server.answer(version: 2, maxRunHours: 3)
        let known = try await store.refresh()
        await server.fail(status: 503)

        await #expect(throws: RulesStoreError.unexpectedResponse) {
            try await store.refresh()
        }
        #expect(await store.current() == known)
    }

    @Test("Испорченный файл — как отсутствие: версия, с которой собрано приложение")
    func corruptedFileFallsBack() async {
        let store = RulesStore(api: nil, storage: InMemoryRulesStorage(Data("не JSON".utf8)))
        #expect(await store.current() == .bundled)
    }

    @Test("Сервер не задан — refresh ничего не запрашивает")
    func noServer() async throws {
        let store = RulesStore(api: nil, storage: InMemoryRulesStorage())
        #expect(try await store.refresh() == .bundled)
    }
}
