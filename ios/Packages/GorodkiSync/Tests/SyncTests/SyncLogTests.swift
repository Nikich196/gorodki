import Foundation
import Synchronization
import Testing

@testable import Sync

/// Хранилище журнала в памяти, которое видит проверка: что сохранено последним.
private final class Saved: Sendable {
    let data = Mutex<Data?>(nil)
}

@Suite("Журнал синхронизации для «Резервной копии»")
struct SyncLogTests {
    private static func report(_ stop: SyncStop? = nil, chunks: Int = 0, finished: Int = 0) -> SyncReport {
        var report = SyncReport()
        report.stop = stop
        report.uploadedChunks = chunks
        report.finishedRuns = finished
        return report
    }

    @Test("Последний проход и последний удачный: неудача не стирает время удачного")
    func lastAndLastSuccess() {
        let log = SyncLog.inMemory()
        #expect(log.current == SyncLog.State())
        log.record(Self.report(chunks: 3, finished: 1), atMs: 1_000)
        #expect(log.current.last == SyncPassRecord(atMs: 1_000, outcome: .ok, uploadedChunks: 3, finishedRuns: 1))
        #expect(log.current.lastSuccessAtMs == 1_000)
        log.record(Self.report(.offline), atMs: 2_000)
        #expect(log.current.last?.outcome == .offline)
        #expect(log.current.lastSuccessAtMs == 1_000)
    }

    @Test("Журнал переживает перезапуск: сохранённое читается новым журналом; выход стирает")
    func survivesRestart() {
        let saved = Saved()
        let open = {
            SyncLog(load: { saved.data.withLock { $0 } }, save: { data in saved.data.withLock { $0 = data } })
        }
        open().record(Self.report(), atMs: 5_000)
        let reopened = open()
        #expect(reopened.current.lastSuccessAtMs == 5_000)
        reopened.clear()
        #expect(saved.data.withLock { $0 } == nil)
        #expect(open().current == SyncLog.State())
    }

    @Test("Испорченное сохранение — как будто проходов не было")
    func corruptedStorage() {
        let log = SyncLog(load: { Data("не JSON".utf8) }, save: { _ in })
        #expect(log.current == SyncLog.State())
    }

    @Test("Каждая остановка — свой текст для игрока, без рода")
    func summaries() {
        let stops: [SyncStop?] = [nil, .offline, .unauthorized, .clockInvalid, .accountDeleting, .rateLimited]
        let texts = stops.map { SyncPassRecord(Self.report($0), atMs: 0).summary }
        #expect(Set(texts).count == stops.count)
        #expect(texts[0] == "Синхронизировано")
        #expect(texts.allSatisfy { !$0.contains("вышел") && !$0.contains("должен") })
    }
}
