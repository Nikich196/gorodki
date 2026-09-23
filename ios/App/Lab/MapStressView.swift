import DesignSystem
import GameCore
import MapKit
import SwiftUI

/// Спайк S4 (PLAN.md, §10): выдержит ли MapKit 5 000 участков и туман при ≥ 55 кадрах в секунду.
/// Участки и туман синтетические, вокруг центра Бреста.
struct MapStressView: View {
    @State private var model = MapStressModel()

    var body: some View {
        MapStressMap(model: model)
            .ignoresSafeArea(edges: .bottom)
            .overlay(alignment: .top) {
                VStack(alignment: .leading, spacing: 8) {
                    Text("\(model.fps) кадр/с · минимум за 10 с: \(model.minimumFps)")
                        .font(.headline.monospacedDigit())
                    Text("\(model.parcelCount) участков · \(model.overlayGroups) групп")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                    Toggle("Туман", isOn: $model.showFog)
                    Toggle("Туман обновляется раз в секунду", isOn: $model.animateFog)
                        .disabled(!model.showFog)
                    Toggle("Перевернуть маску тумана", isOn: $model.flipMask)
                        .disabled(!model.showFog)
                }
                .padding(14)
                .glassEffect(in: .rect(cornerRadius: 20))
                .padding(.horizontal, 16)
            }
            .navigationTitle("Карта (S4)")
            .navigationBarTitleDisplayMode(.inline)
            .onAppear { model.start() }
            .onDisappear { model.stop() }
    }
}

/// Данные и замер кадров для экрана S4.
@MainActor
@Observable
final class MapStressModel {
    private(set) var fps = 0
    private(set) var minimumFps = 0
    private(set) var parcelCount = 0
    private(set) var overlayGroups = 0
    var showFog = true
    var animateFog = true
    var flipMask = true

    let fogOverlay: FogOverlay
    let fogRenderer: FogRenderer
    private(set) var parcels: [MKMultiPolygon] = []
    private(set) var colors: [ObjectIdentifier: UIColor] = [:]

    private var fog = FogLayer()
    private var meter: FrameMeter?
    private var fogTimer: Timer?
    private var recentFps: [Int] = []
    private var walkAngle = 0.0

    static let center = Coordinate(latitude: 52.0976, longitude: 23.6880)

    init() {
        let overlay = FogOverlay()
        fogOverlay = overlay
        fogRenderer = FogRenderer(overlay: overlay)
        buildParcels()
        buildFog()
    }

