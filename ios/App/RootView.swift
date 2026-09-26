import DesignSystem
import SwiftUI

/// Корень приложения: не вошёл — онбординг, вошёл — вкладки (docs/architecture/ios-app.md, «Оболочка и онбординг»).
/// В Debug аргументы `-GorodkiScreen` / `-GorodkiFixture` / `-GorodkiTheme` открывают экран сразу — для снимков.
struct RootView: View {
    var body: some View {
        // Тема — только если её задал `-GorodkiTheme`: `.preferredColorScheme(nil)` у корня перекрыл бы вложенный
        // выбор темы (переключатель «День | Ночь» в «Лаборатории → Дизайн»).
        if let scheme = LaunchOptions.current.colorScheme {
            content.preferredColorScheme(scheme)
        } else {
            content
        }
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
    @State private var shell = ShellModel.live()
    @State private var noticeShown = false

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
                        browseWithoutSignIn: DebugAccess.buildAllows
                            ? { @MainActor in AppSession.shared.browsingWithoutSignIn = true } : nil)
                }
            }
        }
        .task(id: session.status) {
            if session.status == .signedOut {
                // Вышел — онбординг с начала, профиль прежнего игрока забыт.
                onboarding = OnboardingModel.live()
                shell = ShellModel.live()
            }
            shell.profile.signedIn = session.status == .signedIn
            shell.profile.role = session.role
            shell.profile.api = session.status == .signedIn ? AppDependencies.shared.api : nil
            await shell.profile.refresh()
        }
        .onChange(of: session.notice) { _, notice in
            noticeShown = notice != nil
        }
        .alert("Выход", isPresented: $noticeShown, presenting: session.notice) { _ in
            Button("Понятно", role: .cancel) { session.notice = nil }
        } message: { notice in
            Text(notice)
        }
    }
}
