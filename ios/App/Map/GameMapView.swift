import DesignSystem
import GameCore
import MapKit
import SwiftUI

/// Карта игры: подложка Apple Maps как у игры (PLAN.md, D4: плоская, `.muted`, без POI), над ней земля, туман
/// и «бегущие муравьи» на спорной земле (docs/design/tokens.md, §3.1).
///
/// - **Земля** — один `MKMultiPolygon` на стиль (отношение, цвет, уровень) из кусков всех тайлов, без обводки: куски
///   одного стиля — один контур, и на краях тайлов нет швов. Над заливками — кромки (`LandBorders`) отдельным слоем
///   линий: толщина и пунктир — от отношения, ночью у своей — свечение.
/// - **Туман** — `MapFogRenderer` только на «Исследовании»; земли там скрыты.
/// - **Спорное** — `CAShapeLayer` над картой, только неистёкшие зоны: подложка и пунктир, который ползёт
///   (перерисовка тайлов MapKit 15 раз в секунду мерцала бы). «Уменьшить движение» — пунктир стоит.
/// - **Касание** — `UITapGestureRecognizer` → координата и допуск в метрах → `MapModel.select`.
struct GameMapView: UIViewRepresentable {
    let model: MapModel

    func makeCoordinator() -> Coordinator { Coordinator(model: model) }

