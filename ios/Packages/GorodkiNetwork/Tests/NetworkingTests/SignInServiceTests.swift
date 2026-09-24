import Foundation
import GorodkiAPI
import HTTPTypes
import OpenAPIRuntime
import Testing

@testable import Networking

#if canImport(FoundationNetworking)
    import FoundationNetworking
#endif

@Suite("Вход и выход: POST /auth/google и /auth/logout")
struct SignInServiceTests {
    /// Сервер входа без сети: отвечает заданными ответами по очереди и запоминает, что ему прислали.
    actor Server: ClientTransport {
        struct Down: Error {}

        enum Answer {
            case session(isNewUser: Bool)
            case problem(status: Int, code: String)
            case noContent
            case down
        }

        private var answers: [Answer]
        private(set) var operations: [String] = []
        private(set) var signIns: [Components.Schemas.GoogleSignInRequest] = []
        private(set) var logouts: [Components.Schemas.RefreshRequest] = []

        init(_ answers: Answer...) {
            self.answers = answers
        }

        func send(
            _ request: HTTPRequest, body: HTTPBody?, baseURL: URL, operationID: String
        ) async throws -> (HTTPResponse, HTTPBody?) {
            operations.append(operationID)
            let data = try await Data(collecting: try #require(body), upTo: 4096)
            if operationID == "signInWithGoogle" {
                signIns.append(try JSONDecoder().decode(Components.Schemas.GoogleSignInRequest.self, from: data))
            } else {
                logouts.append(try JSONDecoder().decode(Components.Schemas.RefreshRequest.self, from: data))
            }
            switch answers.isEmpty ? .down : answers.removeFirst() {
            case .session(let isNewUser):
                return Self.json(
                    .ok,
                    #"{"accessToken":"A1","refreshToken":"R1","expiresIn":900,"isNewUser":\#(isNewUser)}"#)
            case .problem(let status, let code):
                return Self.json(.init(code: status), #"{"status":\#(status),"code":"\#(code)"}"#, problem: true)
            case .noContent:
                return (HTTPResponse(status: .noContent), nil)
            case .down:
                throw Down()
            }
        }

        private static func json(
            _ status: HTTPResponse.Status, _ text: String, problem: Bool = false
        ) -> (HTTPResponse, HTTPBody?) {
            var fields = HTTPFields()
            fields[.contentType] = problem ? "application/problem+json" : "application/json"
            return (HTTPResponse(status: status, headerFields: fields), HTTPBody(Data(text.utf8)))
        }
    }

    private static let registration = Registration(
        inviteCode: "  BREST-2026 \n", ageConfirmed: true, consentAccepted: true)

    private static func service(_ server: Server) -> (SignInService, TokenStore) {
        let tokens = TokenStore(storage: InMemoryTokenStorage())
        let api = Client(
            serverURL: URL(string: "https://api.example")!, configuration: GorodkiAPI.configuration, transport: server)
        return (SignInService(api: api, tokens: tokens), tokens)
    }

    @Test("Игрок уже есть — вход одним запросом: только ID-токен, без возраста, согласия и инвайта")
    func existingPlayer() async throws {
        let server = Server(.session(isNewUser: false))
        let (service, tokens) = Self.service(server)

        #expect(await service.signIn(idToken: "G1") == .signedIn(isNewUser: false))
        #expect(await tokens.current() == AuthTokens(accessToken: "A1", refreshToken: "R1"))
        let sent = try #require(await server.signIns.first)
        #expect(
            sent.idToken == "G1" && sent.ageConfirmed == false && sent.inviteCode == nil && sent.consentVersion == nil)
    }

    @Test(
        "Игрока нет — сначала регистрация; с ней — вход тем же ID-токеном, инвайт без пробелов, 16+ и версия соглашения",
        arguments: ["age_confirmation_required", "consent_required", "invite_required"])
    func newPlayer(_ code: String) async throws {
        let server = Server(.problem(status: 403, code: code), .session(isNewUser: true))
        let (service, tokens) = Self.service(server)

        #expect(await service.signIn(idToken: "G1") == .registrationNeeded)
        #expect(await tokens.current() == nil)

        #expect(await service.signIn(idToken: "G1", registration: Self.registration) == .signedIn(isNewUser: true))
        #expect(await tokens.current() != nil)
        let sent = try #require(await server.signIns.last)
        #expect(sent.idToken == "G1")
        #expect(sent.inviteCode == "BREST-2026")
        #expect(sent.ageConfirmed == true)
        #expect(sent.consentVersion == Int32(SignInService.consentVersion))
    }

    @Test("Соглашение не принято — версия не отправляется; игроку — «прими правила», а не «обнови приложение»")
    func consentNotAccepted() async throws {
        let server = Server(.problem(status: 403, code: "consent_required"))
        let (service, _) = Self.service(server)
        let registration = Registration(inviteCode: "BREST-2026", ageConfirmed: true, consentAccepted: false)

        #expect(await service.signIn(idToken: "G1", registration: registration) == .failed(.consentRequired))
        #expect(try #require(await server.signIns.first).consentVersion == nil)
    }

    @Test(
        "Ответы сервера → итог входа",
        arguments: [
            // С регистрацией.
            (403, "invite_invalid", true, SignInOutcome.failed(.inviteInvalid)),
            (403, "invite_required", true, .failed(.inviteRequired)),
            (403, "consent_required", true, .failed(.consentOutdated)),
            (403, "age_confirmation_required", true, .failed(.ageNotConfirmed)),
            (403, "account_deleting", true, .failed(.accountDeleting)),
            // Без регистрации.
            (403, "account_deleting", false, .failed(.accountDeleting)),
            (403, "invite_invalid", false, .failed(.inviteInvalid)),
            (401, "google_token_invalid", false, .failed(.googleRejected)),
            (503, "google_not_configured", false, .failed(.notConfigured)),
            (502, "", false, .failed(.offline)),
            (429, "", false, .failed(.offline)),
            (418, "", false, .failed(.unexpected(status: 418))),
            (403, "что-то новое", false, .failed(.unexpected(status: 403))),
        ])
    func outcomes(status: Int, code: String, registering: Bool, expected: SignInOutcome) async {
        let server = Server(.problem(status: status, code: code))
        let (service, tokens) = Self.service(server)

        #expect(await service.signIn(idToken: "G1", registration: registering ? Self.registration : nil) == expected)
        #expect(await tokens.current() == nil)
    }

    @Test("Конфликт первого входа (409) — один повтор; второй конфликт — ошибка")
    func conflictRetriedOnce() async {
        let lucky = Server(.problem(status: 409, code: "sign_in_conflict"), .session(isNewUser: true))
        #expect(
            await Self.service(lucky).0.signIn(idToken: "G1", registration: Self.registration)
                == .signedIn(isNewUser: true))
        #expect(await lucky.operations.count == 2)

        let unlucky = Server(
            .problem(status: 409, code: "sign_in_conflict"), .problem(status: 409, code: "sign_in_conflict"),
            .session(isNewUser: true))
        #expect(
            await Self.service(unlucky).0.signIn(idToken: "G1", registration: Self.registration)
                == .failed(.unexpected(status: 409)))
        #expect(await unlucky.operations.count == 2)
    }

