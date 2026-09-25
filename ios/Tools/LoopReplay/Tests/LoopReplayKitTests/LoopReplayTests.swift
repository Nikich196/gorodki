import Foundation
import GameCore
import Testing

@testable import LoopReplayKit

/// Ожидания — из кода детектора (`LoopDetector`: R = clamp(1,62·√(accᵢ² + accₙ²), 20, 50), путь ≥ 150 м, оценка
/// площади ≥ 1 000 м²) и геометрии синтетических следов (`SyntheticRuns`).
@Suite("Разбор забега: детектор петли GameCore на записанных точках")
struct LoopReplayTests {
    private func replay(_ run: MyData.Run, grid: DetectorGrid = DetectorGrid()) throws -> RunReplay {
        try #require(LoopReplay.run(MyData(runs: [run]), input: "test.json", detectors: grid.settings).runs.first)
    }

    @Test("Квадрат 60 × 60 м с шумом ±5 м: одна петля ≈ 3 300 м² (замыкание за R до начала), R = 20 м, без разрывов")
    func square() throws {
        let run = try replay(SyntheticRuns.square())
        let loops = try #require(run.variants.first).loops

        #expect(run.judge.breaks == 0)
        #expect(run.judge.ignored == 0)
        #expect(loops.count == 1, "\(loops)")
        let loop = try #require(loops.first)
        #expect((2_800...4_400).contains(loop.estimatedArea), "\(loop)")
        #expect(loop.radiusMeters == 20)  // 1,62·√(5² + 5²) ≈ 11,5 → нижняя граница
        #expect(loop.gapMeters <= loop.radiusMeters || loop.closure == .crossing)
    }

    @Test("Судья как на телефоне: точка хуже 25 м отброшена, скачок GPS рвёт след — петля через разрыв не заявляется")
    func judgeBeforeDetector() throws {
        var poor = SyntheticRuns.square()
        poor.points[80].acc = 30
        var jump = SyntheticRuns.square()
        jump.points[100].lat += 0.01  // ≈ 1,1 км за секунду — телепорт

        let withPoor = try replay(poor)
        let withJump = try replay(jump)

        #expect(withPoor.judge.ignored == 1)
        #expect(withPoor.variants.first?.loops.count == 1)
        #expect(withJump.judge.breaks >= 1)
        #expect(withJump.variants.first?.loops.isEmpty == true)
    }

    @Test("Полоса 12 × 300 м: уже R, поэтому петля замыкается сближением, едва оценка дойдёт до 1 000 м²")
    func stripClosesEarly() throws {
        let loops = try #require(try replay(SyntheticRuns.strip()).variants.first).loops

        #expect(loops.count == 1, "\(loops)")
        let loop = try #require(loops.first)
        #expect(loop.closure == .proximity)
        #expect((1_000...1_500).contains(loop.estimatedArea), "\(loop)")
    }

    @Test("Полоса с порогом оценки 3 000 м²: петля — почти вся полоса")
    func stripWithHigherEstimateThreshold() throws {
        var grid = DetectorGrid()
        grid.minEstimatedAreaSquareMeters = [3_000]
        let loops = try #require(try replay(SyntheticRuns.strip(), grid: grid).variants.first).loops

        let loop = try #require(loops.first)
        #expect(loops.count == 1)
        #expect((3_000...3_700).contains(loop.estimatedArea), "\(loop)")
    }

    @Test("Незамкнутая дуга (концы в 71 м): петли нет ни при каком R до 50 м")
    func arc() throws {
        var grid = DetectorGrid()
        grid.radiusFactor = [0]
        grid.minRadiusMeters = [15, 20, 25, 50]
        let run = try replay(SyntheticRuns.arc(), grid: grid)

        #expect(run.variants.count == 4)
        #expect(run.variants.allSatisfy { $0.loops.isEmpty })
    }

    @Test("Меньше R — позже замыкание: при R = 15 м полоса 12 м замыкается, при R = 10 м — нет")
    func radiusGrid() throws {
        var grid = DetectorGrid()
        grid.radiusFactor = [0]
        grid.minRadiusMeters = [10, 15]
        let variants = try replay(SyntheticRuns.strip(), grid: grid).variants

        #expect(variants.map(\.detector.minRadiusMeters) == [10, 15])
        #expect(variants[0].loops.isEmpty, "\(variants[0].loops)")
        #expect(variants[1].loops.count == 1)
    }

