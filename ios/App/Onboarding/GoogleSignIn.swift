import AuthenticationServices
import CryptoKit
import Foundation
import UIKit

/// Вход через Google без SDK (решение 08.10, docs/architecture/ios-app.md, «Вход через Google»): системное окно
/// `ASWebAuthenticationSession`, OAuth 2.0 «код + PKCE» для iOS-клиента Google и обмен кода на ID-токен — то же, что
/// GoogleSignIn делает внутри (AppAuth), но без трёх библиотек. Область — только `openid`: серверу нужен лишь номер
/// аккаунта Google (`sub`), почту и имя приложение не просит (PLAN.md, §3.16).
struct GoogleOAuth: Equatable, Sendable {
    /// Ключ Info.plist ← переменная сборки `GORODKI_GOOGLE_CLIENT_ID` (ios/Config, свой — в Local.xcconfig).
    static let infoPlistKey = "GorodkiGoogleClientID"
    static let authorizationHost = "accounts.google.com"
    static let tokenHost = "oauth2.googleapis.com"
    private static let clientIDSuffix = ".apps.googleusercontent.com"

    /// Client ID iOS-клиента из Google Cloud: `<номер>-<строка>.apps.googleusercontent.com`.
    let clientID: String
    /// Схема возврата — Client ID наоборот (`com.googleusercontent.apps.<номер>-<строка>`): так Google возвращает код
    /// iOS-клиенту. В Info.plist её регистрировать не нужно — возврат ловит само системное окно.
    let callbackScheme: String

    /// `nil` — Client ID не задан или не похож на Client ID iOS-клиента: вход не настроен (#4), кнопка выключена.
    init?(clientID: String?) {
        guard let id = clientID?.trimmingCharacters(in: .whitespacesAndNewlines), id.hasSuffix(Self.clientIDSuffix)
        else { return nil }
        let prefix = id.dropLast(Self.clientIDSuffix.count)
        guard !prefix.isEmpty, prefix.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-") }) else {
            return nil
        }
        self.clientID = id
        self.callbackScheme = "com.googleusercontent.apps." + prefix
    }

    /// Из Info.plist сборки.
    init?(bundle: Bundle) {
        self.init(clientID: bundle.object(forInfoDictionaryKey: Self.infoPlistKey) as? String)
    }

    var redirectURI: String { callbackScheme + ":/oauth2redirect" }

    /// Страница входа Google: выбор аккаунта, код вернётся на `redirectURI` вместе с тем же `state`.
    func authorizationURL(challenge: String, state: String) -> URL? {
        var components = URLComponents()
        components.scheme = "https"
        components.host = Self.authorizationHost
        components.path = "/o/oauth2/v2/auth"
        components.queryItems = [
            URLQueryItem(name: "client_id", value: clientID),
            URLQueryItem(name: "redirect_uri", value: redirectURI),
            URLQueryItem(name: "response_type", value: "code"),
            URLQueryItem(name: "scope", value: "openid"),
            URLQueryItem(name: "code_challenge", value: challenge),
            URLQueryItem(name: "code_challenge_method", value: "S256"),
            URLQueryItem(name: "state", value: state),
            URLQueryItem(name: "prompt", value: "select_account"),
        ]
        return components.url
    }

    /// Код из адреса возврата. `state` не совпал — это не ответ на наш запрос; отказ на странице Google — как закрытое
    /// окно.
    func code(from callback: URL, state: String) throws(GoogleSignInError) -> String {
        guard let components = URLComponents(url: callback, resolvingAgainstBaseURL: false),
            components.scheme?.lowercased() == callbackScheme.lowercased()
        else { throw .unexpected }
        let items = components.queryItems ?? []
        func value(_ name: String) -> String? { items.first { $0.name == name }?.value }
        guard value("state") == state else { throw .unexpected }
        if let error = value("error") {
            throw error == "access_denied" ? .cancelled : .unexpected
        }
        guard let code = value("code"), !code.isEmpty else { throw .unexpected }
        return code
    }

    /// Обмен кода на токены. Секрета у iOS-клиента нет — подлинность обмена держит `code_verifier` (PKCE).
    func tokenRequest(code: String, verifier: String) -> URLRequest? {
        var components = URLComponents()
        components.scheme = "https"
        components.host = Self.tokenHost
        components.path = "/token"
        guard let url = components.url else { return nil }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.httpBody = Data(
            Self.formBody([
                ("grant_type", "authorization_code"), ("code", code), ("client_id", clientID),
                ("redirect_uri", redirectURI), ("code_verifier", verifier),
            ]).utf8)
        return request
    }

    /// ID-токен из ответа на обмен. Его подпись, `aud` и срок проверяет сервер (`POST /auth/google`).
    static func idToken(from data: Data, status: Int) throws(GoogleSignInError) -> String {
        struct TokenResponse: Decodable {
            let idToken: String?

            enum CodingKeys: String, CodingKey {
                case idToken = "id_token"
            }
        }
        guard (200..<300).contains(status),
            let token = (try? JSONDecoder().decode(TokenResponse.self, from: data))?.idToken, !token.isEmpty
        else { throw .unexpected }
        return token
    }

    /// Случайная строка для `code_verifier` (32 байта → 43 знака) и `state`.
    static func randomString() -> String {
        base64URL(SymmetricKey(size: .bits256).withUnsafeBytes { Data($0) })
    }

    /// `code_challenge` для метода S256: SHA-256 от `code_verifier` в base64url без «=».
    static func challenge(for verifier: String) -> String {
        base64URL(Data(SHA256.hash(data: Data(verifier.utf8))))
    }

    static func base64URL(_ data: Data) -> String {
        data.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    /// `application/x-www-form-urlencoded`: кодируется всё, кроме букв, цифр и `-._~`.
    static func formBody(_ fields: [(String, String)]) -> String {
        var allowed = CharacterSet(charactersIn: "-._~")
        allowed.formUnion(CharacterSet(charactersIn: "a"..."z"))
        allowed.formUnion(CharacterSet(charactersIn: "A"..."Z"))
        allowed.formUnion(CharacterSet(charactersIn: "0"..."9"))
        return fields.map { name, value in
            name + "=" + (value.addingPercentEncoding(withAllowedCharacters: allowed) ?? "")
        }.joined(separator: "&")
    }
}

