import Foundation
import GameCore
import Testing

@testable import Persistence

@Suite("Точка «Дом» — только на телефоне: файл, замена, стирание")
struct HomeStoreTests {
    private let store = HomeStore(
        url: FileManager.default.temporaryDirectory
            .appendingPathComponent("home-\(UUID().uuidString)", isDirectory: true)
            .appendingPathComponent("home.json"))

    @Test("Нет файла — нет «Дома»; поставить, заменить, убрать; убрать дважды — не ошибка")
    func roundTrip() throws {
        #expect(store.load() == nil)
        try store.save(Coordinate(latitude: 52.0976, longitude: 23.7341))
        #expect(store.load() == Coordinate(latitude: 52.0976, longitude: 23.7341))
        try store.save(Coordinate(latitude: 52.1, longitude: 23.7))
        #expect(store.load() == Coordinate(latitude: 52.1, longitude: 23.7))
        try store.remove()
        #expect(store.load() == nil)
        try store.remove()
    }

    @Test("Испорченный файл или негодная точка — как без «Дома»")
    func corrupted() throws {
        try FileManager.default.createDirectory(
            at: store.url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("не json".utf8).write(to: store.url)
        #expect(store.load() == nil)
        try Data(#"{"latitude":95,"longitude":0}"#.utf8).write(to: store.url)
        #expect(store.load() == nil)
    }
}
