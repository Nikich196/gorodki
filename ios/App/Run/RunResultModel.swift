import Foundation
import GameCore
import Observation
import Persistence
import Sync

/// Откуда итог и история забегов: очередь синхронизации и история на телефоне (`AppDependencies`) или образцы
/// режима фикстур.
protocol RunResultSource: Sendable {
    /// Итог из сохранённого: забег и заявки очереди (`RunResult`); `nil` — забега в очереди нет.
    func result(of runId: UUID, now: Double) async -> RunResult?
    /// След забега — точки истории (или трекера, пока история не записана).
    func track(of runId: UUID) async -> [Coordinate]
    /// Перезапросить у сервера «+N га», визиты и разбивку (`SyncEngine.refreshResults(of:)`).
    func refresh(_ runId: UUID) async
    /// След файлом GPX 1.1 в «Файлах» (`AppDependencies.exportRunGPX`); `nil` — забега нет в истории.
    func gpx(_ runId: UUID) async -> URL?
    /// Законченные забеги истории, новые первыми.
    func history() async -> [RunHistoryEntry]
}

/// Итог забега и его детали (PLAN.md, §5, экраны 8 и 10). Открылся — перезапрос у сервера (`refreshResults(of:)`),
/// дальше итог пересобирается из сохранённого, когда проход синхронизации что-то решил.
@MainActor
@Observable
final class RunResultModel: Identifiable {
    let runId: UUID
    /// Итог сразу после «Финиша» (в слое HUD) — или детали из истории.
    let justFinished: Bool
    private(set) var readout: RunResultReadout?
    /// Начало забега — заголовок.
    private(set) var startedAt: Date?
    private(set) var track: [Coordinate] = []
    /// Файл GPX для «Поделиться»; `nil` — ещё не готов или забега нет в истории.
    private(set) var gpxURL: URL?
    /// Забега уже нет в очереди (стёрт выходом) — только то, что знает история.
    private(set) var entry: RunHistoryEntry?

    var id: UUID { runId }

    @ObservationIgnored private let source: any RunResultSource
    @ObservationIgnored private let clock: @Sendable () -> Date

    init(
        runId: UUID, justFinished: Bool, source: any RunResultSource,
        clock: @escaping @Sendable () -> Date = { Date() }
    ) {
        self.runId = runId
        self.justFinished = justFinished
        self.source = source
        self.clock = clock
    }

    /// Итог из очереди и истории приложения.
    static func live(runId: UUID, justFinished: Bool) -> RunResultModel {
        RunResultModel(runId: runId, justFinished: justFinished, source: LiveRunResults())
    }

    /// Открытие: показать сохранённое, спросить сервер, показать снова.
    func load() async {
        await reload()
        if track.isEmpty {
            track = await source.track(of: runId)
        }
        await source.refresh(runId)
        await reload()
    }

    /// Пересобрать итог из сохранённого (после прохода синхронизации).
    func reload() async {
        if let result = await source.result(of: runId, now: clock().timeIntervalSince1970) {
            readout = RunResultReadout(result)
            startedAt = Date(timeIntervalSince1970: Double(result.startedAtMs) / 1_000)
        } else if entry == nil {
            entry = await source.history().first { $0.id == runId }
            startedAt = entry.map { Date(timeIntervalSince1970: Double($0.startedAtMs) / 1_000) }
        }
    }

    /// Подготовить GPX для «Поделиться».
    func prepareGPX() async {
        guard gpxURL == nil else { return }
        gpxURL = await source.gpx(runId)
    }

    /// Задать сразу (режим фикстур).
    func present(_ result: RunResult, track: [Coordinate], gpx: URL? = nil) {
        readout = RunResultReadout(result)
        startedAt = Date(timeIntervalSince1970: Double(result.startedAtMs) / 1_000)
        self.track = track
        gpxURL = gpx
    }
}

/// Строка истории забегов.
struct RunHistoryRow: Equatable, Identifiable, Sendable {
    var id: UUID
    var startedAt: Date
    var league: League
    /// «3,21 км · 17:42» — дистанция из итога (если забег ещё в очереди) и длительность.
    var summary: String
    /// «+1,25 га» — «взятое»; «≈ +1,2 га» — пока сервер не решил; `nil` — петель не было.
    var area: String?
    var isReplay: Bool
}

/// История забегов (PLAN.md, §5: «Детали забега» — из истории): законченные забеги с телефона, новые первыми.
@MainActor
@Observable
final class RunHistoryModel {
    private(set) var rows: [RunHistoryRow] = []
    private(set) var loaded = false

    @ObservationIgnored private let source: any RunResultSource
    @ObservationIgnored private let clock: @Sendable () -> Date

    init(source: any RunResultSource, clock: @escaping @Sendable () -> Date = { Date() }) {
        self.source = source
        self.clock = clock
    }

    static func live() -> RunHistoryModel {
        RunHistoryModel(source: LiveRunResults())
    }

    func load() async {
        let now = clock().timeIntervalSince1970
        var fresh: [RunHistoryRow] = []
        for entry in await source.history() {
            let result = await source.result(of: entry.id, now: now)
            fresh.append(Self.row(entry, result: result))
        }
        rows = fresh
        loaded = true
    }

    /// Детали забега из истории.
    func details(_ id: UUID) -> RunResultModel {
        RunResultModel(runId: id, justFinished: false, source: source, clock: clock)
    }

    static func row(_ entry: RunHistoryEntry, result: RunResult?) -> RunHistoryRow {
        let duration = NumberText.clock(seconds: Double(entry.endedAtMs - entry.startedAtMs) / 1_000)
        var summary = duration
        var area: String?
        if let result {
            let readout = RunResultReadout(result)
            summary = readout.distanceText + NumberText.unitSeparator + "км · " + duration
            if readout.loops > 0 {
                area = readout.takenText ?? readout.estimateText
            }
        }
        return RunHistoryRow(
            id: entry.id, startedAt: Date(timeIntervalSince1970: Double(entry.startedAtMs) / 1_000),
            league: entry.league, summary: summary, area: area, isReplay: entry.source == .replay)
    }
}

/// Итог и история из зависимостей приложения.
struct LiveRunResults: RunResultSource {
    func result(of runId: UUID, now: Double) async -> RunResult? {
        let store = AppDependencies.shared.syncStore
        guard let run = try? await store.runs().first(where: { $0.id == runId }) else { return nil }
        let claims = (try? await store.claims(of: runId)) ?? []
        return RunResult(run: run, claims: claims, now: now)
    }

    func track(of runId: UUID) async -> [Coordinate] {
        if let points = try? await AppDependencies.shared.history?.points(of: runId), !points.isEmpty {
            return points.map(\.coordinate)
        }
        // История пишется после «Финиша» в фоне — пока её нет, след последнего забега знает трекер.
        return await RunController.shared.trail().flatMap { $0 }
    }

    func refresh(_ runId: UUID) async {
        await AppDependencies.shared.syncEngine()?.refreshResults(of: runId)
    }

    func gpx(_ runId: UUID) async -> URL? {
        try? await AppDependencies.shared.exportRunGPX(runId)
    }

    func history() async -> [RunHistoryEntry] {
        (try? await AppDependencies.shared.history?.entries()) ?? []
    }
}
