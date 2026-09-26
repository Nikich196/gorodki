import DesignSystem
import GameCore
import MapKit
import SwiftUI

extension Coordinate {
    var location: CLLocationCoordinate2D { CLLocationCoordinate2D(latitude: latitude, longitude: longitude) }
}

/// Карта HUD (PLAN.md, §6.7; docs/design/tokens.md, §3): подложка Apple Maps как у игры (плоская, `.muted`, без POI),
/// след цветом кромки игрока на подложке, временные контуры петель — пунктиром и бледной заливкой (отличаются от
/// настоящего участка, у которого кромка сплошная), пунктир до точки замыкания и «я» с дышащим кольцом.
/// Камера идёт за бегущим так, чтобы точка была над панелью HUD.
struct RunMapView: View {
    let trail: [[Coordinate]]
    let contours: [LoopContour]
    let target: Coordinate?
    let player: PlayerColor
    @State private var camera: MapCameraPosition = .automatic

    /// Сколько метров видно по вертикали (tokens.md, §5: HUD — 1,5 pt на метр, экран ≈ 850 pt).
    private static let spanMeters = 560.0

    var body: some View {
        let position = trail.last?.last
        Map(position: $camera, interactionModes: [.pan, .zoom]) {
            ForEach(contours) { contour in
                MapPolygon(coordinates: contour.coordinates.map(\.location))
                    .foregroundStyle(player.fillColor(.one).opacity(0.7))
                    .stroke(
                        player.edgeColor,
                        style: StrokeStyle(lineWidth: 2.4, lineCap: .round, lineJoin: .round, dash: [7, 6]))
            }
            ForEach(trail.indices, id: \.self) { index in
                let line = trail[index].map(\.location)
                MapPolyline(coordinates: line)
                    .stroke(
                        Palette.trailCase.color,
                        style: StrokeStyle(lineWidth: TrailStyle.caseWidth, lineCap: .round, lineJoin: .round))
                MapPolyline(coordinates: line)
                    .stroke(
                        player.edgeColor,
                        style: StrokeStyle(lineWidth: TrailStyle.width, lineCap: .round, lineJoin: .round))
            }
            if let target, let position {
                MapPolyline(coordinates: [position.location, target.location])
                    .stroke(
                        Palette.gapInk.color,
                        style: StrokeStyle(
                            lineWidth: TrailStyle.gapWidth, lineCap: .round,
                            dash: TrailStyle.gapDash.map { CGFloat($0) }))
                Annotation("Точка замыкания", coordinate: target.location, anchor: .center) {
                    TargetMarker()
                }
            }
            if let position {
                Annotation("Ты здесь", coordinate: position.location, anchor: .center) {
                    PositionMarker(player: player)
                }
                .annotationTitles(.hidden)
            }
        }
        .mapStyle(.standard(elevation: .flat, emphasis: .muted, pointsOfInterest: .excludingAll))
        .mapControlVisibility(.hidden)
        .onAppear { follow(position, animated: false) }
        .onChange(of: position) { _, fresh in follow(fresh, animated: true) }
    }

    /// Камера — на бегущего, чуть южнее: точка над панелью HUD.
    private func follow(_ position: Coordinate?, animated: Bool) {
        guard let position else { return }
        let latitudeSpan = Self.spanMeters / 111_320
        let center = CLLocationCoordinate2D(
            latitude: position.latitude - latitudeSpan * 0.22, longitude: position.longitude)
        let region = MKCoordinateRegion(
            center: center, latitudinalMeters: Self.spanMeters, longitudinalMeters: Self.spanMeters)
        if animated {
            withAnimation(.easeInOut(duration: 0.8)) { camera = .region(region) }
        } else {
            camera = .region(region)
        }
    }
}

