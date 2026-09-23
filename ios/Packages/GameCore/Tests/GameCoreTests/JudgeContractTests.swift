import Foundation
import Testing

@testable import GameCore

/// Эталоны судьи отрезков — `contracts/segment-judge.v1.json`. Их записывает этот тест из сценариев ниже
/// (`GORODKI_UPDATE_CONTRACTS=1 swift test`), а проверяют обе реализации: эта и серверная (`SegmentJudgeContractTests`).
/// Так сервер повторяет проверки телефона до вердикта (PLAN.md, §3.9, слой 3).
@Suite("Контракт судьи отрезков (contracts/segment-judge.v1.json)")
struct JudgeContractTests {
    struct Contract: Codable, Equatable {
        var version: Int
        var scenarios: [Scenario]
    }

    struct Scenario: Codable, Equatable {
        var name: String
        var league: League
        var events: [Event]
    }

    /// Событие в том порядке, в каком его получает судья: точка (с ожидаемым вердиктом) или запись датчика.
    struct Event: Codable, Equatable {
        var point: PointEvent?
        var motion: MotionEvent?
        var steps: StepsEvent?
        var expect: String?
    }

    struct PointEvent: Codable, Equatable {
        var t: Int64
        var lat: Double
        var lon: Double
        var acc: Double
        var speed: Double?
    }

    struct MotionEvent: Codable, Equatable {
        var t: Int64
        var activity: MotionActivity
    }

    struct StepsEvent: Codable, Equatable {
        var start: Int64
        var end: Int64
        var steps: Int?
    }

    private static let relativePath = "contracts/segment-judge.v1.json"

    @Test("Судья на телефоне даёт вердикты из эталонов")
    func replayMatchesExpectations() throws {
        for scenario in try Self.load().scenarios {
            var judge = SegmentJudge(league: scenario.league)
            for (index, event) in scenario.events.enumerated() {
                let verdict = Self.apply(event, to: &judge, seq: index)
                #expect(verdict == event.expect, "\(scenario.name), событие \(index)")
            }
        }
    }

    @Test("Эталоны совпадают с текущими сценариями")
    func contractIsUpToDate() throws {
        let generated = Self.generate()
        if ProcessInfo.processInfo.environment["GORODKI_UPDATE_CONTRACTS"] == "1" {
            try Self.write(generated)
        }
        #expect(try Self.load() == generated)
    }

    @Test("В эталонах есть каждая причина разрыва и отбрасывания, которую может увидеть сервер")
    func coverage() throws {
        let expectations = Set(try Self.load().scenarios.flatMap { $0.events.compactMap(\.expect) })
        let required = [
            "accepted", "ignored:poorAccuracy", "broken:teleport", "broken:tooFast", "broken:vehicle", "broken:cycling",
            "broken:noSteps", "broken:strideOutOfRange", "broken:carLaunch",
        ]
        for verdict in required {
            #expect(expectations.contains(verdict), "нет «\(verdict)»")
        }
    }

    // MARK: - Прогон

    /// Применяет событие; для точки возвращает вердикт строкой, как в эталонах. «Сейчас» — время самой точки.
    private static func apply(_ event: Event, to judge: inout SegmentJudge, seq: Int) -> String? {
        if let motion = event.motion {
            judge.record(MotionSample(timestamp: Double(motion.t) / 1000, activity: motion.activity))
        }
        if let steps = event.steps {
            judge.record(
                PedometerSample(start: Double(steps.start) / 1000, end: Double(steps.end) / 1000, steps: steps.steps))
        }
        guard let point = event.point else { return nil }
        let trackPoint = TrackPoint(
            seq: seq,
            coordinate: Coordinate(latitude: point.lat, longitude: point.lon),
            timestamp: Double(point.t) / 1000,
            horizontalAccuracy: point.acc,
            speed: point.speed)
        return describe(judge.judge(trackPoint, now: trackPoint.timestamp))
    }