    func makeUIView(context: Context) -> MKMapView {
        let map = MKMapView()
        let configuration = MKStandardMapConfiguration(elevationStyle: .flat, emphasisStyle: .muted)
        configuration.pointOfInterestFilter = .excludingAll
        map.preferredConfiguration = configuration
        map.delegate = context.coordinator
        map.setRegion(Coordinator.region(model.initialWindow), animated: false)
        let tap = UITapGestureRecognizer(target: context.coordinator, action: #selector(Coordinator.tapped(_:)))
        map.addGestureRecognizer(tap)
        let ants = context.coordinator.ants
        ants.frame = map.bounds
        ants.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        map.addSubview(ants)
        return map
    }

    func updateUIView(_ map: MKMapView, context: Context) {
        context.coordinator.sync(
            map, theme: Theme(context.environment.colorScheme),
            reduceMotion: context.environment.accessibilityReduceMotion)
    }

    @MainActor
    final class Coordinator: NSObject, MKMapViewDelegate {
        let model: MapModel
        let ants = ContestedAntsView()
        private let fogOverlay = FogOverlay()
        private let fogRenderer: MapFogRenderer
        private var landOverlays: [any MKOverlay] = []
        /// Стиль каждого слоя земли: заливка или кромка.
        private var styles: [ObjectIdentifier: (style: LandStyle, isEdge: Bool)] = [:]
        private var theme = Theme.day
        private var appliedLand: LandKey?
        private var appliedFog: FogKey?
        private var appliedZones: ZonesKey?

        /// Что сейчас нарисовано из земли: меняется ключ — слои перестраиваются.
        private struct LandKey: Equatable {
            var revision: Int
            var visible: Bool
            var coloring: LandColoring
            var viewer: String?
            var player: PlayerColor
            var theme: Theme
        }

        private struct FogKey: Equatable {
            var revision: Int
            var visible: Bool
            var theme: Theme
        }

        private struct ZonesKey: Equatable {
            var zones: [ContestedZone]
            var theme: Theme
            var reduceMotion: Bool
        }

        init(model: MapModel) {
            self.model = model
            fogRenderer = MapFogRenderer(overlay: fogOverlay)
        }

        static func region(_ window: MapWindow) -> MKCoordinateRegion {
            MKCoordinateRegion(
                center: CLLocationCoordinate2D(
                    latitude: (window.south + window.north) / 2, longitude: (window.west + window.east) / 2),
                span: MKCoordinateSpan(
                    latitudeDelta: window.north - window.south, longitudeDelta: window.east - window.west))
        }

        func sync(_ map: MKMapView, theme: Theme, reduceMotion: Bool) {
            self.theme = theme
            let land = LandKey(
                revision: model.landRevision, visible: model.layer == .capture, coloring: model.coloring,
                viewer: model.viewerId, player: model.player, theme: theme)
            if land != appliedLand {
                rebuildLand(on: map, visible: land.visible)
                appliedLand = land
            }
            let fog = FogKey(revision: model.fogRevision, visible: model.layer == .explore, theme: theme)
            if fog != appliedFog {
                let shown = map.overlays.contains { $0 === fogOverlay }
                if fog.visible, !shown {
                    map.addOverlay(fogOverlay, level: .aboveRoads)
                } else if !fog.visible, shown {
                    map.removeOverlay(fogOverlay)
                }
                if fog.visible {
                    fogRenderer.update(model.fog, theme: theme)
                }
                appliedFog = fog
            }
            let zones = ZonesKey(zones: model.visibleZones, theme: theme, reduceMotion: reduceMotion)
            if zones != appliedZones {
                ants.show(zones.zones, theme: theme, reduceMotion: reduceMotion)
                ants.layout(on: map)
                appliedZones = zones
            }
        }

        /// Слои земли заново: заливки, над ними кромки. Порядок: земля, туман над ней (когда он есть, земли нет).
        private func rebuildLand(on map: MKMapView, visible: Bool) {
            map.removeOverlays(landOverlays)
            landOverlays = []
            styles = [:]
            guard visible else { return }
            var fills: [LandStyle: [MKPolygon]] = [:]
            var edges: [LandStyle: [MKPolyline]] = [:]
            for parcel in model.land.parcels {
                let style = model.style(of: parcel)
                fills[style, default: []].append(Self.polygon(parcel.shape))
                for line in parcel.borders {
                    var coordinates = line.map { CLLocationCoordinate2D(latitude: $0.latitude, longitude: $0.longitude) }
                    edges[style.edgeStyle, default: []]
                        .append(MKPolyline(coordinates: &coordinates, count: coordinates.count))
                }
            }
            // Порядок — от призраков к своей: куски не перекрываются, но кромка своей должна быть сверху.
            let order: [TerritoryRelation] = [.lost, .noMansLand, .rival, .clan, .contested, .mine]
            func sorted(_ keys: Dictionary<LandStyle, some Any>.Keys) -> [LandStyle] {
                keys.sorted {
                    let a = order.firstIndex(of: $0.relation) ?? 0
                    let b = order.firstIndex(of: $1.relation) ?? 0
                    return (a, $0.color.rawValue, $0.level?.rawValue ?? 0) < (b, $1.color.rawValue, $1.level?.rawValue ?? 0)
                }
            }
            for style in sorted(fills.keys) {
                guard let polygons = fills[style] else { continue }
                let multi = MKMultiPolygon(polygons)
                styles[ObjectIdentifier(multi)] = (style, false)
                landOverlays.append(multi)
            }
            for style in sorted(edges.keys) {
                guard let lines = edges[style] else { continue }
                let multi = MKMultiPolyline(lines)
                styles[ObjectIdentifier(multi)] = (style, true)
                landOverlays.append(multi)
            }
            map.addOverlays(landOverlays, level: .aboveRoads)
        }

        private static func polygon(_ shape: ParcelShape) -> MKPolygon {
            func coordinates(_ ring: [Coordinate]) -> [CLLocationCoordinate2D] {
                ring.map { CLLocationCoordinate2D(latitude: $0.latitude, longitude: $0.longitude) }
            }
            let holes = shape.holes.map { ring in
                var points = coordinates(ring)
                return MKPolygon(coordinates: &points, count: points.count)
            }
            var exterior = coordinates(shape.exterior)
            return MKPolygon(coordinates: &exterior, count: exterior.count, interiorPolygons: holes)
        }

        // MARK: - MKMapViewDelegate

        func mapView(_ mapView: MKMapView, rendererFor overlay: any MKOverlay) -> MKOverlayRenderer {
            if overlay === fogOverlay {
                return fogRenderer
            }
            guard let (style, isEdge) = styles[ObjectIdentifier(overlay)] else {
                return MKOverlayRenderer(overlay: overlay)
            }
            if !isEdge, let multi = overlay as? MKMultiPolygon {
                let renderer = MKMultiPolygonRenderer(multiPolygon: multi)
                renderer.fillColor = style.fill(theme).uiColor
                renderer.lineWidth = 0  // заливки без обводки: так не видно швов на краях тайлов (PLAN.md, D4)
                return renderer
            }
            if isEdge, let multi = overlay as? MKMultiPolyline {
                let stroke = style.relation.edge
                let renderer = LandEdgeRenderer(
                    multiPolyline: multi, glow: style.relation.glowRadius(theme: theme))
                renderer.strokeColor = style.edge(theme).uiColor
                renderer.lineWidth = stroke.width
                renderer.lineCap = .round
                renderer.lineJoin = .round
                if !stroke.dash.isEmpty {
                    renderer.lineDashPattern = stroke.dash.map { NSNumber(value: $0) }
                }
                return renderer
            }
            return MKOverlayRenderer(overlay: overlay)
        }

        func mapViewDidChangeVisibleRegion(_ mapView: MKMapView) {
            ants.layout(on: mapView)
        }

        func mapView(_ mapView: MKMapView, regionDidChangeAnimated animated: Bool) {
            ants.layout(on: mapView)
            let region = mapView.region
            model.show(
                MapWindow(
                    center: Coordinate(latitude: region.center.latitude, longitude: region.center.longitude),
                    latitudeDelta: region.span.latitudeDelta, longitudeDelta: region.span.longitudeDelta))
        }

        // MARK: - Касание

        /// Допуск касания — 22 pt на экране (половина пальца), в метрах по текущему масштабу.
        private static let touchTolerancePoints = 22.0

        @objc func tapped(_ gesture: UITapGestureRecognizer) {
            guard let map = gesture.view as? MKMapView, gesture.state == .ended else { return }
            let point = gesture.location(in: map)
            let coordinate = map.convert(point, toCoordinateFrom: map)
            let aside = map.convert(CGPoint(x: point.x + Self.touchTolerancePoints, y: point.y), toCoordinateFrom: map)
            let tolerance = MKMapPoint(coordinate).distance(to: MKMapPoint(aside))
            model.select(
                at: Coordinate(latitude: coordinate.latitude, longitude: coordinate.longitude), tolerance: tolerance)
        }
    }
}

/// Кромка земли: толщина и пунктир — у `MKOverlayPathRenderer` в экранных pt, свечение своей ночью — тенью того же
/// цвета (`TerritoryRelation.glowRadius`, 3 pt).
final class LandEdgeRenderer: MKMultiPolylineRenderer {
    private let glow: Double

