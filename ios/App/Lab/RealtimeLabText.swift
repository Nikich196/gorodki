import GameCore
import Realtime

/// Строки раздела «Реальное время» в «Лаборатории → Сервер» (спайк S7) — отдельно от экрана, чтобы их проверяли тесты
/// приложения.
struct RealtimeLabText: Equatable {
    var connection: String
    var reconnects: String
    var lastHint: String

    init(connection: String, reconnects: String, lastHint: String) {
        self.connection = connection
        self.reconnects = reconnects
        self.lastHint = lastHint
    }

    /// - Parameters:
    ///   - diagnostics: `nil` — адрес сервера не задан, соединения нет вовсе.
    ///   - signedIn: есть ли вход сейчас. Без него соединение и не пытается подключиться — «нужен вход», а не
    ///     «остановлено»: иначе на прогулке не понять, сломалось ли что-то.
    init(diagnostics: RealtimeClient.Diagnostics?, signedIn: Bool) {
        guard let diagnostics else {
            self.init(connection: "адрес не задан", reconnects: "—", lastHint: "—")
            return
        }
        let connection: String
        if !signedIn || diagnostics.needsSignIn {
            connection = "нужен вход"
        } else {
            switch diagnostics.state {
            case .stopped:
                connection = "остановлено"
            case .connecting:
                connection = "подключается…"
            case .connected:
                connection = "на связи"
            case .waiting(let delay):
                // Целые секунды паузы; «4 с» — через NumberText, как остальные числа: пробел неразрывный.
                connection =
                    "ждёт повтора (" + NumberText.seconds(Double(delay.components.seconds), fractionDigits: 0) + ")"
            }
        }
        self.init(
            connection: connection, reconnects: "\(diagnostics.reconnects)",
            lastHint: diagnostics.secondsSinceLastHint.map { NumberText.seconds($0, fractionDigits: 0) + " назад" }
                ?? "ещё не было")
    }
}
