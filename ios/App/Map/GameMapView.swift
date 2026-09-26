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
        #if DEBUG
            map.accessibilityIdentifier = "game-map"  // снимки проверяют, что нарисовано (`accessibilityValue`)
        #endif
        context.coordinator.environment = Coordinator.Environment(
            theme: Theme(context.environment.colorScheme),
            reduceMotion: context.environment.accessibilityReduceMotion)
        context.coordinator.observe(map)
        return map
    }

    /// Тема и «Уменьшить движение» — из окружения SwiftUI; данные модели карта отслеживает сама (`observe`).
    func updateUIView(_ map: MKMapView, context: Context) {
        let environment = Coordinator.Environment(
            theme: Theme(context.environment.colorScheme),
            reduceMotion: context.environment.accessibilityReduceMotion)
        if environment != context.coordinator.environment {
            context.coordinator.environment = environment
            context.coordinator.sync(map)
        }
    }

    @MainActor
    final class Coordinator: NSObject, MKMapViewDelegate {
        let model: MapModel
        let ants = ContestedAntsView()
        private let fogOverlay: FogOverlay
        private let fogRenderer: MapFogRenderer
        private var landOverlays: [any MKOverlay] = []
        /// Стиль каждого слоя земли: заливка или кромка.
        private var styles: [ObjectIdentifier: (style: LandStyle, isEdge: Bool)] = [:]
        /// Тема и «Уменьшить движение» — из `updateUIView`.
        var environment = Environment(theme: .day, reduceMotion: false)
        private var theme: Theme { environment.theme }
        private var appliedLand: LandKey?
        private var appliedFog: FogKey?
        private var appliedZones: ZonesKey?

        struct Environment: Equatable {
            var theme: Theme
            var reduceMotion: Bool
        }

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
            let overlay = FogOverlay()
            fogOverlay = overlay
            fogRenderer = MapFogRenderer(overlay: overlay)
        }

        static func region(_ window: MapWindow) -> MKCoordinateRegion {
            MKCoordinateRegion(
                center: CLLocationCoordinate2D(
                    latitude: (window.south + window.north) / 2, longitude: (window.west + window.east) / 2),
                span: MKCoordinateSpan(
                    latitudeDelta: window.north - window.south, longitudeDelta: window.east - window.west))
        }

        /// Следить за моделью: `sync` читает её внутри `withObservationTracking`, изменение — снова `sync` на следующем
        /// витке главного потока. `updateUIView` для этого не годится: на снимке «Отношения» он не пришёл, и карта
        /// осталась с прежними цветами.
        func observe(_ map: MKMapView) {
            withObservationTracking {
                sync(map)
            } onChange: { [weak self, weak map] in
                Task { @MainActor in
                    guard let self, let map else { return }
                    self.observe(map)
                }
            }
        }

        func sync(_ map: MKMapView) {
            let theme = environment.theme
            let reduceMotion = environment.reduceMotion
            let land = LandKey(
                revision: model.landRevision, visible: model.layer == .capture, coloring: model.coloring,
                viewer: model.viewerId, player: model.player, theme: theme)
            let fog = FogKey(revision: model.fogRevision, visible: model.layer == .explore, theme: theme)
            let fogTiles = model.fog
            // Сменился слой — карта переходит кроссфейдом (снимок старой картинки растворяется в новой), а не мигает.
            let layerSwitched = appliedLand.map { $0.visible != land.visible } ?? false
            if layerSwitched, !reduceMotion {
                UIView.transition(
                    with: map, duration: MotionSpec.LayerSwitch.duration,
                    options: [.transitionCrossDissolve, .allowUserInteraction]
                ) {
                    self.apply(land: land, fog: fog, fogTiles: fogTiles, on: map)
                }
            } else {
                apply(land: land, fog: fog, fogTiles: fogTiles, on: map)
            }
            let zones = ZonesKey(zones: model.visibleZones, theme: theme, reduceMotion: reduceMotion)
            if zones != appliedZones {
                ants.show(zones.zones, theme: theme, reduceMotion: reduceMotion)
                ants.layout(on: map)
                appliedZones = zones
            }
        }

        /// Земля и туман — по ключам: перестраивается только то, что изменилось.
        private func apply(land: LandKey, fog: FogKey, fogTiles: [FogTileKey: FogTileBits], on map: MKMapView) {
            if land != appliedLand {
                rebuildLand(on: map, visible: land.visible)
                appliedLand = land
            }
            if fog != appliedFog {
                let shown = map.overlays.contains { $0 === fogOverlay }
                if fog.visible, !shown {
                    map.addOverlay(fogOverlay, level: .aboveRoads)
                } else if !fog.visible, shown {
                    map.removeOverlay(fogOverlay)
                }
                if fog.visible {
                    fogRenderer.update(fogTiles, theme: fog.theme)
                }
                appliedFog = fog
            }
        }

        /// Слои земли заново: заливки, над ними кромки. Порядок: земля, туман над ней (когда он есть, земли нет).
        private func rebuildLand(on map: MKMapView, visible: Bool) {
            // Прежние слои живы, пока не добавлены новые: иначе новый объект занял бы адрес только что освобождённого,
            // и MapKit взял бы для него прежний рендерер — «Отношения» оставались с цветами «Игроков» (снимок 28).
            let previous = landOverlays
            defer { map.removeOverlays(previous) }
            landOverlays = []
            styles = [:]
            guard visible else { return }
            var fills: [LandStyle: [MKPolygon]] = [:]
            var edges: [LandStyle: [MKPolyline]] = [:]
            for parcel in model.land.parcels {
                let style = model.style(of: parcel)
                fills[style, default: []].append(Self.polygon(parcel.shape))
                for line in parcel.borders {
                    var coordinates = line.map {
                        CLLocationCoordinate2D(latitude: $0.latitude, longitude: $0.longitude)
                    }
                    edges[style.edgeStyle, default: []]
                        .append(MKPolyline(coordinates: &coordinates, count: coordinates.count))
                }
            }
            for style in Self.ordered(fills.keys) {
                guard let polygons = fills[style] else { continue }
                let multi = MKMultiPolygon(polygons)
                styles[ObjectIdentifier(multi)] = (style, false)
                landOverlays.append(multi)
            }
            #if DEBUG
                // Что нарисовано: окраска и число слоёв — снимок «Отношения» проверяет, что карта перекрасилась.
                map.accessibilityValue = "\(model.coloring.rawValue) \(fills.count)"
            #endif
            for style in Self.ordered(edges.keys) {
                guard let lines = edges[style] else { continue }
                let multi = MKMultiPolyline(lines)
                styles[ObjectIdentifier(multi)] = (style, true)
                landOverlays.append(multi)
            }
            map.addOverlays(landOverlays, level: .aboveRoads)
        }

        /// Порядок слоёв — от призраков к своей: куски не перекрываются, но кромка своей должна быть сверху.
        /// При равном отношении — по цвету и уровню: одинаковые данные дают одинаковую карту.
        private static func ordered(_ styles: some Sequence<LandStyle>) -> [LandStyle] {
            let order: [TerritoryRelation] = [.lost, .noMansLand, .rival, .clan, .contested, .mine]
            func rank(_ style: LandStyle) -> (Int, String, Int) {
                (order.firstIndex(of: style.relation) ?? 0, style.color.rawValue, style.level?.rawValue ?? 0)
            }
            return styles.sorted { rank($0) < rank($1) }
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
            guard let entry = styles[ObjectIdentifier(overlay)] else {
                return MKOverlayRenderer(overlay: overlay)
            }
            let style = entry.style
            let isEdge = entry.isEdge
            if !isEdge, let multi = overlay as? MKMultiPolygon {
                let renderer = LandFillRenderer(overlay: multi)
                renderer.prepare(multi, color: style.fill(theme))
                return renderer
            }
            if isEdge, let multi = overlay as? MKMultiPolyline {
                let stroke = style.relation.edge
                let renderer = LandEdgeRenderer(multiPolyline: multi)
                renderer.glow = style.relation.glowRadius(theme: theme)
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

/// Заливка земли одного стиля — все куски **одним путём**. `MKMultiPolygonRenderer` закрашивает куски по отдельности
/// со сглаживанием, и общий край соседних кусков одного участка (сервер режет землю по тайлам) закрашен дважды
/// наполовину — по линии тайла видна светлая нить (снимки 24 и 28 первых прогонов, и без сглаживания тоже: свой `draw`
/// у подкласса MapKit не зовёт). Один путь с правилом чёт-нечет закрашивается целиком: шва нет, дыры — дыры.
///
/// Рисует тайлы карты MapKit в своих потоках; данные задаются один раз до первой отрисовки (`prepare`) и дальше не
/// меняются. Своего инициализатора нет — как у `LandEdgeRenderer`.
final class LandFillRenderer: MKOverlayRenderer, @unchecked Sendable {
    private struct Piece {
        var bounds: MKMapRect
        var rings: [[MKMapPoint]]
    }

    private var pieces: [Piece] = []
    private var color = CGColor(gray: 0, alpha: 0)

    func prepare(_ multi: MKMultiPolygon, color: RGBA) {
        pieces = multi.polygons.map { polygon in
            let rings = [polygon] + (polygon.interiorPolygons ?? [])
            return Piece(
                bounds: polygon.boundingMapRect,
                rings: rings.map { Array(UnsafeBufferPointer(start: $0.points(), count: $0.pointCount)) })
        }
        self.color = CGColor(srgbRed: color.red, green: color.green, blue: color.blue, alpha: color.alpha)
    }

    override func draw(_ mapRect: MKMapRect, zoomScale: MKZoomScale, in context: CGContext) {
        // Сглаженный край заходит на пиксель за кусок — берём куски и чуть за краем части карты.
        let margin = 2 / Double(zoomScale)
        let area = mapRect.insetBy(dx: -margin, dy: -margin)
        let path = CGMutablePath()
        for piece in pieces where piece.bounds.intersects(area) {
            for ring in piece.rings where ring.count > 2 {
                path.addLines(between: ring.map { point(for: $0) })
                path.closeSubpath()
            }
        }
        guard !path.isEmpty else { return }
        context.addPath(path)
        context.setFillColor(color)
        context.fillPath(using: .evenOdd)
    }
}

/// Кромка земли: толщина и пунктир — у `MKOverlayPathRenderer` в экранных pt, свечение своей ночью — тенью того же
/// цвета (`TerritoryRelation.glowRadius`, 3 pt).
///
/// Своего инициализатора нет: `init(multiPolyline:)` MapKit внутри зовёт `init(overlay:)`, и подкласс со своим
/// назначенным инициализатором падал бы («unimplemented initializer»). Свечение задаётся после создания.
final class LandEdgeRenderer: MKMultiPolylineRenderer, @unchecked Sendable {
    /// Радиус свечения, pt; 0 — без свечения. Задаётся до первой отрисовки.
    var glow = 0.0

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
