import Foundation
import Testing

@testable import Networking

@Suite("Идентификатор установки")
struct InstallationIDTests {
    @Test("Один и тот же — и в этом запуске, и после перезапуска (то же хранилище)")
    func stable() async {
        let storage = InMemoryTokenStorage()
        let first = await InstallationID(storage: storage).value()

        let relaunched = InstallationID(storage: storage)
        #expect(await relaunched.value() == first)
        #expect(await relaunched.value() == first)
    }

    @Test("Разные установки (разные хранилища) — разные идентификаторы")
    func distinct() async {
        let a = await InstallationID(storage: InMemoryTokenStorage()).value()
        let b = await InstallationID(storage: InMemoryTokenStorage()).value()
        #expect(a != b)
    }

    @Test("Keychain недоступен — временный на время процесса, без записи; доступен — записывается тот же")
    func unavailableKeychain() async throws {
        let storage = FlakyStorage()
        let installation = InstallationID(storage: storage)
        storage.setAvailable(false)

        let temporary = await installation.value()
        #expect(await installation.value() == temporary)
        #expect(storage.stored == nil)

        storage.setAvailable(true)
        #expect(await installation.value() == temporary)
        #expect(storage.stored.flatMap { String(data: $0, encoding: .utf8) } == temporary.uuidString)
    }

    @Test("Запись есть, но Keychain недоступен — новая не создаётся и не записывается поверх")
    func existingIsNotOverwritten() async throws {
        let storage = FlakyStorage()
        let original = UUID()
        try storage.save(Data(original.uuidString.utf8))
        storage.setAvailable(false)
        let installation = InstallationID(storage: storage)

        _ = await installation.value()
        storage.setAvailable(true)

        #expect(await installation.value() == original)
    }

    @Test("Прочитать не удалось, а записать можно — поверх ничего не пишется: запись могла быть")
    func readFailureNeverOverwrites() async {
        let storage = ReadFailingStorage()
        let installation = InstallationID(storage: storage)

        let temporary = await installation.value()

        #expect(storage.saves == 0)
        #expect(await installation.value() == temporary)
    }

    @Test("Испорченная запись заменяется новой")
    func corruptedIsReplaced() async {
        let storage = InMemoryTokenStorage(Data("не uuid".utf8))
        let id = await InstallationID(storage: storage).value()
        #expect(storage.load().flatMap { String(data: $0, encoding: .utf8) } == id.uuidString)
    }
}

/// Хранилище, которое не читается (временная ошибка), но пишется.
final class ReadFailingStorage: TokenStorage, @unchecked Sendable {
    struct ReadError: Error {}

    private let lock = NSLock()
    private var count = 0

    var saves: Int { lock.withLock { count } }

    func load() throws -> Data? { throw ReadError() }

    func save(_ data: Data) throws { lock.withLock { count += 1 } }

    func delete() throws {}
}
