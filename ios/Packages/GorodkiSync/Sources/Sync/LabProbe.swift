import Foundation

/// Пробный забег «Лаборатории» (PLAN.md, §10, спайк S1): настоящий путь забега — трекер, судья, детектор петель, туман,
/// куски в очереди на телефоне — без входа и без сервера. Хозяин таких забегов — `labOwnerId`. Синхронизация отправляет
/// только забеги вошедшего игрока (`SyncEngine`, его идентификатор — `sub` из токена сервера), поэтому пробные остаются
/// на телефоне, пока их не сотрут (`removeLabRuns`, выход из аккаунта). После перезапуска приложения пробный и настоящий
/// забеги продолжаются отдельно и не закрывают друг друга (`RunTracker.recover(labProbe:)`); в историю забегов пробные
/// не попадают.
extension LocalRun {
    /// Хозяин пробных забегов «Лаборатории».
    public static let labOwnerId = "lab"

    /// Пробный забег «Лаборатории».
    public var isLabProbe: Bool { ownerId == Self.labOwnerId }
}

extension SyncStore {
    /// Стереть пробные забеги «Лаборатории» с кусками и заявками; забеги игрока остаются. Идущий пробный забег сначала
    /// заканчивают: иначе запись продолжала бы писать куски стёртого забега.
    /// - Returns: сколько забегов стёрто.
    @discardableResult
    public func removeLabRuns() async throws -> Int {
        let probes = try await runs().filter(\.isLabProbe)
        for run in probes {
            try await removeRun(run.id)
        }
        return probes.count
    }
}

/// Что забег записал в очередь, — для «Лаборатории»: видно, что путь забега дошёл до базы, и видны числа спайка S1,
/// которые есть только в записанном: разрывы GPS и отметка датчиков.
public struct QueuedRunSummary: Equatable, Sendable {
    /// Разрыв GPS длиннее этого — нарушение критерия спайка S1: 30 минут в кармане без разрыва дольше 15 с (PLAN.md, §10).
    public static let gapLimitSeconds = 15.0

    public var chunks = 0
    public var points = 0
    public var claims = 0
    /// Разрывов между соседними записанными точками длиннее `gapLimitSeconds`. После перезапуска приложения
    /// незапечатанный хвост теряется — разрыв считается по записанному, как его увидит сервер.
    public var gapsOverLimit = 0
    public var longestGapSeconds = 0.0
    /// От старта до последней записанной точки, секунды.
    public var recordedSeconds = 0.0
    /// На сколько отметка «все датчики до этого момента получены» отстаёт от последней записанной точки, секунды, —
    /// столько заявка петли с концом в этой точке ждала бы датчиков. Трекер ставит отметку «сейчас −
    /// `RunTracker.sensorLagSeconds`» раз в `RunTracker.tickInterval`, поэтому обычно это 10–15 с; больше — таймер трекера
    /// вставал (например, в фоне); 0 — GPS молчал, а отметка ушла дальше последней точки. `nil` — кусков ещё нет.
    public var sensorLagSeconds: Double?

    public init() {}

    /// Сводка забега `runId` по очереди; `nil` — такого забега в очереди нет.
    public static func of(_ runId: UUID, in store: any SyncStore) async throws -> QueuedRunSummary? {
        guard let run = try await store.runs().first(where: { $0.id == runId }) else { return nil }
        let chunks = try await store.chunks(of: runId)
        var summary = QueuedRunSummary()
        summary.chunks = chunks.count
        summary.claims = try await store.claims(of: runId).count
        let points = chunks.flatMap(\.points).sorted { $0.seq < $1.seq }
        summary.points = points.count
        for (previous, next) in zip(points, points.dropFirst()) {
            let gap = next.timestamp - previous.timestamp
            summary.longestGapSeconds = max(summary.longestGapSeconds, gap)
            if gap > gapLimitSeconds {
                summary.gapsOverLimit += 1
            }
        }
        if let lastPointMs = run.lastPointMs {
            summary.recordedSeconds = Double(max(0, lastPointMs - run.startedAtMs)) / 1_000
            if let markMs = run.sealedSensorsMarkMs {
                summary.sensorLagSeconds = Double(max(0, lastPointMs - markMs)) / 1_000
            }
        }
        return summary
    }
}
