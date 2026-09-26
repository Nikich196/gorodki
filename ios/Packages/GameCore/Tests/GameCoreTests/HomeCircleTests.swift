import Foundation
import Testing

@testable import GameCore

@Suite("Круг «Дома»: только показ поверх своего тумана (PLAN.md §3.10)")
struct HomeCircleTests {
    private static let home = Coordinate(latitude: 52.0976, longitude: 23.7341)

    @Test("Круг 500 м — площадь ≈ π·500² (≈ 78,5 га) с точностью сетки, центр открыт")
    func circleArea() {
        var layer = FogLayer()
        layer.reveal(around: Self.home, radius: HomeCircle.radiusMeters)
        let circle = Double.pi * 500 * 500
        #expect(abs(layer.areaSquareMeters - circle) / circle < 0.02)
        #expect(HomeCircle.fog(around: Self.home) == layer.tiles)
        #expect(layer.isRevealed(FogGrid.cell(of: Self.home)))
    }

    @Test("Показ — объединение с туманом сервера по словам; сам туман сервера не меняется")
    func displayIsUnion() throws {
        var server = FogLayer()
        // Своя тропинка у края круга и в стороне от него: тайлы частично общие.
        server.reveal(
            from: Coordinate(latitude: 52.0976, longitude: 23.7200),
            to: Coordinate(latitude: 52.1150, longitude: 23.7341), radius: 25, maxGap: 5_000)
        let before = server.tiles
        let home = HomeCircle.fog(around: Self.home)

        let shown = HomeCircle.display(server.tiles, home: home)

        #expect(server.tiles == before)
        #expect(Set(shown.keys) == Set(server.tiles.keys).union(home.keys))
        for key in shown.keys {
            let own = server.tiles[key] ?? FogTileBits()
            let circle = home[key] ?? FogTileBits()
            let bits = try #require(shown[key])
            #expect(bits.words == zip(own.words, circle.words).map { $0 | $1 })
        }
        #expect(HomeCircle.display(server.tiles, home: [:]) == server.tiles)
    }

    @Test("Негодная точка или нулевой радиус — круга нет")
    func invalidHome() {
        #expect(HomeCircle.fog(around: Coordinate(latitude: 95, longitude: 0)).isEmpty)
        #expect(HomeCircle.fog(around: Self.home, radius: 0).isEmpty)
    }
}
