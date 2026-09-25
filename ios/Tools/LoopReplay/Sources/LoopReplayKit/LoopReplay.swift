import Foundation
import GameCore

/// Итог половины телефона — вход половины сервера (`backend/tools/Gorodki.Calibration`).
public struct LoopReplayOutput: Codable, Sendable, Equatable {
    /// Имя входного файла — для подписи отчёта.
    public var input: String
    /// Судья брал порог точности новичка (`capture.newcomerMaxAccuracyMeters`), а не лиги.
    public var newcomer: Bool
    public var runs: [RunReplay]
}

/// Один забег: точки, по которым сервер построит кольца, и петли, найденные при каждом варианте чисел детектора.
public struct RunReplay: Codable, Sendable, Equatable {
    public var id: String
    public var league: League
    public var motionAuthorized: Bool
    /// Точки подряд с номера 0: только это начало следа сервер судит и режет на кольца (`LoopRing`).
    public var points: [MyData.Point]
    public var motion: [MyData.Motion]
    public var steps: [MyData.Steps]
    /// Сколько точек лежит после первого пропуска номеров: их не разбирает ни сервер (ждёт недостающие), ни разбор.
    public var pointsAfterGap: Int
    public var judge: JudgeCounts
    /// Итоги заявок этого забега на сервере, если они есть в выгрузке.
    public var serverCaptures: [MyData.Capture]
    public var variants: [DetectorRun]
}

/// Что решил судья по точкам забега.
public struct JudgeCounts: Codable, Sendable, Equatable {
    public var accepted = 0
    public var ignored = 0
    /// Разрывов следа: на каждом детектор начинает заново.
    public var breaks = 0
}

/// Петли, найденные при одном наборе чисел детектора.
public struct DetectorRun: Codable, Sendable, Equatable {
    public var detector: LoopDetectorSettings
    public var loops: [FoundLoop]
}

/// Заявка, которую отправил бы телефон, и числа для разбора.
public struct FoundLoop: Codable, Sendable, Equatable {
    public var startSeq: Int
    public var endSeq: Int
    public var closure: LoopClosure
    /// Грубая площадь на телефоне, м² (как в заявке).
    public var estimatedArea: Double
    /// R для концов петли по формуле детектора: clamp(k·√(accᵢ² + accₙ²), R_мин, R_макс), м.
    public var radiusMeters: Double
    /// Расстояние между концами петли, м.
    public var gapMeters: Double
    public var startAccuracy: Double
    public var endAccuracy: Double
}

/// Перепрогон записанного забега через судью и детектор петли GameCore — тот же путь, что у `RunSession` на телефоне:
/// отброшенная судьёй точка в след не идёт, на разрыве детектор начинается заново, принятая точка и точка разрыва
/// идут в детектор. Датчики судья получает, как на сервере (`TrackJudging`): перед точкой — все записи не позже её.
///
/// Чего перепрогон не знает: перезапусков приложения (после них телефон начинает детектор заново, а в выгрузке их не
/// видно) и заявок, которые очередь не записала.
public enum LoopReplay {
    public static func run(
        _ data: MyData, input: String, detectors: [LoopDetectorSettings], newcomer: Bool = false,
        runId: String? = nil, rules: PhoneRules = .version1
    ) -> LoopReplayOutput {
        let runs = data.runs.filter { runId == nil || $0.id.lowercased() == runId?.lowercased() }
        return LoopReplayOutput(
            input: input, newcomer: newcomer,
            runs: runs.map { run in
                replay(
                    run, captures: data.captures?.filter { $0.runId.lowercased() == run.id.lowercased() } ?? [],
                    detectors: detectors, rules: rules.rules(for: run.league, newcomer: newcomer))
            })
    }

