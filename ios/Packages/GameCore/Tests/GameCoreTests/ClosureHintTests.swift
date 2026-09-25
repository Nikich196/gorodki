import Foundation
import Testing

@testable import GameCore

@Suite("Подсказка «до замыкания»: цель — начало открытой петли (docs/architecture/run-hud.md)")
struct ClosureHintTests {
    /// Генератор с зерном: одни и те же «случайные» прогулки на любой машине.
    struct SplitMix: RandomNumberGenerator {
        var state: UInt64

        mutating func next() -> UInt64 {
            state &+= 0x9E37_79B9_7F4A_7C15
            var z = state
            z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
            z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
            return z ^ (z >> 31)
        }
    }

    /// Точки через каждые `step` метров от вершины к вершине, последняя вершина — тоже точка.
    private func path(
        _ sim: inout TrackSimulator, _ vertices: [(Double, Double)], step: Double = 3, accuracy: Double = 5,
        noise: Double = 0, rng: inout SplitMix
    ) -> [TrackPoint] {
        var points: [TrackPoint] = []
        for (a, b) in zip(vertices, vertices.dropFirst()) {
            let length = ((b.0 - a.0) * (b.0 - a.0) + (b.1 - a.1) * (b.1 - a.1)).squareRoot()
            let count = max(1, Int((length / step).rounded(.up)))
            for i in 0..<count {
                let t = Double(i) / Double(count)
                let dx = noise > 0 ? Double.random(in: -noise...noise, using: &rng) : 0
                let dy = noise > 0 ? Double.random(in: -noise...noise, using: &rng) : 0
                points.append(
                    sim.next(east: a.0 + (b.0 - a.0) * t + dx, north: a.1 + (b.1 - a.1) * t + dy, accuracy: accuracy))
            }
        }
        if let last = vertices.last {
            points.append(sim.next(east: last.0, north: last.1, accuracy: accuracy))
        }
        return points
    }

    private func angle(_ a: Double, from b: Double) -> Double {
        let d = abs(a - b).truncatingRemainder(dividingBy: 360)
        return min(d, 360 - d)
    }

    /// Подсказка после каждой точки; заявки детектора — рядом.
    private func feed(_ points: [TrackPoint], into detector: inout LoopDetector) -> [(LoopClaim?, ClosureHint)] {
        points.map { point in
            let claim = detector.add(point)
            return (claim, detector.closureHint())
        }
    }

    @Test("Квартал 150 × 150 м с шумом GPS: в углах — состояние, расстояние и азимут к началу")
    func noisyBlock() throws {
        var rng = SplitMix(state: 7)
        var sim = TrackSimulator()
        var detector = LoopDetector()
        let corner1 = path(&sim, [(0, 0), (150, 0)], noise: 1.5, rng: &rng)
        let corner2 = path(&sim, [(150, 3), (150, 150)], noise: 1.5, rng: &rng)
        let corner3 = path(&sim, [(147, 150), (0, 150)], noise: 1.5, rng: &rng)

        _ = feed(corner1, into: &detector)
        // 150 м по прямой: пути хватает (шум его только удлиняет), площади нет — «нужно свернуть».
        #expect(detector.closureHint() == .needsTurn(targetSeq: 0))

        let afterTwo = feed(corner2, into: &detector)
        #expect(afterTwo.allSatisfy { $0.0 == nil })
        guard case .canClose(let atCorner2) = detector.closureHint() else {
            Issue.record("после двух сторон квартала подсказка не «можно замкнуть»: \(detector.closureHint())")
            return
        }
        #expect(atCorner2.seq == 0)
        #expect(abs(atCorner2.distanceMeters - (150 * 2.0.squareRoot() - 20)) < 5)
        #expect(angle(atCorner2.bearingDegrees, from: 225) < 3)
        #expect(abs(atCorner2.estimatedAreaSquareMeters - 150 * 150 / 2) < 900)
        #expect(!atCorner2.belowServerMinimum && !atCorner2.tooLarge)
        #expect(abs(atCorner2.coordinate.latitude - TrackSimulator.origin.latitude) < 1e-4)

        _ = feed(corner3, into: &detector)
        guard case .canClose(let atCorner3) = detector.closureHint() else {
            Issue.record("в третьем углу подсказка не «можно замкнуть»: \(detector.closureHint())")
            return
        }
        #expect(abs(atCorner3.distanceMeters - (150 - 20)) < 5)
        #expect(angle(atCorner3.bearingDegrees, from: 180) < 3)
        #expect(abs(atCorner3.estimatedAreaSquareMeters - 150 * 150) < 1_500)
    }

