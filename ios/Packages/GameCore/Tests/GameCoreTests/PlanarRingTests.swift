import Testing

@testable import GameCore

@Suite("Контур на плоскости: площадь и периметр")
struct PlanarRingTests {
    let square = PlanarRing([
        PlanarPoint(east: 0, north: 0),
        PlanarPoint(east: 100, north: 0),
        PlanarPoint(east: 100, north: 100),
        PlanarPoint(east: 0, north: 100),
    ])

    @Test("Квадрат 100 × 100 м — 10 000 м², обход против часовой стрелки даёт плюс")
    func squareCounterClockwise() {
        #expect(square.signedArea == 10_000)
        #expect(square.area == 10_000)
        #expect(square.perimeter == 400)
    }

    @Test("Обход по часовой стрелке меняет знак, но не площадь")
    func squareClockwise() {
        let clockwise = PlanarRing(square.vertices.reversed())

        #expect(clockwise.signedArea == -10_000)
        #expect(clockwise.area == 10_000)
    }

    @Test("Прямоугольный треугольник с катетами 30 и 40 м — 600 м²")
    func triangle() {
        let triangle = PlanarRing([
            PlanarPoint(east: 0, north: 0),
            PlanarPoint(east: 30, north: 0),
            PlanarPoint(east: 0, north: 40),
        ])

        #expect(triangle.area == 600)
        #expect(triangle.perimeter == 120)
    }

    @Test("Пробежка «туда-обратно» по одной линии площади не даёт")
    func outAndBack() {
        let outAndBack = PlanarRing([
            PlanarPoint(east: 0, north: 0),
            PlanarPoint(east: 250, north: 10),
            PlanarPoint(east: 500, north: 20),
            PlanarPoint(east: 250, north: 10),
        ])

        #expect(outAndBack.area == 0)
    }

    @Test("Меньше трёх вершин — площадь ноль")
    func degenerate() {
        #expect(PlanarRing([]).area == 0)
        #expect(PlanarRing([PlanarPoint(east: 1, north: 1), PlanarPoint(east: 5, north: 5)]).area == 0)
    }

    @Test("Сотки и гектары")
    func units() {
        #expect(AreaUnits.sotki(fromSquareMeters: 12_480) == 124.8)
        #expect(AreaUnits.hectares(fromSquareMeters: 12_000) == 1.2)
    }
}