    public static func encode(_ value: some Encodable) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return try encoder.encode(value)
    }

    static func replay(
        _ run: MyData.Run, captures: [MyData.Capture], detectors: [LoopDetectorSettings], rules: LeagueRules
    ) -> RunReplay {
        var prefix: [MyData.Point] = []
        for point in run.points.sorted(by: { $0.seq < $1.seq }) where point.seq != prefix.last?.seq {
            guard point.seq == prefix.count else { break }
            prefix.append(point)
        }
        let points = prefix.map(\.trackPoint)
        let (verdicts, counts) = judge(points, run: run, rules: rules)
        return RunReplay(
            id: run.id, league: run.league, motionAuthorized: run.motionAuthorized, points: prefix,
            motion: run.motion, steps: run.steps, pointsAfterGap: Set(run.points.map(\.seq)).count - prefix.count,
            judge: counts, serverCaptures: captures,
            variants: detectors.map {
                DetectorRun(detector: $0, loops: detect(points, verdicts: verdicts, settings: $0))
            })
    }

    /// Вердикт каждой точке. Записи датчиков — без повторов, по времени (как `TrackJudging.JudgeAll`).
    static func judge(_ points: [TrackPoint], run: MyData.Run, rules: LeagueRules) -> ([JudgeVerdict], JudgeCounts) {
        let motion = unique(run.motion).enumerated().sorted { ($0.element.t, $0.offset) < ($1.element.t, $1.offset) }
            .map(\.element)
        let steps = unique(run.steps).enumerated()
            .sorted { ($0.element.end, $0.element.start, $0.offset) < ($1.element.end, $1.element.start, $1.offset) }
            .map(\.element)
        var judge = SegmentJudge(league: run.league, rules: rules)
        var verdicts: [JudgeVerdict] = []
        var counts = JudgeCounts()
        var nextMotion = 0
        var nextSteps = 0
        for point in points {
            let ms = StoragePrecision.milliseconds(point.timestamp)
            while nextMotion < motion.count, motion[nextMotion].t <= ms {
                let sample = motion[nextMotion]
                judge.record(MotionSample(timestamp: Double(sample.t) / 1_000, activity: sample.activity))
                nextMotion += 1
            }
            while nextSteps < steps.count, steps[nextSteps].end <= ms {
                let sample = steps[nextSteps]
                judge.record(
                    PedometerSample(
                        start: Double(sample.start) / 1_000, end: Double(sample.end) / 1_000, steps: sample.steps))
                nextSteps += 1
            }
            let verdict = judge.judge(point, now: point.timestamp)
            switch verdict {
            case .accepted: counts.accepted += 1
            case .ignored: counts.ignored += 1
            case .segmentBroken: counts.breaks += 1
            }
            verdicts.append(verdict)
        }
        return (verdicts, counts)
    }

    /// Петли, которые заявил бы телефон с этими числами. Номер точки равен её индексу: точки идут подряд с нуля.
    static func detect(_ points: [TrackPoint], verdicts: [JudgeVerdict], settings: LoopDetectorSettings)
        -> [FoundLoop]
    {
        var detector = LoopDetector(settings: settings)
        var loops: [FoundLoop] = []
        for (point, verdict) in zip(points, verdicts) {
            switch verdict {
            case .accepted: break
            case .ignored: continue
            case .segmentBroken: detector.reset()
            }
            guard let claim = detector.add(point) else { continue }
            let start = points[claim.startSeq]
            let end = points[claim.endSeq]
            let (a, b) = (start.horizontalAccuracy, end.horizontalAccuracy)
            let radius = settings.radiusFactor * (a * a + b * b).squareRoot()
            loops.append(
                FoundLoop(
                    startSeq: claim.startSeq, endSeq: claim.endSeq, closure: claim.closure,
                    estimatedArea: claim.estimatedArea,
                    radiusMeters: min(max(radius, settings.minRadiusMeters), settings.maxRadiusMeters),
                    gapMeters: Geodesy.distance(from: start.coordinate, to: end.coordinate),
                    startAccuracy: start.horizontalAccuracy, endAccuracy: end.horizontalAccuracy))
        }
        return loops
    }

    private static func unique<T: Hashable>(_ items: [T]) -> [T] {
        var seen = Set<T>()
        return items.filter { seen.insert($0).inserted }
    }
}
