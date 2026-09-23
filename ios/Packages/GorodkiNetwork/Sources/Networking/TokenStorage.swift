import Foundation
import Synchronization

/// Где токены лежат между запусками приложения. В приложении — Keychain (`KeychainTokenStorage`), в тестах — память.
public protocol TokenStorage: Sendable {
    /// Сохранённые данные; `nil` — ничего не сохранено. Ошибка значит «хранилище сейчас недоступно» (например, телефон
    /// после включения ещё ни разу не разблокирован), а не «вход не выполнен».
    func load() throws -> Data?
    /// Заменяет сохранённые данные.
    func save(_ data: Data) throws
    /// Стирает данные; если их нет — не ошибка.
    func delete() throws
}

/// Хранилище в памяти — для тестов и превью.
public final class InMemoryTokenStorage: TokenStorage {
    private let data: Mutex<Data?>

    public init(_ data: Data? = nil) {
        self.data = Mutex(data)
    }

    public func load() -> Data? { data.withLock { $0 } }

    public func save(_ data: Data) { self.data.withLock { $0 = data } }

    public func delete() { data.withLock { $0 = nil } }
}