    @Test("Сетка: все сочетания, R_мин больше R_макс пропускается")
    func grid() {
        var grid = DetectorGrid()
        grid.minRadiusMeters = [15, 20, 60]
        grid.minEstimatedAreaSquareMeters = [1_000, 2_500]

        #expect(grid.settings.count == 4)
        #expect(grid.settings.allSatisfy { $0.minRadiusMeters <= $0.maxRadiusMeters })
        #expect(DetectorGrid().settings == [LoopDetectorSettings()])
    }

    @Test("Параметры командной строки")
    func options() throws {
        let options = try ReplayOptions.parse(["data.json", "--r-min", "15,20,25", "--r-factor", "0", "--newcomer"])

        #expect(options.input == "data.json")
        #expect(options.grid.minRadiusMeters == [15, 20, 25])
        #expect(options.grid.radiusFactor == [0])
        #expect(options.newcomer)
        #expect(throws: UsageError.self) { try ReplayOptions.parse(["data.json", "--r-min", "двадцать"]) }
        #expect(throws: UsageError.self) { try ReplayOptions.parse(["data.json", "--unknown", "1"]) }
        #expect(throws: UsageError.self) { try ReplayOptions.parse(["--r-min", "20"]) }
        #expect(throws: UsageError.self) { try ReplayOptions.parse(["data.json", "--r-min", "60"]) }
    }

    @Test("«Мои данные» читаются как есть: лишние разделы и поля пропускаются")
    func decodesExport() throws {
        let json = """
            {"exportedAtMs":1790000100000,"profile":{"id":"5b0c","displayName":"Бегун-1234"},"sessions":[],
             "runs":[{"id":"8d3f","league":"run","source":"live","startedAtMs":1790000000000,"endedAtMs":null,
               "status":"finished","deviceId":"a1","appVersion":"0.1.0 (1)","motionAuthorized":true,
               "acceptedMeters":null,"fogNewCells":null,"visitedParcels":null,"pointsErasedAtMs":null,
               "points":[{"seq":0,"t":1790000000000,"lat":52.0976,"lon":23.688,"acc":4.5,"speed":null,"flags":0},
                         {"seq":1,"t":1790000001000,"lat":52.09761,"lon":23.68801,"acc":5,"speed":1.4,"flags":1}],
               "motion":[{"t":1790000000000,"activity":"walking"}],
               "steps":[{"start":1790000000000,"end":1790000001000,"steps":2},
                        {"start":1790000001000,"end":1790000002000,"steps":null}]}],
             "captures":[{"id":"c1","runId":"8d3f","league":"run","status":"rejected","rejectCode":"too_small",
               "receivedAtMs":1790000001500,"effectiveAtMs":1790000001000,"areaSquareMeters":null,
               "rolledBackAtMs":null}],
             "land":[],"fog":[],"privacyZones":[],"rankings":[]}
            """
        let data = try JSONDecoder().decode(MyData.self, from: Data(json.utf8))
        let run = try #require(data.runs.first)

        #expect(run.points.map(\.acc) == [4.5, 5])
        #expect(run.points.map(\.speed) == [nil, 1.4])
        #expect(run.motion == [MyData.Motion(t: 1_790_000_000_000, activity: .walking)])
        #expect(run.steps.last?.steps == nil)
        #expect(data.captures?.first?.rejectCode == "too_small")

        let output = LoopReplay.run(data, input: "x.json", detectors: [LoopDetectorSettings()])
        #expect(output.runs.first?.serverCaptures.count == 1)
    }

    @Test("Пропуск номеров: разбирается только начало следа без дыр, как на сервере")
    func gap() throws {
        var run = SyntheticRuns.arc()
        run.points.removeSubrange(10..<12)
        let replayed = try replay(run)

        #expect(replayed.points.count == 10)
        #expect(replayed.pointsAfterGap == SyntheticRuns.arc().points.count - 12)
    }

    @Test("Выход читается обратно без потерь")
    func roundTrip() throws {
        let output = LoopReplay.run(SyntheticRuns.myData(), input: "synthetic.json", detectors: DetectorGrid().settings)
        let decoded = try JSONDecoder().decode(LoopReplayOutput.self, from: try LoopReplay.encode(output))

        #expect(decoded == output)
        #expect(output.runs.count == 3)
    }
}
