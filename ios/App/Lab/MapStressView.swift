import DesignSystem
import GameCore
import MapKit
import SwiftUI

/// Стенд S4 (PLAN.md §10, «Пробная сборка»): выдержит ли MapKit 5 000 участков и туман на 300 тайлах при ≥ 55 кадрах
/// в секунду (S4-перф, S4-туман), читаются ли дорожки Арены, в том числе под туманом (S4-вид), и нет ли швов на краях
/// тайлов (S13, «снимок границ без швов»). Карта синтетическая — `MapStressScene`.
struct MapStressView: View {
    @State private var model = MapStressModel()
    @State private var showsLayers = true

    var body: some View {
        MapStressMap(model: model)
            .ignoresSafeArea(edges: .bottom)
            .overlay(alignment: .top) {
                VStack(alignment: .leading, spacing: 8) {
                    Text("\(model.fps) кадр/с · минимум за 10 с: \(model.minimumFps)")
                        .font(.headline.monospacedDigit())
                    Text(verbatim: model.summary)
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                    DisclosureGroup("Слои", isExpanded: $showsLayers) {
                        VStack(alignment: .leading, spacing: 8) {
                            Picker("Место", selection: $model.preset) {
                                ForEach(MapStressPreset.allCases) { preset in
                                    Text(preset.title).tag(preset)
                                }
                            }
                            .pickerStyle(.segmented)
                            Toggle("Участки", isOn: $model.showParcels)
                            Toggle("Линии границ", isOn: $model.showBorders)
                            Toggle("Туман", isOn: $model.showFog)
                            if model.showFog {
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(
                                        verbatim: "Непрозрачность тумана: "
                                            + NumberText.decimal(model.fogOpacity, fractionDigits: 2))
                                    Slider(value: $model.fogOpacity, in: MapStressModel.fogOpacityRange)
                                }
                                Toggle("Туман обновляется раз в секунду", isOn: $model.animateFog)
                                Toggle("Перевернуть маску тумана", isOn: $model.flipMask)
                            }
                        }
                        .padding(.top, 6)
                    }
                    .font(.subheadline)
                }
                .padding(14)
                .glassEffect(in: .rect(cornerRadius: 20))
                .padding(.horizontal, 16)
            }
            .navigationTitle("Карта (S4)")
            .navigationBarTitleDisplayMode(.inline)
            .onAppear { model.start() }
            .onDisappear { model.stop() }
            .onChange(of: model.preset) { model.load() }
    }
}

/// Данные и замер кадров для экрана S4.
@MainActor
@Observable
final class MapStressModel {
    /// Пределы непрозрачности тумана — из плана (PLAN.md §6.4: «альфа 0,6–0,8, калибруется на устройстве»).
    static let fogOpacityRange = 0.6...0.8
    private static let initialPreset = MapStressPreset.arena

    private(set) var fps = 0
    private(set) var minimumFps = 0
    private(set) var summary = "Готовлю карту…"
    var preset = MapStressModel.initialPreset
    var showParcels = true
    var showBorders = true
    var showFog = true
    var animateFog = true
    var flipMask = true
    var fogOpacity = FogRenderer.defaultOpacity

    let fogOverlay: FogOverlay
    let fogRenderer: FogRenderer
    /// Заливки: один `MKMultiPolygon` на группу «цвет — уровень» из кусков всех тайлов (PLAN.md, D4).
    private(set) var fills: [MKMultiPolygon] = []
    /// Линии границ: контуры целых участков, одна `MKMultiPolyline` на группу.
    private(set) var borders: [MKMultiPolyline] = []
    private(set) var colors: [ObjectIdentifier: UIColor] = [:]
    /// Номер показанной сцены: карта перестраивает слои и камеру, когда он меняется. 0 — сцены ещё нет.
    private(set) var sceneID = 0
    private(set) var camera: MKCoordinateRegion

    private var fog = FogLayer()
    private var fogCenter: Coordinate
    private var meter: FrameMeter?
    private var fogTimer: Timer?
    private var building: Task<Void, Never>?
    private var recentFps: [Int] = []
    private var walkAngle = 0.0

    init() {
        let overlay = FogOverlay()
        fogOverlay = overlay
        fogRenderer = FogRenderer(overlay: overlay)
        camera = Self.region(for: Self.initialPreset)
        fogCenter = Self.initialPreset.center
    }

