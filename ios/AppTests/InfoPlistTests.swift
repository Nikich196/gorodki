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

    @Test("Сборка видео-повтора (BGContinuedProcessingTask) разрешена в Info.plist — иначе iOS не примет задачу")
    func replayTaskIdentifierIsPermitted() throws {
        let permitted = try #require(
            Bundle.main.object(forInfoDictionaryKey: "BGTaskSchedulerPermittedIdentifiers") as? [String])
        #expect(permitted.contains(ReplayTaskRelay.identifier))
        #expect(ReplayTaskRelay.identifier.hasSuffix(".replay.build"))
    }

    @Test("«Картинка в картинке» видео-повтора: фоновый режим audio")
    func pictureInPictureMode() throws {
        let modes = try #require(Bundle.main.object(forInfoDictionaryKey: "UIBackgroundModes") as? [String])
        #expect(modes.contains("audio"))
    }

    @Test(
        "Тексты разрешений пунктов листика есть и по-русски: без них iOS обрывает приложение при первом запросе",
        arguments: [
            "NSPhotoLibraryUsageDescription", "NSPhotoLibraryAddUsageDescription", "NSCameraUsageDescription",
            "NSCalendarsWriteOnlyAccessUsageDescription", "NSContactsUsageDescription",
        ])
    func usageDescriptions(_ key: String) throws {
        let text = try #require(Bundle.main.object(forInfoDictionaryKey: key) as? String, "нет \(key)")
        #expect(text.count > 40)
        #expect(text.unicodeScalars.contains { (0x0410...0x044F).contains($0.value) }, "не по-русски: \(text)")
    }

    @Test("Выгрузки видны в «Файлах»: общий доступ к Documents и открытие на месте")
    func filesAppAccess() {
        #expect(Bundle.main.object(forInfoDictionaryKey: "UIFileSharingEnabled") as? Bool == true)
        #expect(Bundle.main.object(forInfoDictionaryKey: "LSSupportsOpeningDocumentsInPlace") as? Bool == true)
    }
}
