import Foundation
import HTTPTypes
import OpenAPIRuntime
import Testing

@testable import GorodkiAPI

/// Сгенерированный клиент целиком — запрос (метод, путь, параметры, тело) и разбор ответа по коду — на подставном транспорте.
@Suite("Клиент API: запросы и ответы")
struct ClientTests {
    private static let runId = "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5c"
    private static let server = URL(string: "https://api.example")!

    @Test("Заявка петли: POST /runs/{id}/loops, ответ 202 разбирается как «принято»")
    func claimLoop() async throws {
        let pending = try Samples.data("capture-pending")
        let transport = FakeTransport(status: .accepted, contentType: "application/json", body: pending)
        let client = Client(serverURL: Self.server, configuration: GorodkiAPI.configuration, transport: transport)

        let output = try await client.claimLoop(
            path: .init(runId: Self.runId),
            body: .json(
                .init(
                    claimNo: 0, startSeq: 10, endSeq: 300, closure: .crossing, estimatedArea: 12_000,
                    sentAtMs: 1_790_000_600_000)))

        let accepted = try output.accepted.body.json
        #expect(accepted.status == .pending && accepted.waitingFor == "sensors")
        let request = try #require(await transport.recorder.request)
        #expect(request.method == .post && request.path == "/runs/\(Self.runId)/loops")
        let sent = try #require(await transport.recorder.body)
        let json = try #require(try JSONSerialization.jsonObject(with: sent) as? [String: Any])
        #expect(json["closure"] as? String == "crossing")
        #expect((json["sentAtMs"] as? NSNumber)?.int64Value == 1_790_000_600_000)
    }

    @Test("Кусок: 409 — код и занятые номера доступны приложению")
    func chunkConflict() async throws {
        let conflict = try Samples.data("problem-chunk-conflict")
        let transport = FakeTransport(status: .conflict, contentType: "application/problem+json", body: conflict)
        let client = Client(serverURL: Self.server, configuration: GorodkiAPI.configuration, transport: transport)

        let output = try await client.uploadChunk(
            path: .init(runId: Self.runId, firstSeq: 30),
            body: .json(.init(sentAtMs: 1_790_000_600_000, sensorsCompleteThroughMs: 1_790_000_590_000, points: [])))

        let problem = try output.conflict.body.application_problem_plus_json
        #expect(problem.code == "chunk_conflict")
        #expect(problem.additionalProperties["overlaps"] != nil)
        #expect(try #require(await transport.recorder.request).path == "/runs/\(Self.runId)/chunks/30")
    }

    @Test("Карта: параметры запроса и разбор ответа")
    func territory() async throws {
        let transport = FakeTransport(status: .ok, contentType: "application/json", body: try Samples.data("territory"))
        let client = Client(serverURL: Self.server, configuration: GorodkiAPI.configuration, transport: transport)

        let map = try await client.getTerritory(query: .init(league: "run", tiles: "684:5775@3,685:5775")).ok.body.json

        #expect(map.tiles.first?.parcels.count == 2)
        let path = try #require(await transport.recorder.request?.path)
        #expect(path.hasPrefix("/territory?") && path.contains("league=run") && path.contains("tiles=684"))
    }
}

/// Транспорт без сети: запоминает запрос и возвращает заготовленный ответ.
struct FakeTransport: ClientTransport {
    actor Recorder {
        var request: HTTPRequest?
        var body: Data?

        func record(_ request: HTTPRequest, _ body: Data?) {
            self.request = request
            self.body = body
        }
    }

    let status: HTTPResponse.Status
    let contentType: String
    let body: Data
    let recorder = Recorder()

    func send(
        _ request: HTTPRequest, body: HTTPBody?, baseURL: URL, operationID: String
    ) async throws -> (HTTPResponse, HTTPBody?) {
        var sent: Data?
        if let body {
            sent = try await Data(collecting: body, upTo: 1_000_000)
        }
        await recorder.record(request, sent)
        var fields = HTTPFields()
        fields[.contentType] = contentType
        return (HTTPResponse(status: status, headerFields: fields), HTTPBody(self.body))
    }
}
