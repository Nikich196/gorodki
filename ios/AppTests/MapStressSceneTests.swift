import Foundation
import GameCore
import Testing

@testable import Gorodki

/// Синтетическая карта стенда S4 (`MapStressScene`): стенд должен держать числа плана, а не мягче (PLAN.md §10:
/// «5 000 полигонов и туман на 300 тайлов»), и резать участки по тайлам, как сервер, — иначе на телефоне нечего проверять
/// в S13 («снимок границ без швов»).
@Suite("Стенд карты S4: участки по тайлам, вершины, туман на 300 тайлах")
struct MapStressSceneTests {
    /// Сцена строится один раз на все проверки: в отладочной сборке это заметное время.
    private static let arena = MapStressScene.make(preset: .arena, colors: 12)

    /// Площадь контура в метрах UTM — от центра Арены: в абсолютных метрах (миллионы) формула шнурования теряет точность.
    private static func area(_ ring: [PlanarPoint]) -> Double {
        let origin = Utm34.project(MapStressPreset.arena.center)
        return PlanarRing(ring.map { PlanarPoint(east: $0.east - origin.east, north: $0.north - origin.north) }).area
    }

    @Test("5 000 участков с вершинами как у настоящих кусков (в среднем 82) и не меньше 5 000 кусков")
    func parcelsAndVertices() {
        let scene = Self.arena
        #expect(scene.outlines.count == MapStressScene.parcelCount)
        #expect(scene.pieces.count >= MapStressScene.parcelCount)
        let average = Double(scene.outlines.map(\.ring.count).reduce(0, +)) / Double(scene.outlines.count)
        #expect(abs(average - Double(MapStressScene.averageVertices)) < 2, "в среднем вершин: \(average)")
        #expect(Set(scene.outlines.map(\.group)).count == 12 * MapStressScene.levels)
    }

    @Test("Каждый кусок — внутри своего тайла UTM 1×1 км, а куски участка вместе — ровно участок")
    func piecesAreCutByTiles() {
        let scene = Self.arena
        var pieceArea = [Double](repeating: 0, count: scene.outlines.count)
        var outside = 0
        for piece in scene.pieces {
            let ring = piece.ring.map(Utm34.project)
            // Туда и обратно через градусы — до миллиметра.
            let east = (Double(piece.tile.x) * 1_000 - 0.001)...(Double(piece.tile.x + 1) * 1_000 + 0.001)
            let north = (Double(piece.tile.y) * 1_000 - 0.001)...(Double(piece.tile.y + 1) * 1_000 + 0.001)
            outside += ring.filter { !east.contains($0.east) || !north.contains($0.north) }.count
            pieceArea[piece.parcel] += Self.area(ring)
        }
        #expect(outside == 0, "вершин кусков за краем своего тайла")
        let mismatched = scene.outlines.indices.filter { index in
            let area = Self.area(scene.outlines[index].ring.map(Utm34.project))
            return abs(pieceArea[index] - area) > area * 1e-6
        }
        #expect(mismatched.isEmpty, "участки, чьи куски не складываются в участок: \(mismatched.prefix(5))")
        // Резка по тайлам не редкость: иначе швам негде появиться.
        #expect(scene.pieces.count > scene.outlines.count)
    }

    @Test("Мишень — на углу четырёх тайлов и разрезана ровно на четыре куска")
    func seamTarget() {
        let scene = Self.arena
        let corner = Utm34.project(scene.seamTarget)
        #expect(abs(corner.east - (corner.east / 1_000).rounded() * 1_000) < 0.001)
        #expect(abs(corner.north - (corner.north / 1_000).rounded() * 1_000) < 0.001)
        let targetPieces = scene.pieces.filter { $0.parcel == 0 }
        #expect(targetPieces.count == 4)
        #expect(Set(targetPieces.map(\.tile)).count == 4)
    }

    @Test("Участки не перекрываются — как настоящая земля; все в пределах Арены (1,5 км от БрГТУ)")
    func parcelsDoNotOverlap() {
        let scene = Self.arena
        let center = Utm34.project(MapStressPreset.arena.center)
        let corner = Utm34.project(scene.seamTarget)
        let rings = scene.outlines.map { $0.ring.map(Utm34.project) }
        let vertices = rings.joined()
        #expect(vertices.allSatisfy { hypot($0.east - center.east, $0.north - center.north) < 1_500 + 100 })
        // Мишени (участок 0) не касается никто.
        let intoTarget = rings.dropFirst().joined().filter {
            hypot($0.east - corner.east, $0.north - corner.north) <= MapStressScene.seamTargetRadiusMeters
        }
        #expect(intoTarget.isEmpty)
        // Остальные — каждый в своей ячейке сетки, поэтому не пересекаются и их рамки: проверка проходом по востоку.
        let boxes = rings.dropFirst().map { ring -> (minE: Double, maxE: Double, minN: Double, maxN: Double) in
            let east = ring.map(\.east)
            let north = ring.map(\.north)
            return (east.min() ?? 0, east.max() ?? 0, north.min() ?? 0, north.max() ?? 0)
        }
        let sorted = boxes.sorted { $0.minE < $1.minE }
        var overlaps = 0
        for i in sorted.indices {
            var j = i + 1
            while j < sorted.count, sorted[j].minE < sorted[i].maxE {
                if sorted[j].minN < sorted[i].maxN, sorted[i].minN < sorted[j].maxN {
                    overlaps += 1
                }
                j += 1
            }
        }
        #expect(overlaps == 0)
    }

    @Test("Туман — не меньше чем на 300 тайлах z14, как в критерии S4")
    func fogTiles() {
        #expect(Self.arena.fog.tiles.count >= MapStressScene.fogTileCount)
    }
}