    init(multiPolyline: MKMultiPolyline, glow: Double) {
        self.glow = glow
        super.init(multiPolyline: multiPolyline)
    }

    override func applyStrokeProperties(to context: CGContext, atZoomScale zoomScale: MKZoomScale) {
        super.applyStrokeProperties(to: context, atZoomScale: zoomScale)
        if glow > 0, let color = strokeColor?.cgColor {
            // Тень задаётся в пикселях растра, а не в точках карты: радиус в pt × масштаб экрана.
            context.setShadow(offset: .zero, blur: CGFloat(glow) * contentScaleFactor, color: color)
        }
    }
}

/// «Бегущие муравьи» над картой (docs/design/tokens.md, §3.1 и §7): подложка `ants-halo` 2,6 pt и пунктир 4/4 pt
/// `ants-ink` 1,7 pt, фаза −8 pt за 1,2 с по кругу. Путь пересчитывается при движении карты.
final class ContestedAntsView: UIView {
    private let halo = CAShapeLayer()
    private let dashes = CAShapeLayer()
    private var rings: [[CLLocationCoordinate2D]] = []
    private static let animationKey = "ants"

    override init(frame: CGRect) {
        super.init(frame: frame)
        isUserInteractionEnabled = false
        let stroke = TerritoryRelation.contested.edge
        for layer in [halo, dashes] {
            layer.fillColor = nil
            layer.lineJoin = .round
            layer.lineCap = .butt
            self.layer.addSublayer(layer)
        }
        halo.lineWidth = stroke.haloWidth ?? 0
        dashes.lineWidth = stroke.width
        dashes.lineDashPattern = stroke.dash.map { NSNumber(value: $0) }
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) не используется")
    }

    func show(_ zones: [ContestedZone], theme: Theme, reduceMotion: Bool) {
        rings = zones.flatMap { [$0.shape.exterior] + $0.shape.holes }.map { ring in
            ring.map { CLLocationCoordinate2D(latitude: $0.latitude, longitude: $0.longitude) }
        }
        halo.strokeColor = Palette.antsHalo[theme].uiColor.cgColor
        dashes.strokeColor = Palette.antsInk[theme].uiColor.cgColor
        dashes.removeAnimation(forKey: Self.animationKey)
        if !reduceMotion, !rings.isEmpty {
            let animation = CABasicAnimation(keyPath: "lineDashPhase")
            animation.fromValue = 0
            animation.toValue = MotionSpec.Ants.phaseShift
            animation.duration = MotionSpec.Ants.period
            animation.repeatCount = .infinity
            dashes.add(animation, forKey: Self.animationKey)
        }
    }

    func layout(on map: MKMapView) {
        let path = CGMutablePath()
        for ring in rings where ring.count > 1 {
            path.addLines(between: ring.map { map.convert($0, toPointTo: self) })
            path.closeSubpath()
        }
        CATransaction.begin()
        CATransaction.setDisableActions(true)  // путь следует за картой без анимации
        halo.path = path
        dashes.path = path
        CATransaction.commit()
    }
}
