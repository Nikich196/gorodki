import SwiftUI

/// Точка входа приложения «Городки».
@main
struct GorodkiApp: App {
    @UIApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        WindowGroup {
            RootView()
        }
    }
}
