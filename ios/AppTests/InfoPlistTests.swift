import Foundation
import Testing

@testable import Gorodki

/// Что iOS проверяет по Info.plist при запуске (тесты идут внутри приложения, `Bundle.main` — его пакет). Разойдись
/// идентификаторы фоновых задач с кодом — `BackgroundSync.register()` в `didFinishLaunching` оборвёт запуск; без
/// фоновых режимов не будет ни геопозиции забега в кармане, ни досылки очереди.
@Suite("Info.plist приложения: фоновые задачи и режимы")
struct InfoPlistTests {
    @Test("Идентификаторы фоновой досылки очереди разрешены в Info.plist — иначе регистрация обрывает запуск")
    func backgroundTaskIdentifiersArePermitted() throws {
        let permitted = try #require(
            Bundle.main.object(forInfoDictionaryKey: "BGTaskSchedulerPermittedIdentifiers") as? [String])
        #expect(permitted.contains(BackgroundSync.refreshIdentifier))
        #expect(permitted.contains(BackgroundSync.uploadIdentifier))
    }

    @Test("Фоновые режимы: геопозиция забега, голос в кармане, короткое пробуждение и длинная досылка с сетью")
    func backgroundModes() throws {
        let modes = try #require(Bundle.main.object(forInfoDictionaryKey: "UIBackgroundModes") as? [String])
        #expect(Set(modes).isSuperset(of: ["location", "audio", "fetch", "processing"]), "режимы: \(modes)")
    }

    @Test("Выгрузки видны в «Файлах»: общий доступ к Documents и открытие на месте")
    func filesAppAccess() {
        #expect(Bundle.main.object(forInfoDictionaryKey: "UIFileSharingEnabled") as? Bool == true)
        #expect(Bundle.main.object(forInfoDictionaryKey: "LSSupportsOpeningDocumentsInPlace") as? Bool == true)
    }
}