    /// Какие слои должны быть на карте.
    struct Layers: Equatable {
        var sceneID: Int
        var parcels: Bool
        var borders: Bool
        var fog: Bool
    }

    var layers: Layers { Layers(sceneID: sceneID, parcels: showParcels, borders: showBorders, fog: showFog) }

    func start() {
        let meter = FrameMeter { [weak self] frames in self?.record(frames) }
        self.meter = meter
        fogTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tickFog() }
        }
        if sceneID == 0 {
            load()
        }
    }

    func stop() {
        meter?.invalidate()
        meter = nil
        fogTimer?.invalidate()
        fogTimer = nil
    }

    /// Построить сцену выбранного места. Сцена считается вне главного потока (5 000 участков, туман на 300 тайлах),
    /// объекты MapKit собираются на нём.
    func load() {
        let preset = self.preset
        let colors = PlayerColor.allCases.count
        summary = "Готовлю карту…"
        building?.cancel()
        building = Task {
            let scene = await Task.detached(priority: .userInitiated) {
                MapStressScene.make(preset: preset, colors: colors)
            }.value
            guard !Task.isCancelled, preset == self.preset else { return }
            apply(scene)
        }
    }

    func applyFogSettings() {
        fogRenderer.setFlipMask(flipMask)
        fogRenderer.setOpacity(fogOpacity)
    }

    /// Окно камеры — вся область участков.
    private static func region(for preset: MapStressPreset) -> MKCoordinateRegion {
        MKCoordinateRegion(
            center: CLLocationCoordinate2D(latitude: preset.center.latitude, longitude: preset.center.longitude),
            latitudinalMeters: 2 * preset.radiusMeters, longitudinalMeters: 2 * preset.radiusMeters)
    }

    // MARK: - Сцена

    private func apply(_ scene: MapStressScene) {
        let colorCount = PlayerColor.allCases.count
        var polygons: [Int: [MKPolygon]] = [:]
        for piece in scene.pieces {
            var coordinates = piece.ring.map { CLLocationCoordinate2D(latitude: $0.latitude, longitude: $0.longitude) }
            polygons[scene.outlines[piece.parcel].group, default: []]
                .append(MKPolygon(coordinates: &coordinates, count: coordinates.count))
        }
        var lines: [Int: [MKPolyline]] = [:]
        for outline in scene.outlines {
            var coordinates = outline.ring.map {
                CLLocationCoordinate2D(latitude: $0.latitude, longitude: $0.longitude)
            }
            if let first = coordinates.first {
                coordinates.append(first)  // линия замыкается сама только у многоугольника
            }
            lines[outline.group, default: []].append(MKPolyline(coordinates: &coordinates, count: coordinates.count))
        }

        var colors: [ObjectIdentifier: UIColor] = [:]
        func color(of group: Int) -> PlayerColor { PlayerColor.allCases[group % colorCount] }
        fills = polygons.keys.sorted().compactMap { group in
            guard let pieces = polygons[group] else { return nil }
            let multi = MKMultiPolygon(pieces)
            let level = group / colorCount + 1
            // Уровень — насыщенностью (PLAN.md, §6.3).
            colors[ObjectIdentifier(multi)] = UIColor(color(of: group).color)
                .withAlphaComponent(0.25 + 0.15 * Double(level))
            return multi
        }
        borders = lines.keys.sorted().compactMap { group in
            guard let outlines = lines[group] else { return nil }
            let multi = MKMultiPolyline(outlines)
            // Толщина и узор границ — решение дизайна (PLAN.md, §6.3); стенду хватает сплошной линии цвета игрока.
            colors[ObjectIdentifier(multi)] = UIColor(color(of: group).color)
            return multi
        }
        self.colors = colors

        fog = scene.fog
        fogCenter = scene.preset.center
        fogRenderer.update(fog)
        camera = Self.region(for: scene.preset)
        sceneID += 1
        summary = [
            CountText.parcels(scene.outlines.count) + " → " + CountText.pieces(scene.pieces.count),
            CountText.groups(fills.count),
            "туман: " + CountText.tiles(fog.tiles.count),
        ].joined(separator: " · ")
    }

    // MARK: - Кадры

    private func record(_ frames: Int) {
        fps = frames
        recentFps.append(frames)
        if recentFps.count > 10 {
            recentFps.removeFirst()
        }
        minimumFps = recentFps.min() ?? frames
    }

    // MARK: - Туман: «таяние» раз в секунду

    private func tickFog() {
        guard showFog, animateFog, sceneID > 0 else { return }
        walkAngle += 0.05
        let plane = LocalTangentPlane(origin: fogCenter)
        let point = plane.unproject(PlanarPoint(east: 300 * cos(walkAngle), north: 300 * sin(walkAngle)))
        let before = fog
        fog.reveal(around: point)
        // Перерисовать только изменившиеся тайлы: у нетронутых тот же буфер, сравнение мгновенное.
        fogRenderer.update(fog, changed: Set(fog.tiles.keys.filter { before.tiles[$0] != fog.tiles[$0] }))
    }
}

