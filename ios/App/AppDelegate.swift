import UIKit

/// Делегат приложения — ради того, что нужно сделать при запуске, до того как пользователь что-то откроет:
/// - если iOS перезапустила приложение в фоне во время прогулки, продолжить запись (PLAN.md, §7.2 «Трекинг»);
/// - зарегистрировать фоновые задачи досылки очереди (`BackgroundSync`): позже iOS этого не позволит.
final class AppDelegate: NSObject, UIApplicationDelegate {
    func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil
    ) -> Bool {
        WalkLab.shared.resumeIfNeeded()
        BackgroundSync.register()
        return true
    }
}
