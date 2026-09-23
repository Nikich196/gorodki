import Foundation

/// Сведения из профиля подписи (`embedded.mobileprovision`), который установщик кладёт в приложение.
/// Нужны, чтобы видеть прямо на телефоне, до какого числа действует подпись
/// и какие App Group она на самом деле разрешила.
struct ProvisioningProfile: Sendable {
    let name: String?
    let teamName: String?
    let expirationDate: Date?
    let appGroups: [String]

    /// Профиль из текущего приложения или `nil` (симулятор, сборка без подписи).
    static func embedded(in bundle: Bundle = .main) -> ProvisioningProfile? {
        guard
            let url = bundle.url(forResource: "embedded", withExtension: "mobileprovision"),
            let data = try? Data(contentsOf: url)
        else { return nil }
        return parse(data)
    }

    /// Профиль — это plist, завёрнутый в подпись CMS. Подпись не проверяем,
    /// а просто вырезаем XML между `<?xml` и `</plist>` и читаем его.
    static func parse(_ data: Data) -> ProvisioningProfile? {
        guard
            let start = data.range(of: Data("<?xml".utf8)),
            let end = data.range(of: Data("</plist>".utf8), in: start.lowerBound..<data.endIndex)
        else { return nil }

        let plistData = data.subdata(in: start.lowerBound..<end.upperBound)
        guard
            let plist = try? PropertyListSerialization.propertyList(from: plistData, format: nil),
            let dictionary = plist as? [String: Any]
        else { return nil }

        let entitlements = dictionary["Entitlements"] as? [String: Any] ?? [:]
        return ProvisioningProfile(
            name: dictionary["Name"] as? String,
            teamName: dictionary["TeamName"] as? String,
            expirationDate: dictionary["ExpirationDate"] as? Date,
            appGroups: entitlements["com.apple.security.application-groups"] as? [String] ?? []
        )
    }
}
