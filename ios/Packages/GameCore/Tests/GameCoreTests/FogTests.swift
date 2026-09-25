import Foundation
import Testing

@testable import GameCore

@Suite("Туман «Исследования»: сетка G22 и открытие (PLAN.md §3.10)")
struct FogTests {
    /// Эталоны посчитаны независимо на Python по формулам веб-меркатора.
    @Test("Клетка и тайл точки в Бресте, размер клетки 5,87 м")
    func gridReference() {
        let brest = Coordinate(latitude: 52.0976, longitude: 23.6880)

        let cell = FogGrid.cell(of: brest)

        #expect(cell == FogCell(x: 2_373_137, y: 1_383_592))
        #expect(cell.tile == FogTileKey(x: 9_270, y: 5_404))
        #expect(abs(FogGrid.cellSizeMeters(atLatitude: 52.0976) - 5.8696) < 0.0005)
    }

    @Test("Центр клетки лежит в этой же клетке")
    func centerRoundTrip() {
        for cell in [FogCell(x: 2_373_137, y: 1_383_592), FogCell(x: 2_373_400, y: 1_383_001)] {
            #expect(FogGrid.cell(of: FogGrid.center(of: cell)) == cell)
        }
    }

    @Test("Одна точка открывает круг 25 м: ≈ π·25² / 5,87² ≈ 57 клеток")
    func singlePoint() {
        var layer = FogLayer()
        layer.reveal(around: TrackSimulator.origin)

        #expect(layer.cellCount >= 50 && layer.cellCount <= 64)
        let circle = Double.pi * 25 * 25
        #expect(abs(layer.areaSquareMeters - circle) / circle < 0.12)
    }

    @Test("Километр по диагонали открывает полосу 50 м: ≈ 5,2 га")
    func diagonalKilometre() {
        var sim = TrackSimulator()
        var layer = FogLayer()
        // Путь под углом к сетке: клетки на краях полосы попадают в неё «в среднем», и площадь почти точная.
        let points = sim.straight(from: (0, 0), heading: (0.6, 0.8), speed: 3, seconds: 334)
        for (a, b) in zip(points, points.dropFirst()) {
            layer.reveal(from: a.coordinate, to: b.coordinate)
        }

        let expected = 50.0 * 1_000 + Double.pi * 25 * 25
        #expect(abs(layer.areaSquareMeters - expected) / expected < 0.05, "площадь \(layer.areaSquareMeters)")
    }

    @Test("Путь вдоль строки сетки: полоса ровно 8 или 9 клеток (дискретность 5,87 м)")
    func gridAlignedStripWidth() {
        var sim = TrackSimulator()
        var layer = FogLayer()
        let points = sim.straight(from: (0, 0), speed: 3, seconds: 334)
        for (a, b) in zip(points, points.dropFirst()) {
            layer.reveal(from: a.coordinate, to: b.coordinate)
        }

        // Середина пути: сколько клеток открыто в столбце под ней.
        let middle = FogGrid.cell(of: points[167].coordinate)
        let column = (-12...12).filter { layer.isRevealed(FogCell(x: middle.x, y: middle.y + $0)) }.count
        #expect(column == 8 || column == 9)
    }

    @Test("Разрыв длиннее порога не закрашивается: только круги на концах")
    func gapIsNotFilled() {
        let sim = TrackSimulator()
        let a = sim.coordinate(east: 0, north: 0)
        let b = sim.coordinate(east: 150, north: 0)

        var walking = FogLayer()
        walking.reveal(from: a, to: b, maxGap: 100)
        var cycling = FogLayer()
        cycling.reveal(from: a, to: b, maxGap: 200)

        let twoCircles = 2 * Double.pi * 25 * 25
        #expect(abs(walking.areaSquareMeters - twoCircles) / twoCircles < 0.15)
        #expect(cycling.areaSquareMeters > 150 * 50)
    }

    @Test("Повторный проход ничего нового не открывает, новый район — открывает")
    func newCells() {
        var sim = TrackSimulator()
        var yesterday = FogLayer()
        let route = sim.straight(from: (0, 0), speed: 3, seconds: 100)
        for (a, b) in zip(route, route.dropFirst()) {
            yesterday.reveal(from: a.coordinate, to: b.coordinate)
        }

        var today = yesterday
        for (a, b) in zip(route, route.dropFirst()) {
            today.reveal(from: a.coordinate, to: b.coordinate)
        }
        #expect(today.newCellCount(comparedTo: yesterday) == 0)

        today.reveal(around: sim.coordinate(east: 0, north: 500))
        #expect(today.newCellCount(comparedTo: yesterday) > 40)
    }

    @Test("Объединение слоёв не зависит от порядка")
    func unionIsCommutative() {
        let sim = TrackSimulator()
        var a = FogLayer()
        a.reveal(around: sim.coordinate(east: 0, north: 0))
        var b = FogLayer()
        b.reveal(around: sim.coordinate(east: 20, north: 0))

        var ab = a
        ab.formUnion(b)
        var ba = b
        ba.formUnion(a)

        #expect(ab == ba)
        #expect(ab.cellCount < a.cellCount + b.cellCount)
    }

    @Test("Площадь новых клеток — число новых клеток × площадь клетки; пустой старый тайл — все клетки новые")
    func newAreaAgainstAllTime() throws {
        let sim = TrackSimulator()
        var old = FogLayer()
        old.reveal(around: sim.coordinate(east: 0, north: 0))
        var run = old
        run.reveal(around: sim.coordinate(east: 40, north: 0))
        let key = try #require(run.tiles.keys.first)
        #expect(run.tiles.count == 1)
        let emptyFromCache = try #require(FogTileBits(words: []))
        #expect(emptyFromCache == FogTileBits())
        #expect(FogTileBits(words: [1, 2, 3]) == nil)

        let againstOld = run.newArea(comparedTo: old.tiles)
        let againstEmpty = run.newArea(comparedTo: [key: emptyFromCache])
        let unknown = run.newArea(comparedTo: [:])

        #expect(againstOld.cells == run.newCellCount(comparedTo: old))
        #expect(againstOld.cells > 0 && againstOld.cells < run.cellCount)
        #expect(abs(againstOld.squareMeters - Double(againstOld.cells) * key.cellAreaSquareMeters) < 1e-6)
        #expect(abs(key.cellAreaSquareMeters - 5.87 * 5.87) < 0.1)
        #expect(againstOld.unknownTiles.isEmpty)
        #expect(againstEmpty.cells == run.cellCount)
        #expect(abs(againstEmpty.squareMeters - run.areaSquareMeters) < 1e-6)
        #expect(unknown == FogNewArea(cells: 0, squareMeters: 0, unknownTiles: [key]))
    }
}
