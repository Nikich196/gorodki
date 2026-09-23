import Testing

@testable import GameCore

@Suite("Геодезия: расстояния")
struct GeodesyTests {
    @Test("Один градус по меридиану — 111 195,08 м (радиус 6 371 008,8 м)")
    func oneDegreeOfLatitude() {
        let south = Coordinate(latitude: 52, longitude: 23.7)
        let north = Coordinate(latitude: 53, longitude: 23.7)

        let distance = Geodesy.distance(from: south, to: north)

        #expect(abs(distance - 111_195.08) < 0.01)
    }

    @Test("До противоположной точки Земли — половина окружности")
    func antipodalPoints() {
        let distance = Geodesy.distance(
            from: Coordinate(latitude: 0, longitude: 0),
            to: Coordinate(latitude: 0, longitude: 180)
        )

        #expect(abs(distance - .pi * Geodesy.meanEarthRadius) < 0.001)
    }

    @Test("Расстояние от точки до неё самой — ноль, и оно симметрично")
    func zeroAndSymmetric() {
        let brest = Coordinate(latitude: 52.0976, longitude: 23.7341)
        let nearby = Coordinate(latitude: 52.1012, longitude: 23.7420)

        #expect(Geodesy.distance(from: brest, to: brest) == 0)
        #expect(Geodesy.distance(from: brest, to: nearby) == Geodesy.distance(from: nearby, to: brest))
    }

    @Test("Координаты вне пределов и NaN — недопустимы")
    func validity() {
        #expect(Coordinate(latitude: 52.1, longitude: 23.7).isValid)
        #expect(!Coordinate(latitude: 91, longitude: 0).isValid)
        #expect(!Coordinate(latitude: 0, longitude: -180.5).isValid)
        #expect(!Coordinate(latitude: .nan, longitude: 0).isValid)
    }
}
