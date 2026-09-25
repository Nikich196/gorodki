import Foundation
import Networking
import Observation

/// Шаги онбординга (PLAN.md, §3.18 и §5): интро → код приглашения → 16+ → соглашение и согласие → вход через Google.
/// «Дом» — позже, вместе с картой.
enum OnboardingStep: String, CaseIterable, Hashable, Sendable {
    case intro, invite, age, consent, signIn
}

/// Код приглашения, как его набрал игрок: без пробелов по краям и заглавными буквами — коды выдаются заглавными
/// (`XXXX-XXXX`), а сравнивать их с учётом регистра сервер не обязан уметь.
enum InviteCode {
    static func normalized(_ typed: String) -> String {
        typed.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
    }
}

/// Онбординг: что игрок ввёл и отметил, куда он дошёл и чем кончилась попытка входа. Простые значения — экран только
/// показывает их, а снимки (`-GorodkiScreen`) задают их напрямую.
@MainActor
@Observable
final class OnboardingModel {
    /// Вход в SignInService: ID-токен Google и регистрация нового игрока.
    typealias SignIn = @Sendable (_ idToken: String, _ registration: Registration?) async -> SignInOutcome
    /// Получить ID-токен Google (Google Sign-In на телефоне). `nil` — вход не настроен: нет Client ID (#4) или сервера.
    typealias GoogleToken = @Sendable () async throws -> String

    /// Шаги после интро — путь `NavigationStack`.
    var path: [OnboardingStep] = []
    var introPage = 0
    var inviteCode = ""
    var ageConfirmed = false
    var termsAccepted = false
    var consentGiven = false
    /// Игрок пришёл по «Уже играю — войти»: вход без регистрации.
    var returningPlayer = false
    var isSigningIn = false
    /// Текст ошибки входа для игрока (`SignInFailure.message`).
    var errorMessage: String? = nil

    private let signIn: SignIn?
    private let googleToken: GoogleToken?

    init(signIn: SignIn?, googleToken: GoogleToken?) {
        self.signIn = signIn
        self.googleToken = googleToken
    }

    /// Для запуска: вход — через `SignInService`, если задан адрес сервера. Google Sign-In ещё не подключён: нужен
    /// Client ID (#4) — до тех пор кнопка входа выключена и объясняет почему.
    static func live(_ dependencies: AppDependencies = .shared) -> OnboardingModel {
        let signIn = dependencies.signIn.map { service -> SignIn in
            { idToken, registration in await service.signIn(idToken: idToken, registration: registration) }
        }
        return OnboardingModel(signIn: signIn, googleToken: nil)
    }

    /// Можно ли нажать «Войти через Google».
    var signInAvailable: Bool {
        signIn != nil && googleToken != nil
    }

    var inviteReady: Bool {
        !InviteCode.normalized(inviteCode).isEmpty
    }

    /// Соглашение и согласие — две отдельные отметки (docs/legal/README.md); серверу уходит одна: обе обязательны.
    var consentReady: Bool {
        termsAccepted && consentGiven
    }

    /// Регистрация для `SignInService`; `nil` — вход без неё («Уже играю»).
    var registration: Registration? {
        guard !returningPlayer else { return nil }
        return Registration(
            inviteCode: InviteCode.normalized(inviteCode), ageConfirmed: ageConfirmed, consentAccepted: consentReady)
    }

    // MARK: - Переходы

    func startRegistration() {
        returningPlayer = false
        errorMessage = nil
        path = [.invite]
    }

    func startReturning() {
        returningPlayer = true
        errorMessage = nil
        path = [.signIn]
    }

    /// Следующий шаг после `step`; поле кода — сразу в том виде, в каком уйдёт на сервер.
    func advance(from step: OnboardingStep) {
        errorMessage = nil
        switch step {
        case .intro: startRegistration()
        case .invite:
            inviteCode = InviteCode.normalized(inviteCode)
            path.append(.age)
        case .age: path.append(.consent)
        case .consent: path.append(.signIn)
        case .signIn: break
        }
    }

    /// Войти через Google. Успех меняет вход в `TokenStore` — корень сам покажет вкладки (`AppSession`).
    func signInWithGoogle() async {
        guard let signIn, let googleToken, !isSigningIn else { return }
        isSigningIn = true
        errorMessage = nil
        defer { isSigningIn = false }
        let idToken: String
        do {
            idToken = try await googleToken()
        } catch {
            // Игрок закрыл окно Google или оно не открылось — не ошибка игры.
            return
        }
        apply(await signIn(idToken, registration))
    }

    func apply(_ outcome: SignInOutcome) {
        switch outcome {
        case .signedIn:
            errorMessage = nil
        case .registrationNeeded:
            // «Уже играю», а аккаунта нет — регистрация с начала: код, 16+, согласие.
            returningPlayer = false
            path = [.invite]
            errorMessage = "Аккаунта ещё нет — введи код приглашения."
        case .failed(let failure):
            errorMessage = failure.message
            if failure == .inviteInvalid || failure == .inviteRequired {
                path = [.invite]
            }
        }
    }
}
