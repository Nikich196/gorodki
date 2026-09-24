import Foundation

/// Идентификатор установки — `deviceId` забега (защита от мультиаккаунтов, docs/architecture/captures.md): случайный
/// UUID в Keychain. Переживает переустановку приложения (Keychain при удалении не чистится), но не переезжает на другой
/// телефон (`ThisDeviceOnly`). Хранилище — то же, что у токенов (`TokenStorage`), в своей записи.
///
/// Keychain бывает недоступен (до первой разблокировки после включения): тогда на время процесса — временный
/// идентификатор, а постоянный запишется при следующем обращении. «Нет записи» и «недоступно» различаются: новый
/// идентификатор создаётся, только если записи правда нет, — иначе телефон выглядел бы для сервера новым устройством.
public actor InstallationID {
    private let storage: any TokenStorage
    private var stored: UUID?
    private var temporary: UUID?

    public init(storage: any TokenStorage) {
        self.storage = storage
    }

    public func value() -> UUID {
        if let stored { return stored }
        do {
            if let data = try storage.load(), let id = String(data: data, encoding: .utf8).flatMap(UUID.init) {
                stored = id
                return id
            }
            // Записи нет (или она испорчена) — создать и записать; не записалась — временная до следующего раза.
            let fresh = temporary ?? UUID()
            do {
                try storage.save(Data(fresh.uuidString.utf8))
                stored = fresh
                temporary = nil
            } catch {
                temporary = fresh
            }
            return fresh
        } catch {
            let fallback = temporary ?? UUID()
            temporary = fallback
            return fallback
        }
    }
}