    private static func describe(_ verdict: JudgeVerdict) -> String {
        switch verdict {
        case .accepted: "accepted"
        case .ignored(let issue): "ignored:\(issue.rawValue)"
        case .segmentBroken(let issue): "broken:\(issue.rawValue)"
        }
    }

    // MARK: - Сценарии

    private struct Recording {
        var name: String
        var league: League
        var points: [TrackPoint]
        var motion: [MotionSample] = []
        var steps: [PedometerSample] = []
    }

    private static func recordings() -> [Recording] {
        var result: [Recording] = []

        func straight(
            _ name: String, _ league: League, speed: Double, seconds: Int, reportSpeed: Bool = false,
            motion: [(at: Int, activity: MotionActivity)] = [], stepsPerFiveSeconds: Int?? = nil
        ) {
            var sim = TrackSimulator()
            let points = sim.straight(from: (0, 0), speed: speed, seconds: seconds, reportSpeed: reportSpeed)
            var recording = Recording(name: name, league: league, points: points)
            recording.motion = motion.map { MotionSample(timestamp: points[$0.at].timestamp, activity: $0.activity) }
            if let steps = stepsPerFiveSeconds {
                recording.steps = stride(from: 0, to: seconds, by: 5).map {
                    PedometerSample(start: points[$0].timestamp, end: points[$0].timestamp + 5, steps: steps)
                }
            }
            result.append(recording)
        }

        straight("бег: ходьба 1,4 м/с дольше 5 минут", .run, speed: 1.4, seconds: 320)
        straight("бег: 4,5 м/с со скоростью от GPS", .run, speed: 4.5, seconds: 120, reportSpeed: true)
        straight("бег: машина 15 м/с", .run, speed: 15, seconds: 90)
        straight("бег: 6 м/с дольше 5 минут", .run, speed: 6, seconds: 330)
        straight("бег: датчики «транспорт»", .run, speed: 2, seconds: 60, motion: [(10, .automotive)])
        straight("бег: датчики «велосипед»", .run, speed: 4, seconds: 60, motion: [(5, .cycling)])
        straight(
            "бег: смена «ходьба → транспорт → ходьба»", .run, speed: 2, seconds: 90,
            motion: [(0, .walking), (20, .automotive), (35, .walking)])
        straight("бег: движение без шагов", .run, speed: 2, seconds: 60, stepsPerFiveSeconds: .some(0))
        straight("бег: шаг 5 м", .run, speed: 2, seconds: 60, stepsPerFiveSeconds: .some(2))
        straight("бег: шаг 0,77 м", .run, speed: 2, seconds: 60, stepsPerFiveSeconds: .some(13))
        straight("бег: шагомер не знает", .run, speed: 2, seconds: 60, stepsPerFiveSeconds: .some(nil))

        var blurry = TrackSimulator()
        let blurryPoints = (0..<60).map { i in
            blurry.next(east: 2 * Double(i), north: 0, accuracy: i % 10 == 9 ? 40 : 5)
        }
        result.append(Recording(name: "бег: каждая десятая точка с точностью 40 м", league: .run, points: blurryPoints))

        var jump = TrackSimulator()
        var jumpPoints = (0..<10).map { i in jump.next(east: 2 * Double(i), north: 0) }
        jump.time += 1
        jumpPoints.append(jump.next(east: 520, north: 0))
        jumpPoints += (1..<10).map { i in jump.next(east: 520 + 2 * Double(i), north: 0) }
        result.append(Recording(name: "бег: скачок на 500 м за 2 с", league: .run, points: jumpPoints))

        straight("вело: 20 км/ч дольше 5 минут", .bike, speed: 5.6, seconds: 320)
        straight("вело: 50 км/ч", .bike, speed: 13.9, seconds: 90)
        straight(
            "вело: «транспорт» большую часть 2 минут при 30 км/ч", .bike, speed: 8.3, seconds: 150,
            motion: [(0, .automotive)])

        var launch = TrackSimulator()
        var launchPoints = launch.straight(from: (0, 0), speed: 2.8, seconds: 20, reportSpeed: true)
        var east = 2.8 * 20
        for speed in [5.0, 7.5, 9.5, 11.1, 11.1, 11.1] {
            east += speed
            launchPoints.append(launch.next(east: east, north: 0, speed: speed))
        }
        result.append(Recording(name: "вело: разгон как у машины", league: .bike, points: launchPoints))

        return result
    }

