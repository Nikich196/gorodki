import Foundation
import Networking

/// Что сквозному прогону передаёт окружение (готовит .github/workflows/e2e.yml, вручную — docs/guides/e2e.md).
struct E2EEnvironment {
    var server: URL
    /// JWT игрока A — он бежит.
    var token: String
    /// JWT игрока B — он смотрит со стороны.
    var otherToken: String

    private static var variables: [String: String] { ProcessInfo.processInfo.environment }

    /// Прогон нужен: задан сервер или задача потребовала прогон (`GORODKI_E2E_REQUIRED=1`). Во втором случае потерянная
    /// переменная (опечатка в workflow) роняет тест, а не пропускает его: иначе задача была бы зелёной и ничего
    /// не проверила бы.
    static var isRequested: Bool {
        variables["GORODKI_E2E_SERVER"] != nil || variables["GORODKI_E2E_REQUIRED"] == "1"
    }

    static func load() throws -> E2EEnvironment {
        func value(_ name: String) throws -> String {
            guard let value = variables[name], !value.isEmpty else { throw E2EError.missing(name) }
            return value
        }
        let address = try value("GORODKI_E2E_SERVER")
        guard let server = ServerURL.parse(address) else { throw E2EError.invalid("GORODKI_E2E_SERVER", address) }
        return E2EEnvironment(
            server: server, token: try value("GORODKI_E2E_TOKEN"), otherToken: try value("GORODKI_E2E_OTHER_TOKEN"))
    }
}

enum E2EError: Error, CustomStringConvertible {
    case missing(String)
    case invalid(String, String)
    case timedOut(String, last: String)
    case unexpected(String)

    var description: String {
        switch self {
        case .missing(let name): "нет \(name) — см. docs/guides/e2e.md"
        case .invalid(let name, let value): "\(name) = «\(value)» — не адрес сервера"
        case .timedOut(let what, let last): "не дождались: \(what); последнее наблюдение: \(last)"
        case .unexpected(let what): what
        }
    }
}

/// Ждать, пока `probe` не вернёт значение. Сервер решает заявки и туман в фоне (обработчик раз в 5 с и по сигналу),
/// поэтому проверки после синхронизации — с ожиданием. По сроку — ошибка с последним наблюдением: причина видна в журнале.
func eventually<T>(
    _ what: String, timeout: Duration = .seconds(120), every: Duration = .seconds(2),
    _ probe: () async throws -> (value: T?, observation: String)
) async throws -> T {
    let clock = ContinuousClock()
    let deadline = clock.now + timeout
    var last = "—"
    while true {
        let (value, observation) = try await probe()
        if let value { return value }
        last = observation
        guard clock.now < deadline else { throw E2EError.timedOut(what, last: last) }
        try await Task.sleep(for: every)
    }
}
