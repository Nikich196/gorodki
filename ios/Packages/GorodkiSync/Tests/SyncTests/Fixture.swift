import Foundation
import GameCore

@testable import Sync

/// Общие заготовки: забег, точки по секунде, запись забега в очередь.
enum Fixture {
    static let start = 1_790_000_000.0
    static let owner = "player-1"
    static let device = UUID()

    static func run(id: UUID = UUID(), owner: String = owner, startedAt: Double = start) -> LocalRun {
        LocalRun(
            id: id, ownerId: owner, league: .run, configVersion: 1,
            startedAtMs: StoragePrecision.milliseconds(startedAt),
            deviceId: device, appVersion: "1.0 (1)", motionAuthorized: true)
    }

    /// Точка номер `seq` — через `seq` секунд после начала, по 1 м на север.
    static func point(_ seq: Int, startedAt: Double = start) -> TrackPoint {
        TrackPoint(
            seq: seq, coordinate: Coordinate(latitude: 52.09 + Double(seq) * 9e-6, longitude: 23.68),
            timestamp: startedAt + Double(seq), horizontalAccuracy: 5, speed: 3)
    }

    static func policy(maxPoints: Int = 10, maxAgeSeconds: Double = 3_600) -> ChunkPolicy {
        var policy = ChunkPolicy()
        policy.maxPoints = maxPoints
        policy.maxAgeSeconds = maxAgeSeconds
        return policy
    }

    static func loop(_ startSeq: Int, _ endSeq: Int) -> LoopClaim {
        LoopClaim(startSeq: startSeq, endSeq: endSeq, closure: .crossing, estimatedArea: 5_000)
    }

    /// Забег из `count` точек (по умолчанию по 10 в куске) с записью движения в начале; заявки петель — после точки
    /// с их `endSeq`.
    @discardableResult
    static func record(
        _ run: LocalRun, points count: Int, into store: any SyncStore, claims: [LoopClaim] = [], finish: Bool = true,
        policy: ChunkPolicy = policy()
    ) async throws -> RunRecorder {
        let startedAt = Double(run.startedAtMs) / 1000
        let recorder = try await RunRecorder.begin(run, store: store, policy: policy)
        await recorder.record(MotionSample(timestamp: startedAt + 0.5, activity: .running))
        for seq in 0..<count {
            try await recorder.record(point(seq, startedAt: startedAt))
            for loop in claims where loop.endSeq == seq {
                try await recorder.claim(loop)
            }
        }
        if finish {
            try await recorder.finish(endedAt: startedAt + Double(count))
        }
        return recorder
    }
}
