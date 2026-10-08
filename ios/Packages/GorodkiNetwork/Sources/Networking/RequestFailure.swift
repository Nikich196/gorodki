import Foundation
import OpenAPIRuntime

/// Почему запрос экрана не удался — одним словом для игрока (PLAN.md, §5, экран 27 «Пустые, офлайн и ошибочные
/// состояния»): нет сети, сервер недоступен, сервер отказал по существу. Экран показывает `title` и `message` в общем
/// компоненте состояний (`ContentStateView` дизайн-системы), а не текст ошибки Swift.
public enum RequestFailure: Error, Equatable, Sendable {
    /// Адрес сервера в этой сборке не задан — запрашивать некого.
    case notConfigured
    /// Нет сети: телефон не в интернете или связь оборвалась.
    case offline
    /// Сервер не ответил или шлюз не достучался (502, 503, 504): бесплатный сервер просыпается до минуты.
    case serverUnavailable
    /// Сервер ответил ошибкой (500, 501) — ничего не изменилось.
    case serverError(status: Int)
    /// Вход истёк, и обновить его не вышло (401 после обновления).
    case signedOut
    /// Аккаунта на сервере нет (404) — например, он уже удаляется.
    case notFound
    /// Сервер отказал по существу: код из `application/problem+json`.
    case rejected(status: Int, code: String?)
    /// Ответ не по контракту.
    case unexpected(status: Int?)

    /// Ошибка запроса → что сказать игроку. Ошибки транспорта клиент API заворачивает в `ClientError` — смотрим внутрь.
    public init(_ error: any Error) {
        switch error {
        case let failure as RequestFailure:
            self = failure
        case let account as AccountServiceError:
            switch account {
            case .notFound: self = .notFound
            case .rejected(let status, let code): self = Self.of(status: status, code: code)
            case .unexpectedStatus(let status): self = Self.of(status: status, code: nil)
            }
        case let client as ClientError:
            if let status = client.response?.status.code {
                self = Self.of(status: status, code: nil)
            } else {
                self = RequestFailure(client.underlyingError)
            }
        case let url as URLError:
            self = Self.of(url.code)
        case is DecodingError:
            self = .unexpected(status: nil)
        default:
            // Как у входа (`SignInFailure.offline`): прочие ошибки транспорта — «нет связи».
            self = .offline
        }
    }

    /// Ответ сервера со статусом `status` → причина.
    static func of(status: Int, code: String?) -> RequestFailure {
        switch status {
        case 401: .signedOut
        case 404: .notFound
        case 502, 503, 504: code == nil ? .serverUnavailable : .rejected(status: status, code: code)
        case 500...599: .serverError(status: status)
        case 400...499: .rejected(status: status, code: code)
        default: .unexpected(status: status)
        }
    }

    /// Ошибка URLSession → нет сети или сервер недоступен.
    static func of(_ code: URLError.Code) -> RequestFailure {
        switch code {
        case .timedOut, .cannotFindHost, .cannotConnectToHost, .dnsLookupFailed, .badServerResponse,
            .secureConnectionFailed:
            .serverUnavailable
        default:
            .offline
        }
    }

    /// Заголовок состояния: «Нет сети», «Сервер недоступен».
    public var title: String {
        switch self {
        case .notConfigured: "Сервер не настроен"
        case .offline: "Нет сети"
        case .serverUnavailable: "Сервер недоступен"
        case .serverError: "Сервер не справился"
        case .signedOut: "Нужно войти снова"
        case .notFound: "Аккаунта нет"
        case .rejected: "Не получилось"
        case .unexpected: "Что-то пошло не так"
        }
    }

    /// Что случилось и что сделать — одной-двумя фразами.
    public var message: String {
        switch self {
        case .notConfigured:
            "В этой сборке не задан адрес сервера — данные появятся в сборке с сервером."
        case .offline:
            "Проверь интернет и попробуй ещё раз."
        case .serverUnavailable:
            "Сервер не отвечает. Бесплатный сервер просыпается до минуты — попробуй ещё раз."
        case .serverError(let status):
            "Сервер ответил ошибкой (\(status)), ничего не изменилось. Попробуй позже."
        case .signedOut:
            "Вход истёк. Выйди и войди снова."
        case .notFound:
            "Сервер не нашёл аккаунт — возможно, он уже удаляется."
        case .rejected(let status, let code):
            Self.rejection(status: status, code: code)
        case .unexpected(let status):
            status.map { "Сервер ответил неожиданно (\($0)). Попробуй позже." }
                ?? "Сервер ответил не так, как ждёт приложение. Попробуй позже."
        }
    }

    /// Отказы, которые экран знает по коду (docs/architecture/*.md); остальные — с номером ответа.
    private static func rejection(status: Int, code: String?) -> String {
        switch code {
        case "zone_limit": "Больше приватных зон нельзя — сначала убери одну из прежних."
        case "zone_invalid": "Эта точка не подходит для зоны — выбери место в городе."
        case "fog_clear_busy":
            "Туман сейчас занят — забег открывает его или считается рейтинг. Ничего не стёрто, "
                + "попробуй через минуту."
        default: "Сервер отказал (\(status)\(code.map { ", \($0)" } ?? "")). Попробуй позже."
        }
    }

    /// Нет смысла повторять тот же запрос сразу: сервера нет или отказ по существу.
    public var isRetryable: Bool {
        switch self {
        case .offline, .serverUnavailable, .serverError, .unexpected: true
        case .rejected(_, let code): code == "fog_clear_busy"
        case .notConfigured, .signedOut, .notFound: false
        }
    }
}