    @Test("Путь меньше 150 м — «нужен путь» с верным остатком; без точек — «нет следа»")
    func needsPath() throws {
        var rng = SplitMix(state: 1)
        var sim = TrackSimulator()
        var detector = LoopDetector()
        #expect(detector.closureHint() == .noTrail)

        _ = feed(path(&sim, [(0, 0), (120, 0)], rng: &rng), into: &detector)

        guard case .needsPath(let target, let remaining) = detector.closureHint() else {
            Issue.record("не «нужен путь»: \(detector.closureHint())")
            return
        }
        #expect(target == 0)
        #expect(abs(remaining - 30) < 0.01)
    }

    @Test("Прямая и «туда-обратно» — «нужно свернуть»: стрелки назад нет")
    func straightAndOutAndBack() {
        var rng = SplitMix(state: 2)
        var sim = TrackSimulator()
        var straight = LoopDetector()
        _ = feed(path(&sim, [(0, 0), (300, 0)], rng: &rng), into: &straight)
        #expect(straight.closureHint() == .needsTurn(targetSeq: 0))

        var backSim = TrackSimulator()
        var outAndBack = LoopDetector()
        let found = feed(path(&backSim, [(0, 0), (200, 0), (10, 2)], rng: &rng), into: &outAndBack)
        #expect(found.allSatisfy { $0.0 == nil })
        #expect(outAndBack.closureHint() == .needsTurn(targetSeq: 0))
    }

    @Test("После заявки — снова «нужен путь», цель — точка замыкания; после разрыва — от первой точки нового отрезка")
    func targetMovesAfterClaimAndBreak() throws {
        var rng = SplitMix(state: 3)
        var sim = TrackSimulator()
        var detector = LoopDetector()
        let steps = feed(
            path(&sim, [(0, 0), (100, 0), (100, 100), (0, 100), (0, 0), (60, 0)], rng: &rng), into: &detector)

        let index = try #require(steps.firstIndex { $0.0 != nil })
        let claim = try #require(steps[index].0)
        #expect(steps[index].1 == .needsPath(targetSeq: claim.endSeq, remainingMeters: 150))
        // Дальше по кварталу — «нужен путь» от точки замыкания, а не от начала отрезка.
        guard case .needsPath(let target, let remaining) = detector.closureHint() else {
            Issue.record("после заявки не «нужен путь»: \(detector.closureHint())")
            return
        }
        #expect(target == claim.endSeq)
        #expect(remaining < 150 && remaining > 60)

        detector.reset()
        #expect(detector.closureHint() == .noTrail)
        let first = sim.next(east: 60, north: 5)
        _ = detector.add(first)
        #expect(detector.closureHint() == .needsPath(targetSeq: first.seq, remainingMeters: 150))
    }

    @Test("Согласие с детектором: на случайных прогулках «можно замкнуть» больше 0 м везде, где петля не заявлена")
    func agreesWithDetectorOnRandomWalks() {
        var canClose = 0
        var claims = 0
        for seed in UInt64(1)...5 {
            var rng = SplitMix(state: seed)
            var sim = TrackSimulator()
            var detector = LoopDetector()
            var east = 0.0
            var north = 0.0
            var heading = 0.0
            for _ in 0..<3_000 {
                heading += Double.random(in: -0.35...0.35, using: &rng)
                let step = Double.random(in: 2...6, using: &rng)
                east += step * sin(heading)
                north += step * cos(heading)
                let point = sim.next(east: east, north: north, accuracy: Double.random(in: 3...25, using: &rng))
                if detector.add(point) != nil {
                    claims += 1
                    continue
                }
                if case .canClose(let target) = detector.closureHint() {
                    canClose += 1
                    #expect(target.distanceMeters > 0, "зерно \(seed), точка \(point.seq)")
                }
            }
        }
        // Проверка не пустая: прогулки и замыкают петли, и подолгу бывают в «можно замкнуть».
        #expect(claims > 20)
        #expect(canClose > 1_000)
    }

    @Test("Возврат к началу шагами по 5 м: на точке перед замыканием подсказка не больше шага")
    func lastStepBeforeClosure() throws {
        var rng = SplitMix(state: 4)
        var sim = TrackSimulator()
        var detector = LoopDetector()
        let steps = feed(
            path(&sim, [(0, 0), (100, 0), (100, 100), (0, 100), (0, 0)], step: 5, rng: &rng), into: &detector)

        let index = try #require(steps.firstIndex { $0.0 != nil })
        guard case .canClose(let before) = steps[index - 1].1 else {
            Issue.record("перед замыканием подсказка не «можно замкнуть»: \(steps[index - 1].1)")
            return
        }
        #expect(before.distanceMeters <= 5 + 1e-6)
        #expect(before.distanceMeters > 0)
    }

