import Foundation
import Testing

@testable import GameCore

@Suite("Локальная плоскость «восток — север»")
struct LocalTangentPlaneTests {
    /// Примерно центр Бреста.
    let brest = Coordinate(latitude: 52.0976, longitude: 23.7341)

    @Test("Начало координат переходит в (0; 0)")
    func originIsZero() {
        let plane = LocalTangentPlane(origin: brest)

        let point = plane.project(brest)

        #expect(point.east == 0)
        #expect(point.north == 0)
    }

    @Test(
        "Туда и обратно — те же координаты",
        arguments: [
            PlanarPoint(east: 2_000, north: 0),
            PlanarPoint(east: 0, north: -2_000),
            PlanarPoint(east: -1_500, north: 1_500),
            PlanarPoint(east: 0.1, north: 0.1),
        ]
    )
    func roundTrip(point: PlanarPoint) {
        let plane = LocalTangentPlane(origin: brest)

        let back = plane.project(plane.unproject(point))

        #expect(abs(back.east - point.east) < 1e-6)
        #expect(abs(back.north - point.north) < 1e-6)
    }

    @Test("Расстояние на плоскости совпадает с гаверсинусом в пределах 0,5 %")
    func planarDistanceMatchesHaversine() {
        let plane = LocalTangentPlane(origin: brest)
        let targets = [
            Coordinate(latitude: 52.1066, longitude: 23.7341),  // ~1 км на север
            Coordinate(latitude: 52.0976, longitude: 23.7488),  // ~1 км на восток
            Coordinate(latitude: 52.0913, longitude: 23.7240),  // на юго-запад
        ]

        for target in targets {
            let point = plane.project(target)
            let planar = (point.east * point.east + point.north * point.north).squareRoot()
            let haversine = Geodesy.distance(from: brest, to: target)

            #expect(abs(planar - haversine) / haversine < 0.005)
        }
    }

    @Test("Площадь прямоугольника 0,01° × 0,01° совпадает со сферической формулой в пределах 0,5 %")
    func rectangleAreaMatchesSphere() {
        let plane = LocalTangentPlane(origin: brest)
        let south = 52.09
        let north = 52.10
        let west = 23.72
        let east = 23.73
        let corners = [
            Coordinate(latitude: south, longitude: west),
            Coordinate(latitude: south, longitude: east),
            Coordinate(latitude: north, longitude: east),
            Coordinate(latitude: north, longitude: west),
        ]

        let planarArea = PlanarRing(corners.map(plane.project)).area
        // Площадь «прямоугольника» на сфере: R² · Δλ · (sin φ₂ − sin φ₁).
        let radius = Geodesy.meanEarthRadius
        let sphericalArea =
            radius * radius * (east - west) * .pi / 180
            * (sin(north * .pi / 180) - sin(south * .pi / 180))

        #expect(abs(planarArea - sphericalArea) / sphericalArea < 0.005)
    }
}
