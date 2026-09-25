import DesignSystem
import SwiftUI

/// Корень приложения: не вошёл — онбординг, вошёл — вкладки (docs/architecture/ios-app.md, «Оболочка и онбординг»).
/// В Debug аргументы `-GorodkiScreen` / `-GorodkiFixture` / `-GorodkiTheme` открывают экран сразу — для снимков.
struct RootView: View {
    var body: some View {
        content
            .preferredColorScheme(LaunchOptions.current.colorScheme)
    }

    @ViewBuilder
    private var content: some View {
        #if DEBUG
            if let screen = LaunchOptions.current.screen {
                FixtureRoot(screen: screen, fixture: LaunchOptions.current.fixture)
            } else {
                LiveRoot()
            }
        #else
            LiveRoot()
        #endif
    }
}

/// Корень с настоящим входом: `AppSession` обновляет `SessionRelay`.
private struct LiveRoot: View {
    private let session = AppSession.shared
    @State private var onboarding = OnboardingModel.live()
    @State private var shell = ShellModel(profile: ProfileModel())

    var body: some View {
        Group {
            switch session.status {
            case .unknown:
                Palette.uiBackground.color.ignoresSafeArea()
            case .signedOut, .signedIn:
                if session.showsShell {
                    AppShell(model: shell)
                } else {
                    OnboardingView(
                        model: onboarding,
                        browseWithoutSignIn: DebugAccess.buildAllows ? { session.browsingWithoutSignIn = true } : nil)
                }
            }
        }
        .task(id: session.status) {
            if session.status == .signedOut {
                // Вышел — онбординг с начала, профиль прежнего игрока забыт.
                onboarding = OnboardingModel.live()
                shell = ShellModel(profile: ProfileModel())
            }
            shell.profile.signedIn = session.status == .signedIn
            shell.profile.role = session.role
            guard session.status == .signedIn, let api = AppDependencies.shared.api else { return }
            await shell.profile.load(api: api)
        }
    }
}
