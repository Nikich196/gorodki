import Foundation
import GorodkiAPI
import HTTPTypes
import OpenAPIRuntime
import Testing

@testable import Networking

@Suite("Аккаунт: зоны приватности, удаление, «мои данные»")
struct AccountServiceTests {
    /// Сервер, отвечающий одним заданным ответом; запоминает метод и путь запросов.
    actor Server: ClientTransport {
        private var status = 200
        private var json = "{}"
        private(set) var requests: [String] = []
        private(set) var bodies: [String] = []

        func answer(_ status: Int, _ json: String = "{}") {
            self.status = status
            self.json = json
        }

        func send(
            _ request: HTTPRequest, body: HTTPBody?, baseURL: URL, operationID: String
        ) async throws -> (HTTPResponse, HTTPBody?) {
            requests.append("\(request.method.rawValue) \(request.path ?? "")")
            if let body {
                bodies.append(String(decoding: try await Data(collecting: body, upTo: 1 << 16), as: UTF8.self))
            }
            var fields = HTTPFields()
            fields[.contentType] = status >= 400 ? "application/problem+json" : "application/json"
            let hasBody = status != 204 && status != 404
            return (
                HTTPResponse(status: .init(code: status), headerFields: fields),
                hasBody ? HTTPBody(Data(json.utf8)) : nil
            )
        }
    }

    private let server = Server()

    private var service: AccountService {
        AccountService(
            api: Client(
                serverURL: URL(string: "https://api.example")!, configuration: GorodkiAPI.configuration,
                transport: server))
    }

    private static let zone =
        #"{"id":"0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b","lat":52.1,"lon":23.7,"radiusMeters":200,"createdAtMs":1}"#

    @Test("Зоны: список, новая по точке, удаление; уже удалённая — не ошибка")
    func privacyZones() async throws {
        await server.answer(200, "[\(Self.zone)]")
        #expect(try await service.privacyZones().map(\.radiusMeters) == [200])

        await server.answer(201, Self.zone)
        #expect(try await service.addPrivacyZone(latitude: 52.1, longitude: 23.7).lat == 52.1)
        let sent = try JSONSerialization.jsonObject(with: Data(try #require(await server.bodies.last).utf8))
        #expect(sent as? [String: Double] == ["lat": 52.1, "lon": 23.7])

        await server.answer(404)
        try await service.removePrivacyZone(id: "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b")
        #expect(
            await server.requests == [
                "GET /me/privacy-zones", "POST /me/privacy-zones",
                "DELETE /me/privacy-zones/0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b",
            ])
    }

    @Test("Зон уже сколько можно — отказ с кодом сервера")
    func tooManyZones() async throws {
        await server.answer(409, #"{"status":409,"code":"zone_limit"}"#)
        await #expect(throws: AccountServiceError.rejected(status: 409, code: "zone_limit")) {
            _ = try await service.addPrivacyZone(latitude: 52.1, longitude: 23.7)
        }
    }

    @Test("Удаление аккаунта: DELETE /me, сервер назвал срок; аккаунта уже нет — своя ошибка")
    func deleteAccount() async throws {
        await server.answer(202, #"{"requestedAtMs":1000,"deleteByMs":2000}"#)
        #expect(try await service.deleteAccount().deleteByMs == 2000)
        #expect(await server.requests == ["DELETE /me"])

        await server.answer(404)
        await #expect(throws: AccountServiceError.notFound) { _ = try await service.deleteAccount() }
    }

    @Test("«Мои данные» — JSON ответа сервера, читается обратно")
    func exportData() async throws {
        let body =
            #"{"exportedAtMs":5,"profile":{"id":"0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b","displayName":"Н","colorIndex":1,"role":"player","publicProfile":false,"createdAtMs":1},"sessions":[],"runs":[],"captures":[],"land":[],"fog":[],"privacyZones":[],"rankings":[]}"#
        await server.answer(200, body)
        let data = try await service.exportData()
        #expect(await server.requests == ["GET /me/export"])
        let object = try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
        #expect(object["exportedAtMs"] as? Int == 5)
        #expect((object["profile"] as? [String: Any])?["displayName"] as? String == "Н")
    }
}
