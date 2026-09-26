import DesignSystem
import GameCore
import MapKit
import SwiftUI
import os

/// Карта игры: подложка Apple Maps как у игры (PLAN.md, D4: плоская, `.muted`, без POI), над ней земля, туман
/// и «бегущие муравьи» на спорной земле (docs/design/tokens.md, §3.1).
///
/// - **Земля** — слой MapKit на группу кусков (`LandGroup`: отношение, цвет владельца, уровень) из всех тайлов;
///   кромки (`LandBorders`) — своими слоями над заливками. Слои **создаются один раз** и дальше только меняют
///   содержимое (новые тайлы), цвет (окраска, тема) и видимость (слой карты) с перерисовкой: снимки показали, что после
///   `removeOverlays` и `addOverlays` MapKit оставлял на экране прежнюю картинку.
/// - **Туман** — `MapFogRenderer`, виден только на «Исследовании»; земли там скрыты.
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
        /// Слои земли по группам — живут, пока жива карта.
        private var layers: [LandLayerOverlay.Key: LandLayerOverlay] = [:]
        /// Тема и «Уменьшить движение» — из `updateUIView`.
        var environment = Environment(theme: .day, reduceMotion: false)
        private var theme: Theme { environment.theme }
        private var appliedLand: LandKey?
        private var appliedPaint: PaintKey?
        private var appliedLayer: MapLayer?
        private var appliedFog: FogKey?
        private var appliedZones: ZonesKey?

        struct Environment: Equatable {
            var theme: Theme
            var reduceMotion: Bool
        }

        /// Какая земля в слоях: меняется — у слоёв новое содержимое (новые тайлы, другой зритель).
        private struct LandKey: Equatable {
            var revision: Int
            var viewer: String?
        }

        /// Чем окрашена земля: меняется — слои перекрашиваются.
        private struct PaintKey: Equatable {
            var coloring: LandColoring
            var player: PlayerColor
            var theme: Theme
        }

        private struct FogKey: Equatable {
            var revision: Int
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
        /// витке главного потока. `updateUIView` для этого не годится: на смену окраски он не пришёл.
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
            if appliedFog == nil {
                map.addOverlay(fogOverlay, level: .aboveRoads)  // один раз; виден только на «Исследовании»
            }
            let land = LandKey(revision: model.landRevision, viewer: model.viewerId)
            let paint = PaintKey(coloring: model.coloring, player: model.player, theme: theme)
            let fog = FogKey(revision: model.fogRevision, theme: theme)
            let layer = model.layer
            if land != appliedLand {
                refill(on: map)
                appliedLand = land
                appliedPaint = nil  // у новых слоёв цвет задаёт `repaint`
            }
            if paint != appliedPaint {
                repaint(on: map)
                appliedPaint = paint
            }
            if fog != appliedFog {
                fogRenderer.update(model.fog, theme: theme)
                appliedFog = fog
            }
            if layer != appliedLayer {
                // Смена слоя — кроссфейдом (снимок прежней картинки растворяется в новой), а не миганием.
                if appliedLayer != nil, !reduceMotion {
                    UIView.transition(
                        with: map, duration: MotionSpec.LayerSwitch.duration,
                        options: [.transitionCrossDissolve, .allowUserInteraction]
                    ) {
                        self.show(layer, on: map)
                    }
                } else {
                    show(layer, on: map)
                }
                appliedLayer = layer
            }
            let zones = ZonesKey(zones: model.visibleZones, theme: theme, reduceMotion: reduceMotion)
            if zones != appliedZones {
                ants.show(zones.zones, theme: theme, reduceMotion: reduceMotion)
                ants.layout(on: map)
                appliedZones = zones
            }
            #if DEBUG
                report(on: map)
            #endif
        }

        /// «Захват» — земли, «Исследование» — туман.
        private func show(_ layer: MapLayer, on map: MKMapView) {
            fogRenderer.setVisible(layer == .explore)
            let visible = layer == .capture
            for overlay in layers.values {
                update(overlay, on: map) { $0.visible = visible }
            }
        }

        /// Новое содержимое слоёв: куски по группам. Нет слоя для группы — он добавляется на своё место в порядке;
        /// группа исчезла — её слой пустеет (не снимается с карты).
        private func refill(on map: MKMapView) {
            var fills: [LandGroup: [[[MKMapPoint]]]] = [:]
            var edges: [LandGroup: [[MKMapPoint]]] = [:]
            var fillBounds: [LandGroup: [MKMapRect]] = [:]
            var edgeBounds: [LandGroup: [MKMapRect]] = [:]
            for parcel in model.land.parcels {
                let group = model.group(of: parcel)
                let rings = ([parcel.shape.exterior] + parcel.shape.holes).map(Self.points)
                fills[group, default: []].append(rings)
                fillBounds[group, default: []].append(Self.bounds(rings.first ?? []))
                for line in parcel.borders {
                    let points = Self.points(line)
                    edges[group.edge, default: []].append(points)
                    edgeBounds[group.edge, default: []].append(Self.bounds(points))
                }
            }
            var wanted: [LandLayerOverlay.Key: LandLayerOverlay.Content.Shapes] = [:]
            for (group, pieces) in fills {
                wanted[LandLayerOverlay.Key(group: group, isEdge: false)] = .fills(
                    zip(fillBounds[group] ?? [], pieces).map { LandLayerOverlay.Content.Fill(bounds: $0, rings: $1) })
            }
            for (group, lines) in edges {
                wanted[LandLayerOverlay.Key(group: group, isEdge: true)] = .lines(
                    zip(edgeBounds[group] ?? [], lines).map { LandLayerOverlay.Content.Line(bounds: $0, points: $1) })
            }
            let visible = model.layer == .capture
            for key in Self.ordered(Set(layers.keys).union(wanted.keys)) {
                let shapes = wanted[key] ?? (key.isEdge ? .lines([]) : .fills([]))
                if let overlay = layers[key] {
                    update(overlay, on: map) { $0.shapes = shapes }
                    continue
                }
                let overlay = LandLayerOverlay(key: key)
                overlay.content.withLock {
                    $0.shapes = shapes
                    $0.visible = visible
                }
                layers[key] = overlay
                // На своё место: под ближайшим по порядку слоем, который уже на карте; кромка своей — сверху.
                let order = Self.ordered(Set(layers.keys))
                let above = order.drop { $0 != key }.dropFirst().first { layers[$0] != nil && $0 != key }
                if let above, let neighbor = layers[above], map.overlays.contains(where: { $0 === neighbor }) {
                    map.insertOverlay(overlay, below: neighbor)
                } else {
                    map.addOverlay(overlay, level: .aboveRoads)
                }
            }
        }

        /// Цвета и штрих всех слоёв — по окраске, цвету игрока и теме.
        private func repaint(on map: MKMapView) {
            #if DEBUG
                repaints += 1
            #endif
            let theme = self.theme
            for (key, overlay) in layers {
                let style = model.style(of: key.group)
                if key.isEdge {
                    let color = style.edge(theme)
                    let stroke = style.relation.edge
                    let glow = style.relation.glowRadius(theme: theme)
                    update(overlay, on: map) { content in
                        content.color = color
                        content.lineWidth = stroke.width
                        content.dash = stroke.dash
                        content.glow = glow
                    }
                } else {
                    let color = style.fill(theme)
                    update(overlay, on: map) { $0.color = color }
                }
            }
        }

        /// Изменить содержимое слоя и перерисовать его тайлы.
        private func update(
            _ overlay: LandLayerOverlay, on map: MKMapView, _ change: @Sendable (inout LandLayerOverlay.Content) -> Void
        ) {
            overlay.content.withLock { change(&$0) }
            map.renderer(for: overlay)?.setNeedsDisplay()
        }

        #if DEBUG
            private var repaints = 0

            /// Что нарисовано: окраска, слои и цвет заливки соперника уровня 1 (для снимка «Отношения») — в
            /// `accessibilityValue` карты, только Debug.
            private func report(on map: MKMapView) {
                let rival = layers.first { $0.key.group.relation == .rival && !$0.key.isEdge }?.value
                let color = rival.map { overlay in overlay.content.withLock { $0.color.hexString } } ?? "—"
                map.accessibilityValue =
                    "\(model.coloring.rawValue) слоёв=\(layers.count) перекрасок=\(repaints) соперник=\(color)"
            }
        #endif

        /// Порядок слоёв — заливки под кромками, от призраков к своей: кромка своей сверху. При равном — по цвету
        /// и уровню: одинаковые данные дают одинаковую карту.
        private static func ordered(_ keys: some Sequence<LandLayerOverlay.Key>) -> [LandLayerOverlay.Key] {
            let order: [LandRelation] = [.lost, .rival, .mine]
            func rank(_ key: LandLayerOverlay.Key) -> (Int, Int, Int, Int) {
                (
                    key.isEdge ? 1 : 0, order.firstIndex(of: key.group.relation) ?? 0, key.group.colorIndex,
                    key.group.level ?? 0
                )
            }
            return keys.sorted { rank($0) < rank($1) }
        }

        private static func points(_ ring: [Coordinate]) -> [MKMapPoint] {
            ring.map { MKMapPoint(CLLocationCoordinate2D(latitude: $0.latitude, longitude: $0.longitude)) }
        }

        private static func bounds(_ points: [MKMapPoint]) -> MKMapRect {
            guard let first = points.first else { return .null }
            var minX = first.x
            var maxX = first.x
            var minY = first.y
            var maxY = first.y
            for point in points {
                minX = min(minX, point.x)
                maxX = max(maxX, point.x)
                minY = min(minY, point.y)
                maxY = max(maxY, point.y)
            }
            return MKMapRect(x: minX, y: minY, width: maxX - minX, height: maxY - minY)
        }

        // MARK: - MKMapViewDelegate

        func mapView(_ mapView: MKMapView, rendererFor overlay: any MKOverlay) -> MKOverlayRenderer {
            if overlay === fogOverlay {
                return fogRenderer
            }
            if let layer = overlay as? LandLayerOverlay {
                return LandLayerRenderer(overlay: layer)
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

/// Слой земли одной группы — оверлей на весь мир, содержимое которого меняется: куски (новые тайлы), цвет и штрих
/// (окраска, тема), видимость (слой карты). Читают его потоки отрисовки MapKit — всё под блокировкой.
final class LandLayerOverlay: NSObject, MKOverlay, @unchecked Sendable {
    struct Key: Hashable, Sendable {
        var group: LandGroup
        var isEdge: Bool
    }

    struct Content: Sendable {
        /// Кусок заливки: кольца (внешнее и дыры) в точках карты.
        struct Fill: Sendable {
            var bounds: MKMapRect
            var rings: [[MKMapPoint]]
        }

        /// Линия кромки.
        struct Line: Sendable {
            var bounds: MKMapRect
            var points: [MKMapPoint]
        }

        enum Shapes: Sendable {
            case fills([Fill])
            case lines([Line])
        }

        var shapes = Shapes.fills([])
        var visible = true
        var color = RGBA(0, alpha: 0)
        /// Кромка: толщина, штрих и пробел пунктира, свечение — pt на экране.
        var lineWidth = 0.0
        var dash: [Double] = []
        var glow = 0.0
    }

    let key: Key
    let content = OSAllocatedUnfairLock(initialState: Content())
    let coordinate = CLLocationCoordinate2D(latitude: 52.0976, longitude: 23.7341)
    let boundingMapRect = MKMapRect.world

    init(key: Key) {
        self.key = key
    }
}

/// Рисует слой земли. Заливка — все куски **одним путём** с правилом чёт-нечет: общий край соседних кусков одного
/// участка (сервер режет землю по тайлам) не закрашивается дважды наполовину, и по линии тайла нет светлой нити, как
/// у `MKMultiPolygonRenderer`. Кромка — линии с толщиной и пунктиром в экранных pt (в точках карты — / `zoomScale`),
/// своя ночью — со свечением (тень того же цвета).
final class LandLayerRenderer: MKOverlayRenderer, @unchecked Sendable {
    override func draw(_ mapRect: MKMapRect, zoomScale: MKZoomScale, in context: CGContext) {
        guard let layer = overlay as? LandLayerOverlay else { return }
        let content = layer.content.withLock { $0 }
        guard content.visible, content.color.alpha > 0 else { return }
        let scale = Double(zoomScale)
        // Линия и сглаженный край заходят за кусок — берём куски и чуть за краем части карты.
        let margin = (content.lineWidth + 2 * content.glow + 2) / scale
        let area = mapRect.insetBy(dx: -margin, dy: -margin)
        let path = CGMutablePath()
        let color = CGColor(
            srgbRed: content.color.red, green: content.color.green, blue: content.color.blue,
            alpha: content.color.alpha)
        switch content.shapes {
        case .fills(let fills):
            for fill in fills where fill.bounds.intersects(area) {
                for ring in fill.rings where ring.count > 2 {
                    path.addLines(between: ring.map { point(for: $0) })
                    path.closeSubpath()
                }
            }
            guard !path.isEmpty else { return }
            context.addPath(path)
            context.setFillColor(color)
            context.fillPath(using: .evenOdd)
        case .lines(let lines):
            for line in lines where line.bounds.intersects(area) && line.points.count > 1 {
                path.addLines(between: line.points.map { point(for: $0) })
            }
            guard !path.isEmpty else { return }
            context.addPath(path)
            context.setStrokeColor(color)
            context.setLineWidth(CGFloat(content.lineWidth / scale))
            context.setLineCap(.round)
            context.setLineJoin(.round)
            if !content.dash.isEmpty {
                context.setLineDash(phase: 0, lengths: content.dash.map { CGFloat($0 / scale) })
            }
            if content.glow > 0 {
                // Тень задаётся в пикселях растра, а не в точках карты: радиус в pt × масштаб экрана.
                context.setShadow(offset: .zero, blur: CGFloat(content.glow) * contentScaleFactor, color: color)
            }
            context.strokePath()
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
