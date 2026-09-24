import Testing

@testable import GameCore

/// Та же проекция, что у сервера (`Utm34Tests` в backend): эталоны посчитаны независимо библиотекой PROJ 9.8.1
/// (pyproj 3.8.0), EPSG:4326 → EPSG:32634. Точки — в Бресте и вокруг, плюс крайние случаи: пересечение осевого меридиана
/// с экватором и точка в 6° от осевого меридиана (там ошибка рядов больше всего).
@Suite("UTM 34N: как на сервере")
struct Utm34Tests {
    @Test(
        "Градусы → метры совпадают с PROJ до миллиметра",
        arguments: [
            (52.0930, 23.7314, 687_105.9496, 5_774_902.0559),
            (52.0830, 23.6560, 681_982.5253, 5_773_598.4011),
            (52.0976, 23.6880, 684_114.5597, 5_775_302.5525),
            (52.1100, 23.8300, 693_785.1757, 5_777_051.1033),
            (0.0, 21.0, 500_000.0000, 0.0000),
            (55.8, 27.0, 875_883.3442, 6_200_122.8124),
        ])
    func projectMatchesPROJ(latitude: Double, longitude: Double, easting: Double, northing: Double) {
        let point = Utm34.project(Coordinate(latitude: latitude, longitude: longitude))

        #expect(abs(point.east - easting) < 0.001)
        #expect(abs(point.north - northing) < 0.001)
    }

    @Test(
        "Метры → градусы → метры — та же точка",
        arguments: [
            PlanarPoint(east: 684_114.5597, north: 5_775_302.5525),
            PlanarPoint(east: 690_000, north: 5_780_000),
            PlanarPoint(east: 875_883.3442, north: 6_200_122.8124),
        ])
    func roundTrip(point: PlanarPoint) {
        let back = Utm34.project(Utm34.unproject(point))

        #expect(abs(back.east - point.east) < 0.001)
        #expect(abs(back.north - point.north) < 0.001)
    }

    @Test("Метры → градусы совпадают с PROJ (1e-8° ≈ 1 мм)")
    func unprojectMatchesPROJ() {
        let coordinate = Utm34.unproject(PlanarPoint(east: 684_114.5597, north: 5_775_302.5525))

        #expect(abs(coordinate.latitude - 52.0976) < 1e-8)
        #expect(abs(coordinate.longitude - 23.6880) < 1e-8)
    }
}
