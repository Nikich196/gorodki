import Foundation
import Testing

@testable import GameCore

/// Конфиг версии 1 — контракт между сервером и телефоном: `contracts/game-config.v1.json`.
/// Этот тест держит телефон, `GameConfigContractTests` — сервер: если числа разойдутся, упадёт хотя бы один.
@Suite("Контракт игрового конфига v1 (contracts/game-config.v1.json)")
struct ContractTests {
    /// Только те разделы, которые телефон читает напрямую.
    private struct ConfigFile: Decodable {
        struct Capture: Decodable {
            var loopDetector: LoopDetectorSettings
        }

        struct Leagues: Decodable {
            var run: LeagueRules
            var bike: LeagueRules
        }

        var capture: Capture
        var leagues: Leagues
        var exploration: ExplorationSettings
    }

    private static func load() throws -> ConfigFile {
        let relative = "contracts/game-config.v1.json"
        var directory = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
        for _ in 0..<10 {
            let candidate = directory.appendingPathComponent(relative)
            if FileManager.default.fileExists(atPath: candidate.path) {
                return try JSONDecoder().decode(ConfigFile.self, from: Data(contentsOf: candidate))
            }
            directory.deleteLastPathComponent()
        }
        throw CocoaError(.fileNoSuchFile, userInfo: [NSFilePathErrorKey: relative])
    }

    @Test("Правила лиг на телефоне — те же, что в конфиге сервера")
    func leagues() throws {
        let file = try Self.load()

        #expect(file.leagues.run == .run)
        #expect(file.leagues.bike == .bike)
    }

    @Test("Детектор петли — те же числа")
    func loopDetector() throws {
        #expect(try Self.load().capture.loopDetector == LoopDetectorSettings())
    }

    @Test("Туман — те же радиус, разрывы и «Радар»")
    func exploration() throws {
        let exploration = try Self.load().exploration

        #expect(exploration == ExplorationSettings())
        #expect(exploration.maxGapMeters.value(for: .bike) == 200)
    }
}
