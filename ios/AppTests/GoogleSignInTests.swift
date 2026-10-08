import Foundation
import Networking
import Synchronization
import Testing

@testable import Gorodki

/// Вход через Google без SDK (`GoogleOAuth`, `GoogleSignInSession`): всё, что можно проверить без окна Google, —
/// Client ID, страница входа, PKCE, разбор возврата, обмен кода и что видит игрок при сбое.
@Suite("Вход через Google: OAuth с PKCE")
@MainActor
struct GoogleSignInTests {
    private static let clientID = "123456789012-abc123def.apps.googleusercontent.com"
    private let oauth = GoogleOAuth(clientID: clientID)!

    @Test("Client ID iOS-клиента → схема возврата наоборот; пустой или чужой вид — вход не настроен")
    func clientID() {
        #expect(oauth.callbackScheme == "com.googleusercontent.apps.123456789012-abc123def")
        #expect(oauth.redirectURI == "com.googleusercontent.apps.123456789012-abc123def:/oauth2redirect")
        #expect(GoogleOAuth(clientID: "  \(Self.clientID)\n")?.clientID == Self.clientID)
        #expect(GoogleOAuth(clientID: nil) == nil)
        #expect(GoogleOAuth(clientID: "") == nil)
        #expect(GoogleOAuth(clientID: "$(GORODKI_GOOGLE_CLIENT_ID)") == nil)
        #expect(GoogleOAuth(clientID: ".apps.googleusercontent.com") == nil)
        #expect(GoogleOAuth(clientID: "12/34.apps.googleusercontent.com") == nil)
    }

    @Test("В сборке без Client ID вход через Google выключен (Info.plist: ключ есть, значение пустое)")
    func notConfiguredInTestBuild() {
        #expect(Bundle.main.object(forInfoDictionaryKey: GoogleOAuth.infoPlistKey) as? String == "")
        #expect(GoogleOAuth(bundle: .main) == nil)
    }

    @Test("Страница входа: код с PKCE S256, только openid, возврат на схему клиента, state")
    func authorizationURL() throws {
        let url = try #require(oauth.authorizationURL(challenge: "CH", state: "ST"))
        let components = try #require(URLComponents(url: url, resolvingAgainstBaseURL: false))
        #expect(components.scheme == "https")
        #expect(components.host == "accounts.google.com")
        #expect(components.path == "/o/oauth2/v2/auth")
        let items = Dictionary(uniqueKeysWithValues: (components.queryItems ?? []).map { ($0.name, $0.value ?? "") })
        #expect(items["client_id"] == Self.clientID)
        #expect(items["redirect_uri"] == oauth.redirectURI)
        #expect(items["response_type"] == "code")
        #expect(items["scope"] == "openid", "Почту и имя не просим — серверу нужен только sub")
        #expect(items["code_challenge"] == "CH")
        #expect(items["code_challenge_method"] == "S256")
        #expect(items["state"] == "ST")
    }

    @Test("PKCE: пример из RFC 7636 (приложение B); случайные строки — 43 знака base64url и не повторяются")
    func pkce() {
        #expect(
            GoogleOAuth.challenge(for: "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")
                == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM")
        let first = GoogleOAuth.randomString()
        let second = GoogleOAuth.randomString()
        #expect(first.count == 43)
        #expect(first != second)
        let urlSafe = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_")
        #expect(first.unicodeScalars.allSatisfy { urlSafe.contains($0) })
    }

