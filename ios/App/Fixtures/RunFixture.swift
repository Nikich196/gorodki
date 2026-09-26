#if DEBUG
    import Foundation
    import GameCore
    import Persistence
    import Sync

    /// Образцы экранов забега для режима фикстур (`-GorodkiScreen hud`, `hud-ceremony`, `hud-collapsed`,
    /// `run-result`, `run-details`, `run-history`): петля вокруг квартала в Бресте и открытая петля после неё. Числа —
    /// как в утверждённом макете (design/APPROVALS.md): 3,2 км за 17:42 в темпе 5:32, до замыкания 140 м, петля
    /// 12 480 м² = 1,25 га = 125 соток. Ники, даты и числа — примеры.
    enum RunFixture {
        static let runId = UUID(uuidString: "5E1F0C2A-7B3D-4E6F-8A9B-0C1D2E3F4A5B") ?? UUID()
        /// Угол квартала, откуда начинается петля.
        static let origin = Coordinate(latitude: 52.0936, longitude: 23.7552)
        static let elapsedSeconds = 1_062.0
        static let distanceMeters = 3_200.0
        static let loopSquareMeters = 12_480.0

        private static let plane = LocalTangentPlane(origin: origin)

        private static func point(_ east: Double, _ north: Double) -> Coordinate {
            plane.unproject(PlanarPoint(east: east, north: north))
        }

        /// Замкнутая петля: квартал ≈ 104 × 120 м с дрожанием GPS, начало и конец — в `origin`.
        static let loop: [Coordinate] = {
            var points: [Coordinate] = []
            let corners: [(Double, Double)] = [(0, 0), (0, 120), (104, 120), (104, 0), (0, 0)]
            for (index, corner) in corners.dropLast().enumerated() {
                let next = corners[index + 1]
                for step in 0..<8 {
                    let t = Double(step) / 8
                    let wobble = sin(Double(index * 8 + step) * 1.7) * 1.8
                    points.append(
                        point(corner.0 + (next.0 - corner.0) * t + wobble, corner.1 + (next.1 - corner.1) * t - wobble))
                }
            }
            points.append(origin)
            return points
        }()

        /// Открытая петля после заявки: от точки замыкания на север и на восток — бегущий в 140 м от цели.
        static let openLoop: [Coordinate] = [
            point(-4, 30), point(-8, 70), point(-10, 110), point(-6, 150), point(20, 162), point(60, 168),
            point(96, 170), point(118, 166),
        ]

        static var trail: [[Coordinate]] { [loop + openLoop] }
        static var track: [Coordinate] { loop + openLoop }

        /// Снимок трекера: забег идёт 17:42, 3,2 км; можно замкнуть — 140 м до начала открытой петли.
        static func runningState(now: Date = Date()) -> TrackerState {
            let nowMs = StoragePrecision.milliseconds(now.timeIntervalSince1970)
            var state = TrackerState()
            state.isRunning = true
            state.runId = runId
            state.league = .run
            state.startedAtMs = nowMs - Int64(elapsedSeconds * 1_000)
            state.stats.distanceMeters = distanceMeters
            state.stats.acceptedPoints = 1_020
            state.stats.points = 1_061
            state.stats.loops = 1
            state.stats.loopAreaSquareMeters = loopSquareMeters
            state.stats.fogNewSquareMeters = 8_000
            state.stats.lastAcceptedAtMs = nowMs - 1_000
            state.stats.lastPointAtMs = nowMs - 1_000
            let here = openLoop[openLoop.count - 1]
            let offset = LocalTangentPlane(origin: here).project(origin)
            var bearing = atan2(offset.east, offset.north) * 180 / .pi
            if bearing < 0 { bearing += 360 }
            state.closureHint = .canClose(
                ClosureTarget(
                    seq: 40, coordinate: origin, distanceMeters: 137, bearingDegrees: bearing,
                    estimatedAreaSquareMeters: 12_480, belowServerMinimum: false, tooLarge: false))
            return state
        }

        static let claimedLoop = ClaimedLoop(
            claimNo: 0, loop: LoopClaim(startSeq: 0, endSeq: 32, closure: .proximity, estimatedArea: 12_000))

        /// Экраны забега для фикстуры; `nil` — экран не про забег.
        @MainActor
        static func model(_ screen: FixtureScreen, profile: ProfileModel) -> RunScreenModel? {
            let model = RunScreenModel(
                profile: profile, defaults: UserDefaults(suiteName: "fixture.run") ?? .standard)
            let contour = [LoopContour(claimNo: 0, coordinates: loop)]
            switch screen {
            case .hud:
                model.present(runningState(), trail: trail, contours: contour)
                model.hudPresented = true
            case .hudCeremony:
                var item = CeremonyItem(runId: runId, loop: claimedLoop)
                item.decision = CeremonyDecision(
                    runId: runId, claimNo: 0, applied: true, takenSquareMeters: loopSquareMeters, refusalCode: nil)
                model.present(runningState(), trail: trail, contours: contour, ceremony: item, ring: loop)
                model.hudPresented = true
            case .hudCollapsed:
                model.present(runningState(), trail: trail, contours: contour, missed: [claimedLoop])
            case .runResult:
                var ended = runningState()
                ended.isRunning = false
                model.present(ended, trail: trail)
                let result = RunResultModel(
                    runId: runId, justFinished: true, source: FixtureRunResults(published: false))
                model.result = result
            default:
                return nil
            }
            return model
        }

        /// Детали забега из истории: после границы публичности — разбивка, визиты, туман сервера, GPX.
        @MainActor
        static func details() -> RunResultModel {
            RunResultModel(runId: runId, justFinished: false, source: FixtureRunResults(published: true))
        }

        @MainActor
        static func history() -> RunHistoryModel {
            RunHistoryModel(source: FixtureRunResults(published: true))
        }
    }

    /// Итог и история для фикстур: `published` — граница публичности пройдена (разбивка, визиты, туман сервера есть).
    struct FixtureRunResults: RunResultSource {
        let published: Bool

        private static let day: Int64 = 86_400_000

        private func run(_ id: UUID, startedAtMs: Int64, minutes: Double, meters: Double) -> LocalRun {
            var run = LocalRun(
                id: id, ownerId: "fixture", league: .run, configVersion: 1, startedAtMs: startedAtMs,
                deviceId: UUID(), appVersion: "fixture", motionAuthorized: true)
            run.endedAtMs = startedAtMs + Int64(minutes * 60_000)
            run.lastSeq = 1_060
            run.serverState = .started
            run.finishSent = true
            run.confirmedComplete = true
            var summary = RunSummary()
            summary.distanceMeters = meters
            summary.fogNewSquareMeters = 8_000
            summary.latitude = 52.09
            summary.breaks = ["vehicle": 1]
            run.summary = summary
            if published {
                run.fogNewCells = 230
                run.visitedParcels = 3
            }
            return run
        }

        private func claims(_ id: UUID) -> [PendingClaim] {
            var applied = PendingClaim(runId: id, claimNo: 0, loop: RunFixture.claimedLoop.loop)
            applied.sent = true
            applied.outcome = ClaimOutcome(
                status: "applied", areaSquareMeters: RunFixture.loopSquareMeters,
                areaByOutcome: published ? ["claimedNeutral": 11_980, "refreshed": 500] : nil)
            var refused = PendingClaim(
                runId: id, claimNo: 1,
                loop: LoopClaim(startSeq: 40, endSeq: 58, closure: .crossing, estimatedArea: 1_900))
            refused.sent = true
            refused.outcome = ClaimOutcome(status: "rejected", rejectCode: "too_small")
            return [applied, refused]
        }

        private var entries: [(UUID, Int64, Double, Double)] {
            let now = StoragePrecision.milliseconds(Date().timeIntervalSince1970)
            return [
                (RunFixture.runId, now - Int64(RunFixture.elapsedSeconds * 1_000) - 60_000, 17.7, 3_200),
                (UUID(uuidString: "11111111-2222-3333-4444-555555555555") ?? UUID(), now - Self.day, 31, 5_870),
                (UUID(uuidString: "66666666-7777-8888-9999-AAAAAAAAAAAA") ?? UUID(), now - 3 * Self.day, 24.5, 4_120),
            ]
        }

        func result(of runId: UUID, now: Double) async -> RunResult? {
            guard let entry = entries.first(where: { $0.0 == runId }) else { return nil }
            let run = run(entry.0, startedAtMs: entry.1, minutes: entry.2, meters: entry.3)
            return RunResult(run: run, claims: runId == RunFixture.runId ? claims(runId) : [], now: now)
        }

        func track(of runId: UUID) async -> [Coordinate] { RunFixture.track }

        func refresh(_ runId: UUID) async {}

        func gpx(_ runId: UUID) async -> URL? {
            let points = RunFixture.track.enumerated().map { index, coordinate in
                TrackPoint(
                    seq: index, coordinate: coordinate, timestamp: 1_790_000_000 + Double(index) * 5,
                    horizontalAccuracy: 5)
            }
            let url = FileManager.default.temporaryDirectory.appendingPathComponent("gorodki-fixture.gpx")
            try? Data(GPX.document(name: "Городки — забег", points: points).utf8).write(to: url)
            return url
        }

        func history() async -> [RunHistoryEntry] {
            entries.map {
                RunHistoryEntry(
                    id: $0.0, league: .run, source: .live, startedAtMs: $0.1,
                    endedAtMs: $0.1 + Int64($0.2 * 60_000), pointCount: 1_000)
            }
        }
    }
#endif
