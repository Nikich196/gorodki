import Testing

@testable import GameCore

@Suite("Детектор петли (PLAN.md §3.2)")
struct LoopDetectorTests {
    private func claims(_ points: [TrackPoint], detector: inout LoopDetector) -> [LoopClaim] {
        points.compactMap { detector.add($0) }
    }

    @Test("Квартал 100 × 100 м: одна заявка от первой точки, площадь ≈ 1 га")
    func squareBlock() throws {
        var sim = TrackSimulator()
        var detector = LoopDetector()
        let points = sim.walk([(0, 0), (100, 0), (100, 100), (0, 100)])

        let found = claims(points, detector: &detector)

        let claim = try #require(found.first)
        #expect(found.count == 1)
        #expect(claim.startSeq == points[0].seq)
        #expect(claim.estimatedArea > 8_500 && claim.estimatedArea < 10_500)
    }

    @Test("При плотных точках петля замыкается сближением раньше, чем след успеет пересечь себя")
    func denseTrailClosesByProximity() throws {
        var sim = TrackSimulator()
        var detector = LoopDetector()
        let points = sim.walk([(0, -30), (0, 100), (100, 100), (100, 0), (-30, 0)], closed: false)

        let claim = try #require(claims(points, detector: &detector).first)

        #expect(claim.closure == .proximity)
        #expect(claim.estimatedArea > 8_000 && claim.estimatedArea < 11_500)
    }

    @Test("Редкие точки (велосипед, 1 Гц): длинный скачок через прежний след — заявка «пересечение»")
    func sparseTrailClosesByCrossing() throws {
        var sim = TrackSimulator()
        var detector = LoopDetector()
        var points = sim.walk([(0, -30), (0, 100), (100, 100), (100, 0), (60, 0)], closed: false)
        // Один скачок с (60; 0) на (−30; 0): прямо через первый отрезок, ближе 20 м к прежним точкам не подходя.
        points.append(sim.next(east: -30, north: 0))

        let claim = try #require(claims(points, detector: &detector).first)

        #expect(claim.closure == .crossing)
        #expect(claim.estimatedArea > 8_500 && claim.estimatedArea < 11_500)
    }

    @Test("Петля короче 150 м пути — не петля")
    func tooShort() {
        var sim = TrackSimulator()
        var detector = LoopDetector()
        #expect(claims(sim.walk([(0, 0), (30, 0), (30, 30), (0, 30)]), detector: &detector).isEmpty)
    }

    @Test("Туда и обратно по улице — заявки нет (площади ноль)")
    func outAndBack() {
        var sim = TrackSimulator()
        var detector = LoopDetector()
        #expect(claims(sim.walk([(0, 0), (300, 0), (0, 2)], closed: false), detector: &detector).isEmpty)
    }

    @Test("Два круга вокруг квартала — две заявки, вторая начинается после первой")
    func twoLaps() throws {
        var sim = TrackSimulator()
        var detector = LoopDetector()
        let square: [(Double, Double)] = [(0, 0), (100, 0), (100, 100), (0, 100)]
        let points = sim.walk(square) + sim.walk(square)

        let found = claims(points, detector: &detector)

        #expect(found.count == 2)
        let first = try #require(found.first)
        let second = try #require(found.last)
        #expect(second.startSeq >= first.endSeq)
    }

    @Test("Радиус замыкания зависит от точности: точные точки — не меньше 20 м")
    func closingRadius() {
        var sim = TrackSimulator()
        var detector = LoopDetector()
        // Возвращаемся в 30 м от старта: при точности 5 м R = 20 м — петля не замкнута.
        let points = sim.walk([(0, 0), (100, 0), (100, 100), (0, 100), (0, 30)], closed: false)
        #expect(claims(points, detector: &detector).isEmpty)

        var noisySim = TrackSimulator()
        var noisyDetector = LoopDetector()
        // При точности 20 м R = 1,62·√800 ≈ 46 м — замкнута.
        let noisy = noisySim.walk([(0, 0), (100, 0), (100, 100), (0, 100), (0, 30)], accuracy: 20, closed: false)
        #expect(claims(noisy, detector: &noisyDetector).count == 1)
    }

    @Test("После разрыва отрезка всё начинается заново")
    func resetForgetsTheTrail() {
        var sim = TrackSimulator()
        var detector = LoopDetector()
        let firstHalf = sim.walk([(0, 0), (100, 0), (100, 100)], closed: false)
        let secondHalf = sim.walk([(100, 100), (0, 100), (0, 0)], closed: false)

        _ = claims(firstHalf, detector: &detector)
        detector.reset()

        #expect(claims(secondHalf, detector: &detector).isEmpty)
    }
}
