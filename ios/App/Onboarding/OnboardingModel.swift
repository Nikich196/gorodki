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
    /// Ошибка входа для игрока (`SignInFailure.message`) и шаг, на котором её показать.
    var error: StepError? = nil

    /// Ошибка и шаг, к которому она относится: вернулся игрок назад — чужая ошибка на другом шаге не видна.
    struct StepError: Equatable {
        var step: OnboardingStep
        var message: String
    }

    private let signIn: SignIn?
    private let googleToken: GoogleToken?
    /// ID-токен Google из входа, после которого сервер попросил регистрацию (`registrationNeeded`): после кода, 16+ и
    /// согласия вход повторяется с ним же, без второго окна Google (токен живёт час; истёк — `googleRejected`).
    private var pendingIdToken: String?

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

    /// Текст ошибки для шага `step`; `nil` — на этом шаге ошибки нет.
    func errorMessage(on step: OnboardingStep) -> String? {
        error?.step == step ? error?.message : nil
    }

    // MARK: - Переходы

    func startRegistration() {
        returningPlayer = false
        error = nil
        path = [.invite]
    }

    func startReturning() {
        returningPlayer = true
        error = nil
        pendingIdToken = nil  // «Уже играю» — можно выбрать другой аккаунт Google
        path = [.signIn]
    }

    /// Следующий шаг после `step`; поле кода — сразу в том виде, в каком уйдёт на сервер.
    func advance(from step: OnboardingStep) {
        error = nil
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
        error = nil
        defer { isSigningIn = false }
        let idToken: String
        if let pendingIdToken {
            idToken = pendingIdToken
        } else {
            do {
                idToken = try await googleToken()
            } catch {
                // Игрок закрыл окно Google или оно не открылось — не ошибка игры.
                return
            }
        }
        apply(await signIn(idToken, registration), idToken: idToken)
    }

    /// Итог входа: куда вернуть игрока и какую ошибку показать. `idToken` — с каким токеном входили.
    func apply(_ outcome: SignInOutcome, idToken: String? = nil) {
        switch outcome {
        case .signedIn:
            error = nil
            pendingIdToken = nil
        case .registrationNeeded:
            // «Уже играю», а аккаунта нет — регистрация с начала: код, 16+, согласие; вход потом — с тем же токеном.
            pendingIdToken = idToken
            returningPlayer = false
            path = [.invite]
            error = StepError(step: .invite, message: "Аккаунта ещё нет — введи код приглашения.")
        case .failed(let failure):
            if failure == .googleRejected || failure == .accountDeleting {
                // Токен истёк или этим аккаунтом не войти — следующий вход откроет окно Google заново.
                pendingIdToken = nil
            }
            let steps: [OnboardingStep] = [.invite, .age, .consent, .signIn]
            let step: OnboardingStep =
                switch failure {
                case .inviteInvalid, .inviteRequired: .invite
                case .ageNotConfirmed: .age
                case .consentRequired: .consent
                default: .signIn
                }
            if step != .signIn {
                path = Array(steps.prefix(through: steps.firstIndex(of: step) ?? 0))
            }
            error = StepError(step: step, message: failure.message)
        }
    }
}