    @Test("R по формуле §3.2: точность 5 м → R = clamp(11,5; 20; 50) = 20 м — возврат на 15 м от начала замыкает")
    func closingRadiusIsClamped() {
        var rng = SplitMix(state: 5)
        var sim = TrackSimulator()
        var detector = LoopDetector()
        let points = path(&sim, [(0, 0), (100, 0), (100, 100), (0, 100), (0, 25)], rng: &rng)
        #expect(points.compactMap { detector.add($0) }.isEmpty)

        let claim = detector.add(sim.next(east: 0, north: 15))

        #expect(claim?.startSeq == 0)
        #expect(claim?.closure == .proximity)
    }

    @Test("Пороги площади сервера: меньше минимума — флаг, ровно минимум — нет; больше 3,5 км² — «слишком большая»")
    func areaFlags() throws {
        var rng = SplitMix(state: 6)
        var sim = TrackSimulator()
        var detector = LoopDetector()
        // Прямоугольный треугольник 150 × 33,32 м: площадь с хордой ≈ 2 499 м².
        _ = feed(path(&sim, [(0, 0), (150, 0), (150, 33.32)], rng: &rng), into: &detector)
        guard case .canClose(let small) = detector.closureHint() else {
            Issue.record("не «можно замкнуть»: \(detector.closureHint())")
            return
        }
        #expect(abs(small.estimatedAreaSquareMeters - 2_499) < 0.5)
        #expect(small.belowServerMinimum && !small.tooLarge)
        // Ровно на пороге — не «меньше порога».
        let exact = CaptureAreaLimits(
            minAreaSquareMeters: small.estimatedAreaSquareMeters, maxAreaSquareMeters: 3_500_000)
        guard case .canClose(let atLimit) = detector.closureHint(limits: exact) else { return }
        #expect(!atLimit.belowServerMinimum)

        var bigSim = TrackSimulator()
        var big = LoopDetector()
        _ = feed(path(&bigSim, [(0, 0), (3_000, 0), (3_000, 2_400)], step: 10, rng: &rng), into: &big)
        guard case .canClose(let large) = big.closureHint() else {
            Issue.record("большая петля: не «можно замкнуть»: \(big.closureHint())")
            return
        }
        #expect(large.estimatedAreaSquareMeters > 3_500_000)
        #expect(large.tooLarge && !large.belowServerMinimum)
    }

    /// Куда смотрит цель из последней точки: обход трёх сторон квадрата 200 м — начало остаётся в этой стороне.
    enum Direction: Double, CaseIterable, Sendable, CustomTestStringConvertible {
        case north = 0
        case east = 90
        case south = 180
        case west = 270

        var vertices: [(Double, Double)] {
            switch self {
            case .north: [(0, 0), (200, 0), (200, -200), (0, -200)]
            case .east: [(0, 0), (0, 200), (-200, 200), (-200, 0)]
            case .south: [(0, 0), (200, 0), (200, 200), (0, 200)]
            case .west: [(0, 0), (0, -200), (200, -200), (200, 0)]
            }
        }

        var testDescription: String { "\(self)" }
    }

    @Test("Азимут на цель: север, восток, юг, запад", arguments: Direction.allCases)
    func bearing(direction: Direction) {
        let vertices = direction.vertices
        let expected = direction.rawValue
        var rng = SplitMix(state: 8)
        var sim = TrackSimulator()
        var detector = LoopDetector()
        _ = feed(path(&sim, vertices, rng: &rng), into: &detector)

        guard case .canClose(let target) = detector.closureHint() else {
            Issue.record("не «можно замкнуть»: \(detector.closureHint())")
            return
        }
        #expect(angle(target.bearingDegrees, from: expected) < 0.01)
        #expect(target.bearingDegrees >= 0 && target.bearingDegrees < 360)
        #expect(abs(target.distanceMeters - 180) < 0.01)
    }

    @Test("4 часа с точкой в секунду (14 400 точек): подсказка на каждой точке — только префиксные суммы детектора")
    func fourHourRun() {
        var rng = SplitMix(state: 9)
        var sim = TrackSimulator()
        var detector = LoopDetector()
        var east = 0.0
        var north = 0.0
        var heading = 0.0
        var hints = 0
        for _ in 0..<14_400 {
            heading += Double.random(in: -0.2...0.2, using: &rng)
            east += 3 * sin(heading)
            north += 3 * cos(heading)
            _ = detector.add(sim.next(east: east, north: north))
            if detector.closureHint() != .noTrail {
                hints += 1
            }
        }
        #expect(hints == 14_400)
    }
}
