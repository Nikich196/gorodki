import UIKit

/// Делегат приложения — ради того, что нужно сделать при запуске, до того как пользователь что-то откроет:
/// - если iOS перезапустила приложение в фоне во время забега или прогулки «Лаборатории», продолжить запись — сразу,
///   иначе iOS может снова усыпить приложение (PLAN.md, §7.2 «Трекинг»);
/// - зарегистрировать фоновые задачи досылки очереди (`BackgroundSync`): позже iOS этого не позволит;
/// - начать слушать сеть, вход и подсказки сервера. Не из экрана: при перезапуске в фоне (геопозиция забега, фоновая
///   задача) сцена может и не подключиться, и тогда очередь не уходила бы сразу по возвращении сети, а смена входа
///   не закрывала бы соединение прежнего игрока.
final class AppDelegate: NSObject, UIApplicationDelegate {
    func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil
    ) -> Bool {
        RunController.shared.resumeAtLaunch()
        WalkLab.shared.resumeIfNeeded()
        BackgroundSync.register()
        // Слушатели сами ничего не открывают: соединение реального времени — только на переднем плане (`GorodkiApp`).
        NetworkWatcher.shared.start()
        SessionRelay.shared.start()
        RealtimeRelay.shared.start()
        return true
    }
}
