import Foundation
import GorodkiAPI
import OpenAPIRuntime

/// Что новый игрок заполняет при регистрации (PLAN.md, D10, §3.16).
public struct Registration: Equatable, Sendable {
    public var inviteCode: String
    /// Игрок подтвердил, что ему 16 или больше.
    public var ageConfirmed: Bool
    /// Игрок принял соглашение версии `SignInService.consentVersion` (текст, который показывает приложение).
    public var consentAccepted: Bool

    public init(inviteCode: String, ageConfirmed: Bool, consentAccepted: Bool) {
        self.inviteCode = inviteCode
        self.ageConfirmed = ageConfirmed
        self.consentAccepted = consentAccepted
    }
}

/// Чем кончилась попытка входа.
public enum SignInOutcome: Equatable, Sendable {
    case signedIn(isNewUser: Bool)
    /// Такого игрока ещё нет: нужны инвайт, 16+ и согласие (`Registration`), затем повтор с тем же ID-токеном.
    case registrationNeeded
    case failed(SignInFailure)
}

/// Почему вход не удался — с текстом для игрока.
public enum SignInFailure: Equatable, Sendable {
    /// Нет сети или сервер не ответил.
    case offline
    /// Вход через Google на сервере ещё не настроен (`google_not_configured`).
    case notConfigured
    /// Google не подтвердил вход (`google_token_invalid`).
    case googleRejected
    /// Код не подошёл: его нет, он истёк или приглашения закончились (`invite_invalid`).
    case inviteInvalid
    /// Код не введён (`invite_required`).
    case inviteRequired
    /// Не подтверждён возраст 16+ (`age_confirmation_required`).
    case ageNotConfirmed
    /// Игрок не отметил согласие с правилами (`consent_required` на регистрацию без согласия).
    case consentRequired
    /// Сервер ждёт соглашение новее того, что показало приложение (`consent_required` на регистрацию с согласием):
    /// нужно обновить приложение.
    case consentOutdated
    /// Аккаунт удаляется — войти нельзя (`account_deleting`).
    case accountDeleting
    /// Сервер ответил не так, как описано в контракте.
    case unexpected(status: Int)

    /// Что показать игроку.
    public var message: String {
        switch self {
        case .offline:
            "Нет связи с сервером. Проверь интернет и попробуй ещё раз."
        case .notConfigured:
            "Вход пока не работает: сервер ещё не настроен. Попробуй позже."
        case .googleRejected:
            "Google не подтвердил вход. Попробуй войти ещё раз."
        case .inviteInvalid:
            "Код приглашения не подошёл: его нет, он истёк или приглашения по нему закончились."
        case .inviteRequired:
            "Нужен код приглашения — попроси его у того, кто позвал тебя в игру."
        case .ageNotConfirmed:
            "Играть можно с 16 лет — подтверди возраст."
        case .consentRequired:
            "Чтобы играть, прими пользовательское соглашение и дай согласие на обработку персональных данных."
        case .consentOutdated:
            "Пользовательское соглашение обновилось. Обнови приложение, чтобы принять новую редакцию."
        case .accountDeleting:
            "Этот аккаунт удаляется — войти в него нельзя."
        case .unexpected(let status):
            "Сервер ответил неожиданно (\(status)). Попробуй позже."
        }
    }
}

/// Вход и выход (docs/architecture/auth.md): `POST /auth/google` и `POST /auth/logout`, токены — в `TokenStore`.
/// ID-токен даёт Google Sign-In на телефоне — он подключится, когда будет Client ID (issue #4).
///
/// Существующий игрок входит одним запросом. Для нового сервер отвечает, чего не хватает; первым он проверяет возраст,
/// поэтому ответ «нужен возраст», «нужно согласие» или «нужен инвайт» на запрос без регистрации значит «игрок новый» —
/// приложение показывает форму регистрации и повторяет вход с тем же ID-токеном (Google выдаёт его на час).
public struct SignInService: Sendable {
    /// Версия соглашения, текст которого показывает приложение; сервер сверяет её со своей (`Auth:ConsentVersion`).
    public static let consentVersion = 1

    private let api: any APIProtocol
    private let tokens: TokenStore

    public init(api: any APIProtocol, tokens: TokenStore) {
        self.api = api
        self.tokens = tokens
    }

    /// Войти с ID-токеном Google; `registration` — для нового игрока, после ответа `.registrationNeeded`.
    public func signIn(idToken: String, registration: Registration? = nil) async -> SignInOutcome {
        let body = Components.Schemas.GoogleSignInRequest(
            idToken: idToken,
            inviteCode: registration.map { $0.inviteCode.trimmingCharacters(in: .whitespacesAndNewlines) },
            ageConfirmed: registration?.ageConfirmed ?? false,
            consentVersion: registration?.consentAccepted == true ? Int32(Self.consentVersion) : nil)
        // `sign_in_conflict`: два первых входа одновременно или совпавший ник — сервер откатил всё, повтор пройдёт.
        for attempt in 1...2 {
            let output: Operations.signInWithGoogle.Output
            do {
                output = try await api.signInWithGoogle(body: .json(body))
            } catch {
                return .failed(.offline)
            }
            switch output {
            case .ok(let response):
                guard let session = try? response.body.json else { return .failed(.unexpected(status: 200)) }
                await tokens.signIn(AuthTokens(session))
                return .signedIn(isNewUser: session.isNewUser)
            case .undocumented(let status, let payload):
                let code = await APITokenRefresher.problemCode(payload.body)
                if status == 409, code == "sign_in_conflict", attempt == 1 {
                    continue
                }
                return Self.outcome(status: status, code: code, registration: registration)
            }
        }
        return .failed(.unexpected(status: 409))
    }

    /// Выход: отозвать токены входа на сервере, затем стереть их с телефона. Без сети выход всё равно выполняется —
    /// refresh-токен на сервере истечёт сам (30 дней), а на телефоне его уже не будет.
    public func signOut() async {
        if let refreshToken = await tokens.current()?.refreshToken {
            _ = try? await api.logout(body: .json(.init(refreshToken: refreshToken)))
        }
        await tokens.signOut()
    }

    static func outcome(status: Int, code: String?, registration: Registration?) -> SignInOutcome {
        switch (status, code) {
        case (403, "age_confirmation_required"), (403, "consent_required"), (403, "invite_required"):
            guard let registration else { return .registrationNeeded }
            switch code {
            case "age_confirmation_required": return .failed(.ageNotConfirmed)
            case "consent_required": return .failed(registration.consentAccepted ? .consentOutdated : .consentRequired)
            default: return .failed(.inviteRequired)
            }
        case (403, "invite_invalid"):
            return .failed(.inviteInvalid)
        case (403, "account_deleting"):
            return .failed(.accountDeleting)
        case (401, _):
            return .failed(.googleRejected)
        case (503, "google_not_configured"):
            return .failed(.notConfigured)
        case (500...599, _), (429, _):
            return .failed(.offline)
        default:
            return .failed(.unexpected(status: status))
        }
    }
}
