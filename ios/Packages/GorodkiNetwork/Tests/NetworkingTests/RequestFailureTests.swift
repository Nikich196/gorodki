import Foundation
import HTTPTypes
import OpenAPIRuntime
import Testing

@testable import Networking

@Suite("Ошибки экранов: нет сети, сервер недоступен, отказ — тексты для игрока (PLAN.md, §5, экран 27)")
struct RequestFailureTests {
    private struct Other: Error {}

    @Test("Ответы сервера: 401 — войти снова, 404 — аккаунта нет, 503 без кода — недоступен, 500 — ошибка сервера")
    func statuses() {
        #expect(RequestFailure(AccountServiceError.unexpectedStatus(401)) == .signedOut)
        #expect(RequestFailure(AccountServiceError.notFound) == .notFound)
        #expect(RequestFailure(AccountServiceError.unexpectedStatus(503)) == .serverUnavailable)
        #expect(RequestFailure(AccountServiceError.unexpectedStatus(502)) == .serverUnavailable)
        #expect(RequestFailure(AccountServiceError.unexpectedStatus(500)) == .serverError(status: 500))
        #expect(RequestFailure(AccountServiceError.unexpectedStatus(418)) == .rejected(status: 418, code: nil))
        #expect(RequestFailure(AccountServiceError.unexpectedStatus(302)) == .unexpected(status: 302))
    }

    @Test("Отказы по коду: зон слишком много, туман занят — свой текст; занятый туман можно повторить")
    func rejections() {
        let limit = RequestFailure(AccountServiceError.rejected(status: 409, code: "zone_limit"))
        #expect(limit == .rejected(status: 409, code: "zone_limit"))
        #expect(limit.message.contains("Больше приватных зон нельзя"))
        #expect(!limit.isRetryable)

        let busy = RequestFailure(AccountServiceError.rejected(status: 503, code: "fog_clear_busy"))
        #expect(busy == .rejected(status: 503, code: "fog_clear_busy"))
        #expect(busy.message.contains("Ничего не стёрто"))
        #expect(busy.isRetryable)

        #expect(RequestFailure.rejected(status: 400, code: "other").message.contains("(400, other)"))
    }

    @Test(
        "Транспорт: нет интернета — «нет сети», хост не отвечает — «сервер недоступен», в том числе внутри ClientError")
    func transport() {
        #expect(RequestFailure(URLError(.notConnectedToInternet)) == .offline)
        #expect(RequestFailure(URLError(.networkConnectionLost)) == .offline)
        #expect(RequestFailure(URLError(.timedOut)) == .serverUnavailable)
        #expect(RequestFailure(URLError(.cannotConnectToHost)) == .serverUnavailable)
        #expect(RequestFailure(Other()) == .offline)

        let wrapped = ClientError(
            operationID: "getFogSummary", operationInput: (), causeDescription: "transport",
            underlyingError: URLError(.cannotFindHost))
        #expect(RequestFailure(wrapped) == .serverUnavailable)
        let answered = ClientError(
            operationID: "getMe", operationInput: (), response: HTTPResponse(status: .serviceUnavailable),
            causeDescription: "decoding", underlyingError: Other())
        #expect(RequestFailure(answered) == .serverUnavailable)
    }

    @Test("У каждого случая есть заголовок и текст, числа — без дробей через точку")
    func texts() {
        let all: [RequestFailure] = [
            .notConfigured, .offline, .serverUnavailable, .serverError(status: 500), .signedOut, .notFound,
            .rejected(status: 409, code: "zone_limit"), .rejected(status: 400, code: "zone_invalid"),
            .unexpected(status: nil), .unexpected(status: 302),
        ]
        for failure in all {
            #expect(!failure.title.isEmpty)
            #expect(failure.message.hasSuffix("."))
        }
        #expect(RequestFailure.offline.isRetryable)
        #expect(!RequestFailure.notConfigured.isRetryable)
    }
}
