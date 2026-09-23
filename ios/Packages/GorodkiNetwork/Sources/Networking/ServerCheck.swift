import Foundation
import GorodkiAPI
import OpenAPIRuntime

/// Проверка связи с сервером (`GET /health`) для «Лаборатории»: тот же клиент, транспорт и подпись запросов, что
/// у синхронизации. Текст ошибки — без запроса и заголовков: снимок экрана можно смело прислать в чат.
public enum ServerCheck: Equatable, Sendable {
    /// Сервер ответил: его версия и игровой день по Минску.
    case online(version: String, gameDay: String)
    /// Сервер ответил кодом, которого нет в контракте.
    case unexpectedStatus(Int)
    /// Ответа нет: сеть, адрес, тайм-аут.
    case unreachable(String)

    public static func run(_ api: any APIProtocol) async -> ServerCheck {
        do {
            switch try await api.getHealth() {
            case .ok(let response):
                let health = try response.body.json
                return .online(version: health.version, gameDay: health.gameDay)
            case .undocumented(let status, _):
                return .unexpectedStatus(status)
            }
        } catch let error as ClientError {
            return .unreachable(error.underlyingError.localizedDescription)
        } catch {
            return .unreachable(error.localizedDescription)
        }
    }
}
