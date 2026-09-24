import Testing

@testable import GameCore

@Suite("Касание участка на карте")
struct ParcelHitTestTests {
    private let plane = LocalTangentPlane(origin: Coordinate(latitude: 52.0976, longitude: 23.6880))

    /// Прямоугольник на плоскости (метры от начала), замкнутый повтором первой точки — как кольцо сервера.
    private func rect(east: Double, north: Double, width: Double, height: Double) -> [Coordinate] {
        [(0.0, 0.0), (width, 0), (width, height), (0, height), (0, 0)].map {
            plane.unproject(PlanarPoint(east: east + $0.0, north: north + $0.1))
        }
    }

    private func at(_ east: Double, _ north: Double) -> Coordinate {
        plane.unproject(PlanarPoint(east: east, north: north))
    }

    @Test("Внутри участка — он; в его дыре — не он; далеко от всех — никто")
    func insideHoleOutside() {
        let parcel = ParcelShape(
            exterior: rect(east: 0, north: 0, width: 100, height: 100),
            holes: [rect(east: 30, north: 30, width: 40, height: 40)])

        #expect(ParcelHitTest.parcel(at: at(10, 10), tolerance: 5, among: [parcel]) == 0)
        #expect(ParcelHitTest.parcel(at: at(50, 50), tolerance: 5, among: [parcel]) == nil)  // центр дыры: до края 20 м
        #expect(ParcelHitTest.parcel(at: at(300, 300), tolerance: 5, among: [parcel]) == nil)
    }

    @Test("Рядом с узким участком — он, если граница ближе допуска касания; дальше — никто")
    func nearThinParcel() {
        let strip = ParcelShape(exterior: rect(east: 0, north: 0, width: 200, height: 2))

        #expect(ParcelHitTest.parcel(at: at(50, 6), tolerance: 5, among: [strip]) == 0)  // 4 м от края
        #expect(ParcelHitTest.parcel(at: at(50, 9), tolerance: 5, among: [strip]) == nil)  // 7 м
    }

    @Test("Между двумя участками — ближайший; внутри одного — он, даже если край другого ближе допуска")
    func nearestWins() {
        let left = ParcelShape(exterior: rect(east: 0, north: 0, width: 50, height: 50))
        let right = ParcelShape(exterior: rect(east: 58, north: 0, width: 50, height: 50))

        #expect(ParcelHitTest.parcel(at: at(55, 25), tolerance: 5, among: [left, right]) == 1)  // 3 м до правого
        #expect(ParcelHitTest.parcel(at: at(52, 25), tolerance: 5, among: [left, right]) == 0)  // 2 м до левого
        #expect(ParcelHitTest.parcel(at: at(49, 25), tolerance: 20, among: [left, right]) == 0)
    }

    @Test("Возле угла — расстояние до угла, а не до продолжения стороны: 14 м по диагонали при допуске 12 м — мимо")
    func cornerDistance() {
        let square = ParcelShape(exterior: rect(east: 0, north: 0, width: 50, height: 50))

        #expect(ParcelHitTest.parcel(at: at(60, 60), tolerance: 12, among: [square]) == nil)  // до сторон-прямых 10 м
        #expect(ParcelHitTest.parcel(at: at(58, 58), tolerance: 12, among: [square]) == 0)  // до угла 11,3 м
    }

    @Test("Кольцо сервера [широта, долгота, …] разбирается по парам; нечётный хвост отбрасывается")
    func serverRing() {
        let ring = ParcelShape.ring([52.1, 23.7, 52.1, 23.71, 52.11, 23.71, 52.1, 23.7, 99])
        #expect(ring.count == 4)
        #expect(ring[1] == Coordinate(latitude: 52.1, longitude: 23.71))
    }
}
