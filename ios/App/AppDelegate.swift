import UIKit

/// Делегат приложения нужен ради одного: если iOS перезапустила приложение в фоне во время прогулки,
/// запись нужно продолжить сразу при запуске — до того как пользователь что-то откроет (PLAN.md, §7.2 «Трекинг»).
final class AppDelegate: NSObject, UIApplicationDelegate {
    func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil
    ) -> Bool {
        WalkLab.shared.resumeIfNeeded()
        return true
    }
}
