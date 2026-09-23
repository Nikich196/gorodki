import Foundation

/// Адрес сервера API из настроек сборки: ключ Info.plist `GorodkiServerURL` ← переменная `GORODKI_SERVER_URL`
/// (ios/Config/Shared.xcconfig, свой — в Local.xcconfig). Пока сервер не развёрнут, адрес пуст — это не ошибка:
/// приложение работает без сети, очередь синхронизации ждёт.
public enum ServerURL {
    public static let infoPlistKey = "GorodkiServerURL"

    /// Адрес или `nil`, если он не задан или задан неверно. Нужны схема `https` (или `http` — для своего сервера на
    /// компьютере) и хост; логин и пароль в адресе, параметры и `#` не допускаются. «/» на конце убирается: пути API
    /// начинаются с «/».
    public static func parse(_ value: String?) -> URL? {
        guard let text = value?.trimmingCharacters(in: .whitespacesAndNewlines), !text.isEmpty,
            var components = URLComponents(string: text),
            let scheme = components.scheme?.lowercased(), scheme == "https" || scheme == "http",
            let host = components.host, !host.isEmpty,
            components.user == nil, components.password == nil,
            components.query == nil, components.fragment == nil
        else { return nil }
        while components.path.hasSuffix("/") {
            components.path.removeLast()
        }
        return components.url
    }
}