    @Test("Возврат: код при своём state; чужой state, чужая схема, нет кода — сбой; отказ на странице Google — отмена")
    func callback() throws {
        let scheme = oauth.callbackScheme
        let ok = try #require(URL(string: "\(scheme):/oauth2redirect?state=ST&code=4/0AbC-d_e&scope=openid"))
        #expect(try oauth.code(from: ok, state: "ST") == "4/0AbC-d_e")

        let wrongState = try #require(URL(string: "\(scheme):/oauth2redirect?state=XX&code=C"))
        #expect(throws: GoogleSignInError.unexpected) { try oauth.code(from: wrongState, state: "ST") }
        let wrongScheme = try #require(URL(string: "com.example.app:/oauth2redirect?state=ST&code=C"))
        #expect(throws: GoogleSignInError.unexpected) { try oauth.code(from: wrongScheme, state: "ST") }
        let noCode = try #require(URL(string: "\(scheme):/oauth2redirect?state=ST"))
        #expect(throws: GoogleSignInError.unexpected) { try oauth.code(from: noCode, state: "ST") }
        let denied = try #require(URL(string: "\(scheme):/oauth2redirect?state=ST&error=access_denied"))
        #expect(throws: GoogleSignInError.cancelled) { try oauth.code(from: denied, state: "ST") }
        let deniedForeign = try #require(URL(string: "\(scheme):/oauth2redirect?state=XX&error=access_denied"))
        #expect(throws: GoogleSignInError.unexpected) { try oauth.code(from: deniedForeign, state: "ST") }
    }

    @Test("Обмен кода: POST формой на oauth2.googleapis.com/token, без секрета, с code_verifier")
    func tokenRequest() throws {
        let request = try #require(oauth.tokenRequest(code: "4/0AbC+d e", verifier: "V-1_2.3~"))
        #expect(request.url?.absoluteString == "https://oauth2.googleapis.com/token")
        #expect(request.httpMethod == "POST")
        #expect(request.value(forHTTPHeaderField: "Content-Type") == "application/x-www-form-urlencoded")
        let body = try #require(request.httpBody.flatMap { String(data: $0, encoding: .utf8) })
        let redirect = "com.googleusercontent.apps.123456789012-abc123def%3A%2Foauth2redirect"
        #expect(
            body == "grant_type=authorization_code&code=4%2F0AbC%2Bd%20e&client_id=\(Self.clientID)"
                + "&redirect_uri=\(redirect)&code_verifier=V-1_2.3~")
        #expect(!body.contains("client_secret"))
    }

    @Test("Ответ на обмен: id_token при 2xx; ошибка Google или ответ без токена — сбой")
    func idToken() throws {
        let ok = Data(#"{"access_token":"a","id_token":"eyJ.x.y","expires_in":3599}"#.utf8)
        #expect(try GoogleOAuth.idToken(from: ok, status: 200) == "eyJ.x.y")
        #expect(throws: GoogleSignInError.unexpected) {
            try GoogleOAuth.idToken(from: Data(#"{"error":"invalid_grant"}"#.utf8), status: 400)
        }
        #expect(throws: GoogleSignInError.unexpected) {
            try GoogleOAuth.idToken(from: Data(#"{"access_token":"a"}"#.utf8), status: 200)
        }
        #expect(throws: GoogleSignInError.unexpected) { try GoogleOAuth.idToken(from: ok, status: 500) }
    }

    @Test("Онбординг: закрытое окно Google — без ошибки; нет связи с Google — текст на шаге входа, сервер не трогаем")
    func onboardingFailures() async {
        let serverCalls = Mutex(0)
        let signIn: OnboardingModel.SignIn = { _, _ in
            serverCalls.withLock { $0 += 1 }
            return .signedIn(isNewUser: false)
        }
        let cancelled = OnboardingModel(signIn: signIn, googleToken: { throw GoogleSignInError.cancelled })
        #expect(cancelled.signInAvailable)
        await cancelled.signInWithGoogle()
        #expect(cancelled.errorMessage(on: .signIn) == nil)

        let offline = OnboardingModel(signIn: signIn, googleToken: { throw GoogleSignInError.offline })
        await offline.signInWithGoogle()
        #expect(offline.errorMessage(on: .signIn) == GoogleSignInError.offline.message)
        #expect(serverCalls.withLock { $0 } == 0)
        #expect(!offline.isSigningIn)
    }
}
