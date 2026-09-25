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
    /// Сообщение, которое корень показывает поверх любого экрана: например, выход стёр вход, но не все данные игрока.
    var notice: String? = nil

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
/// Сборки команды открывают его всем: Debug (Xcode, снимки) и пробная сборка IPA (`GORODKI_PROBE`: ipa.yml, запуск
/// с галочкой «probe») — проверки на телефоне нужны до входа. IPA для игроков собирается без флага: Release-Free,
/// который ставят через Sideloadly, и Release-Paid открывают меню только ролям.
enum DebugAccess {
    static var buildAllows: Bool {
        #if DEBUG || GORODKI_PROBE
            return true
        #else
            return false
        #endif
    }

    static func isAvailable(role: String?) -> Bool {
        isAvailable(role: role, buildAllows: buildAllows)
    }

    static func isAvailable(role: String?, buildAllows: Bool) -> Bool {
        buildAllows || role == "demo" || role == "admin"
    }
}