/// Почему вход через Google не дал токена.
enum GoogleSignInError: Error, Equatable, Sendable {
    /// Игрок закрыл окно или отказался на странице Google — не ошибка игры, экран молчит.
    case cancelled
    /// Google не ответил на обмен кода.
    case offline
    /// Ответ не по протоколу OAuth.
    case unexpected

    /// Что показать игроку; `nil` — ничего.
    var message: String? {
        switch self {
        case .cancelled: nil
        case .offline: "Google не ответил. Проверь интернет и попробуй ещё раз."
        case .unexpected: "Не получилось войти через Google. Попробуй ещё раз."
        }
    }
}

/// Окно входа Google: открыть, дождаться возврата с кодом, обменять код на ID-токен.
@MainActor
final class GoogleSignInSession: NSObject, ASWebAuthenticationPresentationContextProviding {
    private let oauth: GoogleOAuth
    private let urlSession: URLSession
    /// Окно живёт, пока идёт вход: без сильной ссылки система закрыла бы его сразу.
    private var session: ASWebAuthenticationSession?

    init(oauth: GoogleOAuth, urlSession: URLSession = .shared) {
        self.oauth = oauth
        self.urlSession = urlSession
    }

    /// ID-токен Google для `POST /auth/google`.
    static func idToken(_ oauth: GoogleOAuth) async throws -> String {
        try await GoogleSignInSession(oauth: oauth).idToken()
    }

    func idToken() async throws -> String {
        let verifier = GoogleOAuth.randomString()
        let state = GoogleOAuth.randomString()
        guard let url = oauth.authorizationURL(challenge: GoogleOAuth.challenge(for: verifier), state: state) else {
            throw GoogleSignInError.unexpected
        }
        let callback = try await present(url)
        let code = try oauth.code(from: callback, state: state)
        guard let request = oauth.tokenRequest(code: code, verifier: verifier) else {
            throw GoogleSignInError.unexpected
        }
        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await urlSession.data(for: request)
        } catch {
            throw GoogleSignInError.offline
        }
        return try GoogleOAuth.idToken(from: data, status: (response as? HTTPURLResponse)?.statusCode ?? 0)
    }

    private func present(_ url: URL) async throws -> URL {
        defer { session = nil }
        return try await withCheckedThrowingContinuation { continuation in
            let session = ASWebAuthenticationSession(url: url, callback: .customScheme(oauth.callbackScheme)) {
                callback, error in
                if let callback {
                    continuation.resume(returning: callback)
                } else if (error as? ASWebAuthenticationSessionError)?.code == .canceledLogin {
                    continuation.resume(throwing: GoogleSignInError.cancelled)
                } else {
                    continuation.resume(throwing: GoogleSignInError.unexpected)
                }
            }
            session.presentationContextProvider = self
            self.session = session
            // Не открылось — обработчик не вызовется, отвечаем сами.
            if !session.start() {
                continuation.resume(throwing: GoogleSignInError.unexpected)
            }
        }
    }

    nonisolated func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
        MainActor.assumeIsolated {
            let scenes = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            if let key = scenes.flatMap(\.windows).first(where: \.isKeyWindow) {
                return key
            }
            return scenes.first.map { UIWindow(windowScene: $0) } ?? ASPresentationAnchor()
        }
    }
}
