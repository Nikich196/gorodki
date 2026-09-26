#if DEBUG
    import Foundation
    import GorodkiAPI
    import Networking
    import SwiftUI

    /// Экран режима фикстур (`LaunchOptions`): онбординг на нужном шаге, вкладка или отладочное меню — сразу,
    /// с данными из образцов `contracts/samples` (папка `samples` в ресурсах Debug-сборки).
    struct FixtureRoot: View {
        let screen: FixtureScreen
        let fixture: String?
        @State private var onboarding: OnboardingModel
        @State private var shell: ShellModel

        init(screen: FixtureScreen, fixture: String?) {
            self.screen = screen
            _onboarding = State(initialValue: Fixtures.onboarding(screen, fixture: fixture))
            let profile =
                screen == .offline
                ? ProfileFixtures.offlineProfile() : Fixtures.profile(screen.isMap || screen.isRun ? "player" : fixture)
            _shell = State(
                initialValue: ShellModel(
                    tab: Fixtures.tab(screen), profile: profile, run: RunFixture.model(screen, profile: profile),
                    map: Fixtures.map(screen, fixture: fixture, profile: profile)))
            self.fixture = fixture
        }

        var body: some View {
            switch screen {
            case .intro, .invite, .age, .terms, .consent, .signIn:
                OnboardingView(model: onboarding, browseWithoutSignIn: { @MainActor in })
            case .map, .mapParcel, .mapExplore, .leaderboards, .clan, .profile, .hud, .hudCeremony, .hudCollapsed,
                .runResult, .offline, .mapHome:
                AppShell(model: shell)
            case .settings, .privacyZones, .home, .explorationStats:
                ProfileFixtureScreen(screen: screen, fixture: fixture)
            case .runDetails:
                NavigationStack {
                    RunResultView(model: RunFixture.details())
                }
            case .runHistory:
                NavigationStack {
                    RunHistoryView(model: RunFixture.history())
                }
            case .debug:
                NavigationStack {
                    DebugMenuView()
                }
            }
        }
    }

    /// Данные режима фикстур. Имена `-GorodkiFixture`:
    /// - `player` — вошедший игрок: `me.json`, `fog-summary.json`, `seasons.json`;
    /// - `player-map` — он же и карта с землёй и туманом (`MapFixture`); экраны `map-parcel` и `map-explore`
    ///   берут её сами;
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
            let steps: [OnboardingStep] = [.invite, .age, .terms, .consent, .signIn]
            switch screen {
            case .invite: model.path = Array(steps.prefix(1))
            case .age: model.path = Array(steps.prefix(2))
            case .terms: model.path = Array(steps.prefix(3))
            case .consent: model.path = Array(steps.prefix(4))
            case .signIn: model.path = steps
            default: break
            }
            if screen != .invite || fixture != nil {
                model.inviteCode = "ABCD-2345"
            }
            model.ageConfirmed = [.terms, .consent, .signIn].contains(screen)
            model.termsAccepted = screen == .consent || screen == .signIn
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
            case .profile, .offline: .profile
            default: .map
            }
        }

        /// Карта: с землёй и туманом — для `player-map` и экранов карты, иначе пустая.
        static func map(_ screen: FixtureScreen, fixture: String?, profile: ProfileModel) -> MapModel {
            guard screen.isMap || fixture == "player-map" else { return MapModel(profile: profile) }
            let model = MapModel(
                profile: profile, names: FixturePlayerNames(),
                initialWindow: MapFixture.window,
                clock: { [now = MapFixture.nowMs] in Date(timeIntervalSince1970: Double(now) / 1_000) })
            model.apply(land: MapFixture.land())
            model.apply(fog: MapFixture.fog())
            switch screen {
            case .mapParcel:
                model.select(at: MapFixture.parcelTap, tolerance: 5)
            case .mapExplore:
                model.layer = .explore
            case .mapHome:
                model.home = HomeModel(home: ProfileFixtures.home)
                model.layer = .explore
            default:
                break
            }
            return model
        }

        static func profile(_ fixture: String?) -> ProfileModel {
            guard fixture == "player" || fixture == "player-map" else { return ProfileModel() }
            let profile = ProfileModel(signedIn: true)
            if let me = sample("me", as: Components.Schemas.MeResponse.self) {
                profile.apply(me)
            }
            let seasons = sample("seasons", as: Components.Schemas.SeasonsResponse.self)
            if let seasons {
                profile.apply(seasons)
            }
            if let summary = sample("fog-summary", as: Components.Schemas.FogSummaryResponse.self) {
                profile.apply(summary, currentSeason: seasons?.current.map(Int.init), seasons: seasons)
            }
            if let seasons {
                // «Сейчас» — десятый день сезона образца: карточка сезона с «день 10 из 14».
                profile.apply(seasons, nowMs: 1_794_776_400_000 + 9 * 86_400_000)
            }
            if let stats = sample("me-stats", as: Components.Schemas.MyStatsResponse.self) {
                profile.apply(stats)
            }
            return profile
        }

        /// Образец ответа сервера из ресурсов (`samples/<имя>.json`); `nil` — нет файла или он не разобрался.
        nonisolated static func sample<Value: Decodable>(_ name: String, as type: Value.Type, bundle: Bundle = .main)
            -> Value?
        {
            guard let url = bundle.url(forResource: name, withExtension: "json", subdirectory: "samples"),
                let data = try? Data(contentsOf: url)
            else { return nil }
            return try? JSONDecoder().decode(type, from: data)
        }
    }
#endif
