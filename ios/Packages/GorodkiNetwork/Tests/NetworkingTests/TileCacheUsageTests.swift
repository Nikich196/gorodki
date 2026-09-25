import Foundation
import Testing

@testable import Networking

@Suite("«Хранилище»: сколько занимают тайлы на диске")
struct TileCacheUsageTests {
    private let fixture = DiskFixture()

    private func tile(x: Int, bytes: Int) -> TileFile {
        TileFile(
            x: x, y: 5775, version: 1, loadedAtMs: 0, savedAtMs: 0, cellCount: nil,
            payload: Data(repeating: 0x5B, count: bytes))
    }

    @Test("Земля и туман считаются отдельно, у всех игроков; прочее — отдельной строкой")
    func splitsByCache() throws {
        let location = fixture.location
        try location.store(owner: "p1", cache: "territory-run").write(tile(x: 1, bytes: 100))
        try location.store(owner: "p1", cache: "territory-run").write(tile(x: 2, bytes: 100))
        try location.store(owner: "p2", cache: "fog-foot-all").write(tile(x: 1, bytes: 5_000))
        try Data(repeating: 1, count: 7).write(to: location.root.appendingPathComponent("old-format.bin"))

        let usage = location.usage()
        #expect(usage.territory.files == 2)
        #expect(usage.fog.files == 1)
        #expect(usage.other == TileCacheUsage.Part(bytes: 7, files: 1))
        // Размер — сами файлы на диске (JSON-конверт больше полезной нагрузки).
        #expect(usage.territory.bytes > 200)
        #expect(usage.fog.bytes > 5_000)
        #expect(usage.totalBytes == usage.territory.bytes + usage.fog.bytes + 7)
    }

    @Test("Нет папки или всё стёрто — ноль")
    func emptyWhenMissing() throws {
        #expect(fixture.location.usage() == TileCacheUsage())
        try fixture.location.store(owner: "p1", cache: "fog-foot-all").write(tile(x: 1, bytes: 10))
        #expect(fixture.location.usage().fog.files == 1)
        fixture.location.removeAll()
        #expect(fixture.location.usage().totalBytes == 0)
    }
}