/// Считает кадры за секунду по `CADisplayLink`.
@MainActor
private final class FrameMeter: NSObject {
    private var link: CADisplayLink?
    private var frames = 0
    private var windowStart = CACurrentMediaTime()
    private let onSecond: @MainActor (Int) -> Void

    init(onSecond: @escaping @MainActor (Int) -> Void) {
        self.onSecond = onSecond
        super.init()
        let link = CADisplayLink(target: self, selector: #selector(tick))
        link.preferredFrameRateRange = CAFrameRateRange(minimum: 30, maximum: 120, preferred: 120)
        link.add(to: .main, forMode: .common)
        self.link = link
    }

    func invalidate() {
        link?.invalidate()
        link = nil
    }

    @objc private func tick() {
        frames += 1
        let now = CACurrentMediaTime()
        if now - windowStart >= 1 {
            onSecond(frames)
            frames = 0
            windowStart = now
        }
    }
}

/// Сама карта: MKMapView с участками, линиями границ и туманом.
private struct MapStressMap: UIViewRepresentable {
    let model: MapStressModel

    func makeCoordinator() -> Coordinator { Coordinator(model: model) }

    func makeUIView(context: Context) -> MKMapView {
        let map = MKMapView()
        let configuration = MKStandardMapConfiguration(elevationStyle: .flat, emphasisStyle: .muted)
        configuration.pointOfInterestFilter = .excludingAll
        map.preferredConfiguration = configuration
        map.delegate = context.coordinator
        map.setRegion(model.camera, animated: false)
        return map
    }

    func updateUIView(_ map: MKMapView, context: Context) {
        let layers = model.layers
        let coordinator = context.coordinator
        if coordinator.applied != layers {
            // Порядок слоёв: заливки, над ними линии, сверху туман (PLAN.md, §6.4 — туман над дорогами). Перестройка —
            // только когда слои правда поменялись: каждая стоит пересчёта 5 000 кусков.
            map.removeOverlays(map.overlays)
            if layers.parcels {
                map.addOverlays(model.fills, level: .aboveRoads)
            }
            if layers.borders {
                map.addOverlays(model.borders, level: .aboveRoads)
            }
            if layers.fog {
                map.addOverlay(model.fogOverlay, level: .aboveRoads)
            }
            if coordinator.applied?.sceneID != layers.sceneID {
                map.setRegion(model.camera, animated: false)
            }
            coordinator.applied = layers
        }
        model.applyFogSettings()
    }

    @MainActor
    final class Coordinator: NSObject, MKMapViewDelegate {
        let model: MapStressModel
        /// Слои, которые сейчас на карте.
        var applied: MapStressModel.Layers?

        init(model: MapStressModel) {
            self.model = model
        }

        func mapView(_ mapView: MKMapView, rendererFor overlay: MKOverlay) -> MKOverlayRenderer {
            if overlay === model.fogOverlay {
                return model.fogRenderer
            }
            if let multi = overlay as? MKMultiPolygon {
                let renderer = MKMultiPolygonRenderer(multiPolygon: multi)
                renderer.fillColor = model.colors[ObjectIdentifier(multi)] ?? .systemBlue.withAlphaComponent(0.4)
                renderer.lineWidth = 0  // заливки без обводки: так не видно швов на краях тайлов (PLAN.md, D4)
                return renderer
            }
            if let multi = overlay as? MKMultiPolyline {
                let renderer = MKMultiPolylineRenderer(multiPolyline: multi)
                renderer.strokeColor = model.colors[ObjectIdentifier(multi)] ?? .systemBlue
                renderer.lineWidth = 1
                return renderer
            }
            return MKOverlayRenderer(overlay: overlay)
        }
    }
}
