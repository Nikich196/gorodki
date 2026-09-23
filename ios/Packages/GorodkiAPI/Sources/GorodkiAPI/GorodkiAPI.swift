import OpenAPIRuntime

/// Клиент API «Городков». Типы и методы (`Client`, `Components.Schemas.*`, `Operations.*`) генерирует
/// swift-openapi-generator из `openapi.json` — копии `contracts/openapi.v1.json`, которую обновляет тест сервера.
public enum GorodkiAPI {
    /// Настройки клиента: даты — ISO 8601 с долями секунды (так их пишет .NET). В самом API время — миллисекунды,
    /// даты остались только в служебном `/health`.
    public static let configuration = Configuration(dateTranscoder: .iso8601WithFractionalSeconds)
}
