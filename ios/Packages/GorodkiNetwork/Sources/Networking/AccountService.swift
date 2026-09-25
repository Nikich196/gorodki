import Foundation
import GorodkiAPI
import OpenAPIRuntime

/// Аккаунт на сервере: зоны приватности (`/me/privacy-zones`), удаление аккаунта (`DELETE /me`) и «мои данные»
/// (`GET /me/export`). Только запросы — экраны этапа 2 зовут их через `AppDependencies`; стирание данных на телефоне
/// после удаления — там же (`wipeLocalData`).
public struct AccountService: Sendable {
    public typealias PrivacyZone = Components.Schemas.PrivacyZoneResponse
    public typealias Deletion = Components.Schemas.AccountDeletionResponse

    private let api: any APIProtocol

    public init(api: any APIProtocol) {
        self.api = api
    }

    /// Свои зоны приватности.
    public func privacyZones() async throws -> [PrivacyZone] {
        switch try await api.listPrivacyZones() {
        case .ok(let ok):
            return try ok.body.json
        case .undocumented(let status, _):
            throw AccountServiceError.unexpectedStatus(status)
        }
    }

    /// Новая зона с центром в точке; радиус назначает сервер (`PrivacyConfig.zoneRadiusMeters`).
    /// - Throws: `AccountServiceError.rejected` с кодом сервера (400 — неверная точка, 409 — зон уже `maxZones`).
    public func addPrivacyZone(latitude: Double, longitude: Double) async throws -> PrivacyZone {
        switch try await api.createPrivacyZone(body: .json(.init(lat: latitude, lon: longitude))) {
        case .created(let created):
            return try created.body.json
        case .badRequest(let response):
            throw AccountServiceError.rejected(status: 400, code: try? response.body.application_problem_plus_json.code)
        case .conflict(let response):
            throw AccountServiceError.rejected(status: 409, code: try? response.body.application_problem_plus_json.code)
        case .undocumented(let status, _):
            throw AccountServiceError.unexpectedStatus(status)
        }
    }

    /// Убрать зону. Уже убранная (404) — тоже успех: повтор после обрыва связи не ошибка.
    public func removePrivacyZone(id: String) async throws {
        switch try await api.deletePrivacyZone(path: .init(id: id)) {
        case .noContent, .notFound:
            return
        case .undocumented(let status, _):
            throw AccountServiceError.unexpectedStatus(status)
        }
    }

    /// Удалить аккаунт: сервер принимает запрос (202) и стирает данные к `deleteByMs`.
    /// - Throws: `AccountServiceError.notFound` — аккаунта уже нет.
    public func deleteAccount() async throws -> Deletion {
        switch try await api.deleteMe() {
        case .accepted(let accepted):
            return try accepted.body.json
        case .notFound:
            throw AccountServiceError.notFound
        case .undocumented(let status, _):
            throw AccountServiceError.unexpectedStatus(status)
        }
    }

    /// «Мои данные» — JSON для файла в `Exports/` (ключи по алфавиту, с отступами: файл читает человек).
    public func exportData() async throws -> Data {
        switch try await api.exportMyData() {
        case .ok(let ok):
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            return try encoder.encode(try ok.body.json)
        case .notFound:
            throw AccountServiceError.notFound
        case .undocumented(let status, _):
            throw AccountServiceError.unexpectedStatus(status)
        }
    }
}

public enum AccountServiceError: Error, Equatable {
    /// Сервер отказал по существу: код из `application/problem+json`.
    case rejected(status: Int, code: String?)
    /// Аккаунта нет (404).
    case notFound
    /// Сервер ответил не так, как описано в контракте.
    case unexpectedStatus(Int)
}
