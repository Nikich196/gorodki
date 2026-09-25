import Networking
import Observation

/// Вошёл ли игрок — для корня приложения: не вошёл — онбординг, вошёл — вкладки (docs/architecture/ios-app.md,
/// «Оболочка и онбординг»). Обновляет `SessionRelay` — единственный подписчик событий входа (`TokenStore.events`).
@MainActor
@Observable
final class AppSession {
    static let shared = AppSession()

    enum Status: Equatable {
        /// Вход ещё не прочитан из Keychain — доли секунды после запуска.
        case unknown
        case signedOut
        case signedIn
    }

    private(set) var status: Status = .unknown
    /// Роль из access-токена (`player`, `demo`, `admin`); `nil` — не вошёл или токен без роли.
    private(set) var role: String? = nil
    /// Сборка команды разрешает посмотреть вкладки без входа (`DebugAccess.buildAllows`): вход через Google ждёт
    /// Client ID (#4), а пробную установку смотреть нужно уже сейчас. Не сохраняется — после перезапуска снова онбординг.
    var browsingWithoutSignIn = false

    /// Показать вкладки, а не онбординг.
    var showsShell: Bool {
        status == .signedIn || browsingWithoutSignIn
    }

    func update(_ tokens: AuthTokens?) {
        status = tokens == nil ? .signedOut : .signedIn
        role = tokens?.role
        if tokens != nil {
            browsingWithoutSignIn = false
        }
    }
}

/// Кому открыто отладочное меню («Лаборатория», «Проверка установки»; PLAN.md, §5: «только роли demo и admin»).
/// Сборки команды — Debug и бесплатная подпись (`FREE_SIGNING`: пробная установка через Sideloadly) — открывают его
/// всем: игроков в них нет, а проверки на телефоне нужны до входа. Сборка для TestFlight (Paid) — только ролям.
enum DebugAccess {
    static var buildAllows: Bool {
        #if DEBUG || FREE_SIGNING
            return true
        #else
            return false
        #endif
    }

    static func isAvailable(role: String?) -> Bool {
        buildAllows || role == "demo" || role == "admin"
    }
}
