import Foundation
import Testing

@testable import Gorodki

/// «Посмотреть демо» (`App/Demo`): образцы, на которых оно работает в любой сборке.
@Suite("Демо: встроенные образцы")
struct DemoModeTests {
    @Test("Образцы демо — папкой DemoData в пакете приложения, и демо читает из неё")
    func demoDataIsBundled() throws {
        // ios/DemoData — символьная ссылка на contracts/samples. Git с core.symlinks=false (копия репозитория с Windows)
        // выписывает её файлом с текстом ссылки — тогда в пакете вместо папки файл, и в Release демо пустое.
        let url = try #require(
            Bundle.main.url(forResource: "me", withExtension: "json", subdirectory: "DemoData"),
            "Нет DemoData/me.json: ios/DemoData должна быть ссылкой на ../contracts/samples")
        #expect(FileManager.default.fileExists(atPath: url.path))
        #expect(Fixtures.sampleFolders.contains("DemoData"))
    }
}
