import Foundation

/// Своя Live Activity среди нескольких. У забега, прогулки «Лаборатории» и проверки установки один тип атрибутов
/// (`RunActivityAttributes`), поэтому после перезапуска свою можно узнать только по идентификатору, сохранённому при
/// запуске. «Первая попавшаяся» могла оказаться чужой: восстановление забега закрывало плашку идущей прогулки, а
/// «Закончить» в проверке установки — плашку забега.
struct LiveActivitySlot {
    /// Ключ в UserDefaults — свой у каждого владельца.
    let key: String
    var defaults: UserDefaults = .standard

    /// Сохранённый идентификатор — возможно, уже закрытой активности.
    var saved: String? { defaults.string(forKey: key) }

    /// Своя активность, если она ещё идёт; закрытую системой или игроком — `nil`.
    /// - Parameter running: идентификаторы идущих активностей (`RunActivityController.runningIDs`).
    func adopt(running: [String]) -> String? {
        guard let saved, running.contains(saved) else { return nil }
        return saved
    }

    /// Запомнить запущенную; `nil` (запустить не удалось) — забыть прежнюю.
    func remember(_ id: String?) {
        defaults.set(id, forKey: key)
    }

    func forget() {
        defaults.removeObject(forKey: key)
    }

    /// Забыть закрытую — если в слоте всё ещё она. Забывают после закрытия, а пока плашка закрывалась, владелец мог
    /// запустить и запомнить новую: её идентификатор стирать нельзя.
    func forget(_ id: String) {
        guard saved == id else { return }
        forget()
    }
}
