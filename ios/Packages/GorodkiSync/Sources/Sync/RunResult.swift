import Foundation
import GameCore

/// Итог забега — экраны «Итог забега» и «Детали забега» (docs/architecture/run-hud.md, «Итог забега»). Собирается
/// **из сохранённого** — забега и его заявок в очереди, — а не из памяти забега: итог одинаков сразу после «Финиша»,
/// после перезапуска и из истории. Чистая функция: данные сервера приходят в разное время, и итог просто пересобирается.
public struct RunResult: Equatable, Sendable {
    /// Состояние итога — выводится из данных.
    public enum Readiness: Equatable, Sendable {
        /// Телефону ещё есть что отправить: забег не доставлен целиком или заявки не ушли (нет сети, отложен).
        case waitingForNetwork
        /// Всё у сервера, он считает: заявки ещё не решены, «+N га» или визитов ещё нет.
        case computing
        /// Больше ждать нечего.
        case ready
    }

    /// Заявка петли в итоге.
    public struct Claim: Equatable, Sendable {
        public var claimNo: Int
        /// Оценка телефона, м² («≈»).
        public var estimatedSquareMeters: Double
        /// Итог сервера; `nil` — ещё нет.
        public var outcome: ClaimOutcome?
        /// Отказ в самой заявке или `PendingClaim.runRejectedCode`.
        public var refusedCode: String?
        public var isSettled: Bool
    }

    public var runId: UUID
    public var league: League
    public var isReplay: Bool
    public var startedAtMs: Int64
    public var endedAtMs: Int64?
    /// Забег завершился сам — вышел предел длины.
    public var endedAtLimit: Bool
    public var durationSeconds: Double
    public var distanceMeters: Double
    public var claims: [Claim]
    /// Сумма оценок петель телефоном, м².
    public var estimatedLoopSquareMeters: Double
    /// «Взятое» по засчитанным заявкам, м².
    public var takenSquareMeters: Double
    /// Площадь по видам — сумма по заявкам, приславшим разбивку; `nil` — ни одна ещё не прислала.
    public var areaByOutcome: [String: Double]?
    /// Не у всех засчитанных заявок есть разбивка — строка «позже» (сервер отдаёт её после границы публичности;
    /// перезапрос — `SyncEngine.refreshResults`).
    public var areaByOutcomePending: Bool
    /// «≈+N га» — оценка телефона, замороженная в сводке забега (не пересчитывается по кэшу тумана), м².
    public var fogEstimateSquareMeters: Double
    /// Оценка — нижняя граница («≥»): туман за всё время был известен не для всех тайлов.
    public var fogEstimateIsLowerBound: Bool
    /// «+N га» сервера: новые клетки за всё время; `nil` — ещё нет.
    public var fogNewCells: Int?
    /// Они же в м² — на широте забега; `nil` — числа ещё нет (или широта забега неизвестна).
    public var fogNewSquareMeters: Double?
    /// Визиты — сколько участков освежил забег (`RunResponse.visitedParcels`); `nil` — сервер ещё не посчитал
    /// (не раньше публичной задержки, 20 минут после конца забега): строка «позже».
    public var visitedParcels: Int?
    /// Разрывы следа по причинам (`TrackIssue.rawValue`).
    public var breaks: [String: Int]
    /// Забег отвергнут сервером (`replay_forbidden`, `run_invalid`…): итог говорит об этом вместо площадей.
    public var rejectCode: String?
    public var readiness: Readiness

    /// - Parameter now: «сейчас», секунды Unix: длительность незавершённого забега и срок, после которого «+N га» уже
    ///   не ждут (`SyncEngine.fogQueryDays`).
    public init(run: LocalRun, claims: [PendingClaim], now: Double) {
        let summary = run.summary ?? RunSummary()
        runId = run.id
        league = run.league
        isReplay = run.source == .replay
        startedAtMs = run.startedAtMs
        endedAtMs = run.endedAtMs
        endedAtLimit = summary.endedAtLimit
        durationSeconds =
            Double(max(0, (run.endedAtMs ?? StoragePrecision.milliseconds(now)) - run.startedAtMs)) / 1_000
        distanceMeters = summary.distanceMeters
        self.claims = claims.map {
            Claim(
                claimNo: $0.claimNo, estimatedSquareMeters: $0.loop.estimatedArea, outcome: $0.outcome,
                refusedCode: $0.refusedCode, isSettled: $0.isSettled)
        }
        estimatedLoopSquareMeters = claims.reduce(0) { $0 + $1.loop.estimatedArea }
        let applied = claims.compactMap(\.outcome).filter { $0.status == "applied" }
        takenSquareMeters = applied.reduce(0) { $0 + $1.areaSquareMeters }
        let breakdowns = applied.compactMap(\.areaByOutcome)
        areaByOutcome = breakdowns.isEmpty ? nil : breakdowns.reduce(into: [:]) { $0.merge($1, uniquingKeysWith: +) }
        areaByOutcomePending = breakdowns.count < applied.count
        fogEstimateSquareMeters = summary.fogNewSquareMeters
        fogEstimateIsLowerBound = summary.fogNewIsLowerBound
        fogNewCells = run.fogNewCells
        if let cells = run.fogNewCells, let latitude = summary.latitude {
            let size = FogGrid.cellSizeMeters(atLatitude: latitude)
            fogNewSquareMeters = Double(cells) * size * size
        } else {
            fogNewSquareMeters = nil
        }
        visitedParcels = run.visitedParcels
        breaks = summary.breaks
        rejectCode = run.serverState == .rejected ? run.rejectCode : nil

        let fogExpiredMs = run.startedAtMs + Int64(SyncEngine.fogQueryDays * 86_400_000)
        if run.serverState == .rejected {
            readiness = .ready  // его заявки не отправлены и не будут: ждать нечего
        } else if run.serverState == .unknown || !run.confirmedComplete
            || claims.contains(where: { !$0.sent && !$0.isSettled })
        {
            readiness = .waitingForNetwork
        } else if claims.allSatisfy(\.isSettled)
            && (run.fogNewCells != nil && run.visitedParcels != nil
                || StoragePrecision.milliseconds(now) > fogExpiredMs)
        {
            readiness = .ready
        } else {
            readiness = .computing
        }
    }
}