/// «Я» на карте: точка цвета игрока с белой кромкой и кольцом, которое «дышит» раз в секунду (tokens.md, §7).
struct PositionMarker: View {
    let player: PlayerColor
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        ZStack {
            if reduceMotion {
                breathRing(scale: 1)
            } else {
                PhaseAnimator([false, true]) { expanded in
                    breathRing(scale: expanded ? 1.12 : 1)
                } animation: { expanded in
                    expanded ? Motion.fogBreathExpand : Motion.fogBreathContract
                }
            }
            Circle()
                .fill(player.color)
                .frame(width: 18, height: 18)
                .overlay { Circle().stroke(Palette.trailCase.color, lineWidth: 3) }
                .shadow(color: .black.opacity(0.25), radius: 3, y: 1)
        }
        .accessibilityHidden(true)
    }

    private func breathRing(scale: Double) -> some View {
        Circle()
            .fill(player.color.opacity(0.18))
            .overlay { Circle().stroke(FogStyle.edge.color, lineWidth: 1.6) }
            .frame(width: 44, height: 44)
            .scaleEffect(scale)
    }
}

/// Точка замыкания: кольцо с точкой, как в макете.
struct TargetMarker: View {
    var body: some View {
        Circle()
            .fill(Palette.trailCase.color)
            .frame(width: 18, height: 18)
            .overlay { Circle().stroke(Palette.gapInk.color, lineWidth: 2.4) }
            .overlay { Circle().fill(Palette.gapInk.color).frame(width: 6, height: 6) }
            .accessibilityHidden(true)
    }
}

/// След в рамке без карты — итог забега: точки на плоскости, вписанные в рамку с отступом. Рисуется `trim`-ом.
struct TrackShape: Shape {
    let coordinates: [Coordinate]

    nonisolated func path(in rect: CGRect) -> Path {
        let points = TrackGeometry.fit(coordinates, in: rect.insetBy(dx: 12, dy: 12))
        var path = Path()
        guard let first = points.first else { return path }
        path.move(to: first)
        for point in points.dropFirst() {
            path.addLine(to: point)
        }
        return path
    }
}

/// Плоская проекция следа для рисования вне карты.
enum TrackGeometry {
    /// Точки на плоскости (восток — вправо, север — вверх), вписанные в рамку с сохранением пропорций.
    nonisolated static func fit(_ coordinates: [Coordinate], in rect: CGRect) -> [CGPoint] {
        guard let origin = coordinates.first else { return [] }
        let plane = LocalTangentPlane(origin: origin)
        let planar = coordinates.map { plane.project($0) }
        let east = planar.map(\.east)
        let north = planar.map(\.north)
        guard let minEast = east.min(), let maxEast = east.max(), let minNorth = north.min(),
            let maxNorth = north.max()
        else { return [] }
        let width = max(maxEast - minEast, 1)
        let height = max(maxNorth - minNorth, 1)
        let scale = min(rect.width / width, rect.height / height)
        let offsetX = rect.minX + (rect.width - width * scale) / 2
        let offsetY = rect.minY + (rect.height - height * scale) / 2
        return planar.map {
            CGPoint(x: offsetX + ($0.east - minEast) * scale, y: offsetY + (maxNorth - $0.north) * scale)
        }
    }

    /// Регион карты, вмещающий точки с запасом `padding` (доля размера) и сдвигом вниз `shiftDown` (доля высоты):
    /// так контур оказывается над панелью или карточкой.
    static func region(_ coordinates: [Coordinate], padding: Double = 1.6, shiftDown: Double = 0)
        -> MKCoordinateRegion?
    {
        let latitudes = coordinates.map(\.latitude)
        let longitudes = coordinates.map(\.longitude)
        guard let minLat = latitudes.min(), let maxLat = latitudes.max(), let minLon = longitudes.min(),
            let maxLon = longitudes.max()
        else { return nil }
        let latitudeDelta = max((maxLat - minLat) * padding, 0.002)
        let longitudeDelta = max((maxLon - minLon) * padding, 0.002)
        let center = CLLocationCoordinate2D(
            latitude: (minLat + maxLat) / 2 - latitudeDelta * shiftDown, longitude: (minLon + maxLon) / 2)
        return MKCoordinateRegion(
            center: center, span: MKCoordinateSpan(latitudeDelta: latitudeDelta, longitudeDelta: longitudeDelta))
    }
}