    func start() {
        let meter = FrameMeter { [weak self] frames in self?.record(frames) }
        self.meter = meter
        fogTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tickFog() }
        }
    }

    func stop() {
        meter?.invalidate()
        meter = nil
        fogTimer?.invalidate()
        fogTimer = nil
    }

    func applyFogSettings() {
        fogRenderer.setFlipMask(flipMask)
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

    // MARK: - Участки: 5 000 многоугольников, 12 цветов × 3 уровня = 36 групп

    private func buildParcels() {
        var generator = SeededGenerator(seed: 2026)
        let plane = LocalTangentPlane(origin: Self.center)
        var groups: [Int: [MKPolygon]] = [:]
        for _ in 0..<5_000 {
            let cx = Double.random(in: -2_000...2_000, using: &generator)
            let cy = Double.random(in: -2_000...2_000, using: &generator)
            let radius = Double.random(in: 15...45, using: &generator)
            let sides = Int.random(in: 5...9, using: &generator)
            var coordinates: [CLLocationCoordinate2D] = (0..<sides).map { k in
                let angle = 2 * Double.pi * Double(k) / Double(sides)
                let r = radius * Double.random(in: 0.7...1.0, using: &generator)
                let c = plane.unproject(PlanarPoint(east: cx + r * cos(angle), north: cy + r * sin(angle)))
                return CLLocationCoordinate2D(latitude: c.latitude, longitude: c.longitude)
            }
            let group = Int.random(in: 0..<(PlayerColor.allCases.count * 3), using: &generator)
            groups[group, default: []].append(MKPolygon(coordinates: &coordinates, count: coordinates.count))
        }

        parcels = groups.keys.sorted().compactMap { key in
            guard let polygons = groups[key] else { return nil }
            let multi = MKMultiPolygon(polygons)
            let player = PlayerColor.allCases[key % PlayerColor.allCases.count]
            let level = key / PlayerColor.allCases.count + 1
            // Уровень — насыщенностью (PLAN.md, §6.3).
            colors[ObjectIdentifier(multi)] = UIColor(player.color).withAlphaComponent(0.25 + 0.15 * Double(level))
            return multi
        }
        parcelCount = 5_000
        overlayGroups = parcels.count
    }

    // MARK: - Туман: синтетические прогулки и «таяние» раз в секунду

    private func buildFog() {
        var generator = SeededGenerator(seed: 7)
        let plane = LocalTangentPlane(origin: Self.center)
        for _ in 0..<40 {
            var x = Double.random(in: -3_000...3_000, using: &generator)
            var y = Double.random(in: -3_000...3_000, using: &generator)
            var heading = Double.random(in: 0...(2 * .pi), using: &generator)
            var previous = plane.unproject(PlanarPoint(east: x, north: y))
            for _ in 0..<200 {
                heading += Double.random(in: -0.4...0.4, using: &generator)
                x += 8 * cos(heading)
                y += 8 * sin(heading)
                let next = plane.unproject(PlanarPoint(east: x, north: y))
                fog.reveal(from: previous, to: next)
                previous = next
            }
        }
        fogRenderer.update(fog)
    }

    private func tickFog() {
        guard showFog, animateFog else { return }
        walkAngle += 0.05
        let plane = LocalTangentPlane(origin: Self.center)
        let point = plane.unproject(PlanarPoint(east: 300 * cos(walkAngle), north: 300 * sin(walkAngle)))
        fog.reveal(around: point)
        fogRenderer.update(fog)
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

/// Предсказуемый генератор случайных чисел: одна и та же «карта» при каждом запуске.
private struct SeededGenerator: RandomNumberGenerator {
    private var state: UInt64

    init(seed: UInt64) {
        state = seed &+ 0x9E37_79B9_7F4A_7C15
    }

    mutating func next() -> UInt64 {
        // SplitMix64.
        state &+= 0x9E37_79B9_7F4A_7C15
        var z = state
        z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
        z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
        return z ^ (z >> 31)
    }
}

/// Сама карта: MKMapView с участками и туманом.
private struct MapStressMap: UIViewRepresentable {
    let model: MapStressModel

    func makeCoordinator() -> Coordinator { Coordinator(model: model) }

    func makeUIView(context: Context) -> MKMapView {
        let map = MKMapView()
        let configuration = MKStandardMapConfiguration(elevationStyle: .flat, emphasisStyle: .muted)
        configuration.pointOfInterestFilter = .excludingAll
        map.preferredConfiguration = configuration
        map.delegate = context.coordinator
        map.addOverlays(model.parcels, level: .aboveRoads)
        map.addOverlay(model.fogOverlay, level: .aboveRoads)
        let center = CLLocationCoordinate2D(
            latitude: MapStressModel.center.latitude, longitude: MapStressModel.center.longitude)
        map.setRegion(
            MKCoordinateRegion(center: center, latitudinalMeters: 3_000, longitudinalMeters: 3_000), animated: false)
        return map
    }

    func updateUIView(_ map: MKMapView, context: Context) {
        let hasFog = map.overlays.contains { $0 === model.fogOverlay }
        if model.showFog, !hasFog {
            map.addOverlay(model.fogOverlay, level: .aboveRoads)
        } else if !model.showFog, hasFog {
            map.removeOverlay(model.fogOverlay)
        }
        model.applyFogSettings()
    }

    @MainActor
    final class Coordinator: NSObject, MKMapViewDelegate {
        let model: MapStressModel

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
            return MKOverlayRenderer(overlay: overlay)
        }
    }
}
