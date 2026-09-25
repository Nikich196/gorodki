#if DEBUG
    import Foundation
    import GorodkiAPI
    import Networking
    import SwiftUI

    /// Экран режима фикстур (`LaunchOptions`): онбординг на нужном шаге, вкладка или отладочное меню — сразу,
    /// с данными из образцов `contracts/samples` (папка `samples` в ресурсах Debug-сборки).
    struct FixtureRoot: View {
        let screen: FixtureScreen
        @State private var onboarding: OnboardingModel
        @State private var shell: ShellModel

        init(screen: FixtureScreen, fixture: String?) {
            self.screen = screen
            _onboarding = State(initialValue: Fixtures.onboarding(screen, fixture: fixture))
            _shell = State(
                initialValue: ShellModel(tab: Fixtures.tab(screen), profile: Fixtures.profile(fixture)))
        }

        var body: some View {
            switch screen {
            case .intro, .invite, .age, .consent, .signIn:
                OnboardingView(model: onboarding, browseWithoutSignIn: { @MainActor in })
            case .map, .leaderboards, .clan, .profile:
                AppShell(model: shell)
            case .debug:
                NavigationStack {
                    DebugMenuView()
                }
            }
        }
    }

    /// Данные режима фикстур. Имена `-GorodkiFixture`:
    /// - `player` — вошедший игрок: `me.json`, `fog-summary.json`, `seasons.json`;
    /// - `google-ready` — вход через Google настроен (кнопка активна, но никуда не ходит);
    /// - `offline`, `invite-invalid`, `google-rejected`, `account-deleting` — ошибка входа с текстом `SignInFailure`.
    @MainActor
    enum Fixtures {
        static func onboarding(_ screen: FixtureScreen, fixture: String?) -> OnboardingModel {
            var signIn: OnboardingModel.SignIn?
            var googleToken: OnboardingModel.GoogleToken?
            if fixture == "google-ready" {
                signIn = { _, _ in .failed(.offline) }
                googleToken = { "fixture" }
            }
            let model = OnboardingModel(signIn: signIn, googleToken: googleToken)
            let steps: [OnboardingStep] = [.invite, .age, .consent, .signIn]
            switch screen {
            case .invite: model.path = Array(steps.prefix(1))
            case .age: model.path = Array(steps.prefix(2))
            case .consent: model.path = Array(steps.prefix(3))
            case .signIn: model.path = steps
            default: break
            }
            if screen != .invite || fixture != nil {
                model.inviteCode = "ABCD-2345"
            }
            model.ageConfirmed = screen == .consent || screen == .signIn
            model.termsAccepted = screen == .signIn
            model.consentGiven = screen == .signIn
            if let failure = fixture.flatMap(failure(named:)) {
                model.error = OnboardingModel.StepError(step: model.path.last ?? .intro, message: failure.message)
            }
            return model
        }

        static func failure(named name: String) -> SignInFailure? {
            switch name {
            case "offline": .offline
            case "invite-invalid": .inviteInvalid
            case "google-rejected": .googleRejected
            case "account-deleting": .accountDeleting
            default: nil
            }
        }

        static func tab(_ screen: FixtureScreen) -> AppTab {
            switch screen {
            case .leaderboards: .leaderboards
            case .clan: .clan
            case .profile: .profile
            default: .map
            }
        }

        static func profile(_ fixture: String?) -> ProfileModel {
            guard fixture == "player" else { return ProfileModel() }
            let profile = ProfileModel(signedIn: true)
            if let me = sample("me", as: Components.Schemas.MeResponse.self) {
                profile.apply(me)
            }
            let seasons = sample("seasons", as: Components.Schemas.SeasonsResponse.self)
            if let seasons {
                profile.apply(seasons)
            }
            if let summary = sample("fog-summary", as: Components.Schemas.FogSummaryResponse.self) {
                profile.apply(summary, currentSeason: seasons?.current.map(Int.init))
            }
            return profile
        }

        /// Образец ответа сервера из ресурсов (`samples/<имя>.json`); `nil` — нет файла или он не разобрался.
        static func sample<Value: Decodable>(_ name: String, as type: Value.Type, bundle: Bundle = .main) -> Value? {
            guard let url = bundle.url(forResource: name, withExtension: "json", subdirectory: "samples"),
                let data = try? Data(contentsOf: url)
            else { return nil }
            return try? JSONDecoder().decode(type, from: data)
        }
    }
#endif