    @Test("Нет сети — «нет связи», вход не выполнен")
    func offline() async {
        let (service, tokens) = Self.service(Server(.down))

        #expect(await service.signIn(idToken: "G1") == .failed(.offline))
        #expect(await tokens.current() == nil)
    }

    @Test("Выход: сначала отзыв на сервере с refresh-токеном, потом токены стёрты")
    func signOut() async throws {
        let server = Server(.session(isNewUser: false), .noContent)
        let (service, tokens) = Self.service(server)
        _ = await service.signIn(idToken: "G1")

        await service.signOut()

        #expect(await server.operations == ["signInWithGoogle", "logout"])
        #expect(try #require(await server.logouts.first).refreshToken == "R1")
        #expect(await tokens.current() == nil)
    }

    @Test("Выход без сети — токены всё равно стёрты")
    func signOutOffline() async {
        let server = Server(.session(isNewUser: false), .down)
        let (service, tokens) = Self.service(server)
        _ = await service.signIn(idToken: "G1")

        await service.signOut()

        #expect(await server.operations == ["signInWithGoogle", "logout"])
        #expect(await tokens.current() == nil)
    }

    @Test("Выход без входа — без запроса к серверу")
    func signOutSignedOut() async {
        let server = Server()
        let (service, _) = Self.service(server)

        await service.signOut()

        #expect(await server.operations.isEmpty)
    }

    @Test("У каждой ошибки — свой понятный текст, без кодов сервера")
    func messages() {
        let failures: [SignInFailure] = [
            .offline, .notConfigured, .googleRejected, .inviteInvalid, .inviteRequired, .ageNotConfirmed,
            .consentRequired, .consentOutdated, .accountDeleting, .unexpected(status: 418),
        ]
        let texts = failures.map(\.message)
        #expect(Set(texts).count == failures.count)
        for text in texts {
            #expect(!text.isEmpty && !text.contains("_"))
        }
    }
}
