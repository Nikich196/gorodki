import Foundation
import GameCore
import GorodkiAPI
import OpenAPIRuntime

/// Версия игрового конфига и числа, нужные телефону (`PhoneRules`).
public struct RulesVersion: Hashable, Sendable {
    public var version: Int
    /// С какого момента версия действует на сервере, мс Unix.
    public var activeFromMs: Int64
    public var rules: PhoneRules

    public init(version: Int, activeFromMs: Int64, rules: PhoneRules) {
        self.version = version
        self.activeFromMs = activeFromMs
        self.rules = rules
    }

    /// С чем собрано приложение: версия 1.
    public static let bundled = RulesVersion(version: 1, activeFromMs: 0, rules: .version1)
}

/// Где хранится последний полученный конфиг (ответ `GET /config` как есть): в приложении — файл, в тестах — память.
public protocol RulesStorage: Sendable {
    func load() throws -> Data?
    func save(_ data: Data) throws
}

public final class InMemoryRulesStorage: RulesStorage, @unchecked Sendable {
    private let lock = NSLock()
    private var data: Data?

    public init(_ data: Data? = nil) { self.data = data }

    public func load() -> Data? { lock.withLock { data } }

    public func save(_ data: Data) { lock.withLock { self.data = data } }
}

/// Файл в каталоге приложения: конфиг переживает перезапуск, и забег без сети начинается с последней известной версией.
public struct FileRulesStorage: RulesStorage {
    public let url: URL

    public init(url: URL) { self.url = url }

    public func load() throws -> Data? {
        FileManager.default.fileExists(atPath: url.path) ? try Data(contentsOf: url) : nil
    }

    public func save(_ data: Data) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try data.write(to: url, options: .atomic)
    }
}

/// Правила для нового забега (PLAN.md, §7.2; `GET /config` «берётся при каждом „Старте“»).
///
/// Телефон никогда не ждёт сервер: «Старт» берёт последнюю известную версию сразу (`current`), а свежую приложение
/// запрашивает в фоне (`refresh`) — при запуске и выходе на передний план. Сервер принимает забег с версией, действовавшей
/// при его начале или сменённой не больше недели назад (`RunLimits.ConfigGrace`), поэтому версия недельной давности ещё
/// годится. Испорченный файл — как его отсутствие: действует версия, с которой собрано приложение.
public actor RulesStore {
    private let api: (any APIProtocol)?
    private let storage: any RulesStorage
    private var cached: RulesVersion?

    public init(api: (any APIProtocol)?, storage: any RulesStorage) {
        self.api = api
        self.storage = storage
    }

    /// Последняя известная версия.
    public func current() -> RulesVersion {
        if let cached { return cached }
        let loaded = (try? storage.load()).flatMap { try? Self.decode($0) } ?? .bundled
        cached = loaded
        return loaded
    }

    /// Запросить действующую версию у сервера и запомнить.
    /// - Throws: ошибку сети или сервера; прежняя версия остаётся.
    @discardableResult
    public func refresh() async throws -> RulesVersion {
        guard let api else { return current() }
        let output = try await api.getConfig()
        guard case .ok(let ok) = output else {
            throw RulesStoreError.unexpectedResponse
        }
        // Типы клиента → JSON → `PhoneRules`: один разбор и для файла, и для ответа сервера.
        let data = try JSONEncoder().encode(try ok.body.json)
        let fresh = try Self.decode(data)
        try? storage.save(data)
        cached = fresh
        return fresh
    }

    private struct Envelope: Decodable {
        var version: Int
        var activeFromMs: Int64
        var rules: PhoneRules
    }

    static func decode(_ data: Data) throws -> RulesVersion {
        let envelope = try JSONDecoder().decode(Envelope.self, from: data)
        return RulesVersion(version: envelope.version, activeFromMs: envelope.activeFromMs, rules: envelope.rules)
    }
}

public enum RulesStoreError: Error, Equatable {
    /// Сервер ответил не так, как описано в контракте.
    case unexpectedResponse
}
