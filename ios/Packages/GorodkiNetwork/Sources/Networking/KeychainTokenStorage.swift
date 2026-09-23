#if canImport(Security)
    import Foundation
    import Security

    /// Токены в Keychain (PLAN.md, §7.2 «Хранение»): одна запись `kSecClassGenericPassword`, сервис — bundle ID
    /// приложения + `.auth`. Пара хранится одной записью, поэтому не бывает «новый access при старом refresh».
    ///
    /// Доступ — после первой разблокировки с момента включения: фоновая синхронизация и трекинг работают и при
    /// заблокированном экране. Только на этом устройстве: в резервную копию и на новый телефон токены не переезжают —
    /// восстановленный старый refresh-токен сервер принял бы за кражу и отозвал бы весь вход.
    public struct KeychainTokenStorage: TokenStorage {
        /// Ошибка Keychain с кодом `OSStatus` (например, `errSecInteractionNotAllowed` до первой разблокировки).
        public struct Failure: Error, Equatable {
            public let status: OSStatus
        }

        public let service: String
        public let account: String

        public init(service: String, account: String = "session") {
            self.service = service
            self.account = account
        }

        /// Сервис по bundle ID приложения: у сборок Free и Paid он разный, и записи не пересекаются.
        public init(bundleIdentifier: String) {
            self.init(service: bundleIdentifier + ".auth")
        }

        public func load() throws -> Data? {
            var query = identity
            query[kSecReturnData as String] = true
            query[kSecMatchLimit as String] = kSecMatchLimitOne
            var result: CFTypeRef?
            let status = SecItemCopyMatching(query as CFDictionary, &result)
            switch status {
            case errSecSuccess:
                guard let data = result as? Data else { throw Failure(status: errSecDecode) }
                return data
            case errSecItemNotFound:
                return nil
            default:
                throw Failure(status: status)
            }
        }

        public func save(_ data: Data) throws {
            let attributes: [String: Any] = [
                kSecValueData as String: data,
                kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
            ]
            var status = SecItemUpdate(identity as CFDictionary, attributes as CFDictionary)
            if status == errSecItemNotFound {
                status = SecItemAdd(identity.merging(attributes) { _, new in new } as CFDictionary, nil)
            }
            guard status == errSecSuccess else { throw Failure(status: status) }
        }

        public func delete() throws {
            let status = SecItemDelete(identity as CFDictionary)
            guard status == errSecSuccess || status == errSecItemNotFound else { throw Failure(status: status) }
        }

        /// Какая это запись: класс, сервис, учётная запись.
        private var identity: [String: Any] {
            [
                kSecClass as String: kSecClassGenericPassword,
                kSecAttrService as String: service,
                kSecAttrAccount as String: account,
            ]
        }
    }
#endif
