import Foundation

/// Идентификаторы фоновых задач досылки (`BackgroundSync`). Берутся из Info.plist, а не строятся из bundle ID во время
/// работы: список в Info.plist подставляется при сборке, а установщик (Sideloadly) может сменить bundle ID.
/// Идентификатор не из списка iOS не даст ни зарегистрировать, ни заявить — и досылка в фоне молча не шла бы.
enum BackgroundTaskIdentifiers {
    static let infoPlistKey = "BGTaskSchedulerPermittedIdentifiers"

    /// Разрешённый идентификатор с этим окончанием (`sync.refresh`). Списка нет — из bundle ID, как его строит сборка.
    static func pick(_ suffix: String, permitted: [String]?, bundleIdentifier: String?) -> String {
        permitted?.first { $0.hasSuffix("." + suffix) } ?? "\(bundleIdentifier ?? "gorodki").\(suffix)"
    }
}