    /// Сценарии → события в порядке сервера: перед точкой — записи движения не позже неё и шагомера, закончившиеся
    /// не позже (`TrackJudging.JudgeAll` на сервере). Всё — в точности хранения. Ожидания — вердикты этой реализации.
    private static func generate() -> Contract {
        let scenarios = recordings().map { recording -> Scenario in
            var motionQueue = recording.motion.map { $0.quantizedForStorage() }
            var stepQueue = recording.steps.map { $0.quantizedForStorage() }
            var events: [Event] = []
            for point in recording.points.map({ $0.quantizedForStorage() }) {
                while let sample = motionQueue.first, sample.timestamp <= point.timestamp {
                    let time = StoragePrecision.milliseconds(sample.timestamp)
                    events.append(Event(motion: MotionEvent(t: time, activity: sample.activity)))
                    motionQueue.removeFirst()
                }
                while let sample = stepQueue.first, sample.end <= point.timestamp {
                    events.append(
                        Event(
                            steps: StepsEvent(
                                start: StoragePrecision.milliseconds(sample.start),
                                end: StoragePrecision.milliseconds(sample.end),
                                steps: sample.steps)))
                    stepQueue.removeFirst()
                }
                events.append(
                    Event(
                        point: PointEvent(
                            t: StoragePrecision.milliseconds(point.timestamp),
                            lat: point.coordinate.latitude,
                            lon: point.coordinate.longitude,
                            acc: point.horizontalAccuracy,
                            speed: point.speed)))
            }

            var judge = SegmentJudge(league: recording.league)
            for index in events.indices {
                events[index].expect = apply(events[index], to: &judge, seq: index)
            }
            return Scenario(name: recording.name, league: recording.league, events: events)
        }
        return Contract(version: 1, scenarios: scenarios)
    }

    // MARK: - Файл

    private static func contractURL() throws -> URL {
        var directory = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
        for _ in 0..<10 {
            if FileManager.default.fileExists(atPath: directory.appendingPathComponent("contracts").path) {
                return directory.appendingPathComponent(relativePath)
            }
            directory.deleteLastPathComponent()
        }
        throw CocoaError(.fileNoSuchFile, userInfo: [NSFilePathErrorKey: relativePath])
    }

    private static func load() throws -> Contract {
        try JSONDecoder().decode(Contract.self, from: Data(contentsOf: contractURL()))
    }

    /// Одно событие — одна строка: так правки эталонов видны в обзоре PR.
    private static func write(_ contract: Contract) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        func json(_ value: some Encodable) throws -> String {
            String(decoding: try encoder.encode(value), as: UTF8.self)
        }

        var lines = ["{\"version\":\(contract.version),\"scenarios\":["]
        for (scenarioIndex, scenario) in contract.scenarios.enumerated() {
            lines.append("{\"name\":\(try json(scenario.name)),\"league\":\(try json(scenario.league)),\"events\":[")
            for (index, event) in scenario.events.enumerated() {
                lines.append(try json(event) + (index + 1 < scenario.events.count ? "," : ""))
            }
            lines.append("]}" + (scenarioIndex + 1 < contract.scenarios.count ? "," : ""))
        }
        lines.append("]}")
        try (lines.joined(separator: "\n") + "\n").write(to: try contractURL(), atomically: true, encoding: .utf8)
    }
}
