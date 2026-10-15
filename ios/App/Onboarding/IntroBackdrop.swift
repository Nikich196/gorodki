import DesignSystem
import GameCore
import MapKit
import SwiftUI

/// Фон интро (Никита 15.10: «вход сделать индивидуальным, чтобы отличался от других»): настоящая карта Бреста у БрГТУ —
/// Арена — и на ней живая сцена каждой карточки теми же токенами, что игровая карта: петля обегает квартал и заливается
/// цветом игрока, туман рассеивается вдоль пройденного пути, кварталы в цветах соперников и спорная кромка «бегущими
/// муравьями». Карта неподвижна и касаний не принимает — это картинка, а не карта игры. «Уменьшить движение» — конечный
/// кадр каждой сцены.
struct IntroBackdrop: View {
    let page: Int
    /// Сколько сверху занимает надпись и снизу — панель: камера центрирует сцену между ними, подпись «Карты» — над панелью.
    var topInset: CGFloat = 0
    var bottomInset: CGFloat = 0
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var startedAt = Date.now
    /// Растёт, когда камера встала: до этого точки на экран не переводятся, и сцена перерисовывается.
    @State private var cameraTick = 0
    @State private var position = MapCameraPosition.camera(IntroScene.camera)
    /// Начальная камера встала над сценой.
    @State private var cameraSettled = false
    /// Центр камеры — на экране он не посередине: MapKit центрирует по видимой области над панелью.
    @State private var cameraCenter: CLLocationCoordinate2D?

    var body: some View {
        MapReader { proxy in
            Map(position: $position, interactionModes: [])
                .mapStyle(.standard(elevation: .flat, emphasis: .muted, pointsOfInterest: .excludingAll))
                .mapControlVisibility(.hidden)
                .safeAreaPadding(.bottom, bottomInset)
                .onMapCameraChange(frequency: .onEnd) { context in
                    cameraTick += 1
                    // Переводить точки можно, только когда камера уже над сценой: до начальной камеры MapKit показывает весь
                    // мир, и сдвиг увёл бы карту за тысячи километров.
                    if IntroScene.isNear(context.camera.centerCoordinate) {
                        cameraSettled = true
                        cameraCenter = context.camera.centerCoordinate
                        center(with: proxy)
                    }
                }
                .onChange(of: [size.height, topInset, bottomInset]) {
                    if cameraSettled {
                        center(with: proxy)
                    }
                }
                .overlay {
                    TimelineView(.animation(paused: reduceMotion)) { timeline in
                        IntroSceneView(
                            page: page,
                            elapsed: reduceMotion ? nil : timeline.date.timeIntervalSince(startedAt),
                            theme: Theme(colorScheme),
                            project: { coordinate in
                                proxy.convert(
                                    CLLocationCoordinate2D(
                                        latitude: coordinate.latitude, longitude: coordinate.longitude),
                                    to: .local)
                            })
                    }
                    .id(cameraTick)
                    .allowsHitTesting(false)
                }
        }
        .overlay(alignment: .top) {
            // Под надписью «ГОРОДКИ» карта уходит в фон экрана — надпись читается на любых улицах.
            LinearGradient(
                colors: [Brand.base[Theme(colorScheme)].color, Brand.base[Theme(colorScheme)].color.opacity(0)],
                startPoint: .top, endPoint: .bottom
            )
            .frame(height: topInset + 70)
            .allowsHitTesting(false)
        }
        .onChange(of: page) { startedAt = .now }
        .onGeometryChange(for: CGSize.self) {
            $0.size
        } action: {
            size = $0
        }
        .accessibilityHidden(true)
    }

    @State private var size = CGSize.zero

    /// Сдвигает камеру так, чтобы середина сцены стояла посередине между надписью и панелью (и после смены их размеров). Сдвиг считается по тому, где сцена
    /// оказалась на экране: отступ снизу (подпись «Карты» над панелью) MapKit учитывает в начальной камере по-своему.
    private func center(with proxy: MapProxy) {
        guard size.height > 0, let cameraCenter,
            let scene = proxy.convert(IntroScene.sceneCenter.clLocation, to: .local),
            let pivot = proxy.convert(cameraCenter, to: .local)
        else { return }
        let target = CGPoint(x: size.width / 2, y: topInset + (size.height - topInset - bottomInset) / 2)
        // Новый центр камеры встанет туда, где сейчас `pivot`: точка экрана `shifted` переедет в `pivot`, а сцена — в `target`.
        let shifted = CGPoint(x: pivot.x + scene.x - target.x, y: pivot.y + scene.y - target.y)
        // Уже на месте — камеру не трогаем: иначе каждое её изменение вызывало бы новое.
        guard abs(target.y - scene.y) > 2 || abs(target.x - scene.x) > 2,
            let coordinate = proxy.convert(shifted, from: .local)
        else { return }
        let camera = IntroScene.camera
        position = .camera(
            MapCamera(
                centerCoordinate: coordinate, distance: camera.distance, heading: camera.heading, pitch: camera.pitch))
    }
}

extension Coordinate {
    fileprivate var clLocation: CLLocationCoordinate2D {
        CLLocationCoordinate2D(latitude: latitude, longitude: longitude)
    }
}

/// Геометрия сцен — метры от угла квартала у БрГТУ (тот же угол, что у петли демо, `RunFixture.origin`).
enum IntroScene {
    static let plane = LocalTangentPlane(origin: RunFixture.origin)

    static func point(_ east: Double, _ north: Double) -> Coordinate {
        plane.unproject(PlanarPoint(east: east, north: north))
    }

    /// Середина сцен: её камера ставит посередине между надписью и панелью.
    static let sceneCenter = point(45, 90)

    /// Камера над сценой (в пределах ~1 км), а не над всем миром.
    static func isNear(_ coordinate: CLLocationCoordinate2D) -> Bool {
        abs(coordinate.latitude - sceneCenter.latitude) < 0.01
            && abs(coordinate.longitude - sceneCenter.longitude) < 0.015
    }

    /// Камера над Ареной: видно все кварталы третьей карточки и улицы вокруг.
    static var camera: MapCamera {
        MapCamera(
            centerCoordinate: CLLocationCoordinate2D(latitude: sceneCenter.latitude, longitude: sceneCenter.longitude),
            distance: 1_750, heading: 0, pitch: 0)
    }

    /// Петля карточки «Обеги — и участок твой» — квартал ≈ 104 × 120 м с дрожанием GPS, как у забега демо.
    static let loop = RunFixture.loop

    /// Прогулка карточки тумана: с юго-запада к кварталу, вокруг него и дальше на восток.
    static let walk: [Coordinate] =
        [point(-170, -70), point(-160, 30), point(-90, 52), point(-30, 8)] + RunFixture.track
        + [point(170, 172), point(232, 128), point(262, 30), point(300, -40)]

    /// Земля карточки «Держи свою землю»: своя, соперники, клан нет — и одна спорная.
    static let parcels: [IntroParcel] = [
        IntroParcel(color: .sky, level: .two, relation: .mine, ring: loop),
        IntroParcel(color: .coral, level: .one, relation: .rival, ring: block(128, 8, 236, 118, seed: 1)),
        IntroParcel(color: .forest, level: .three, relation: .rival, ring: block(-150, 16, -24, 140, seed: 2)),
        IntroParcel(color: .violet, level: .two, relation: .rival, ring: block(16, 146, 118, 250, seed: 3)),
        IntroParcel(color: .sun, level: .one, relation: .contested, ring: block(132, 142, 232, 244, seed: 4)),
    ]

    /// Квартал с чуть неровной кромкой — как контур следа, а не квадрат.
    static func block(_ west: Double, _ south: Double, _ east: Double, _ north: Double, seed: Int) -> [Coordinate] {
        let corners = [(west, south), (west, north), (east, north), (east, south)]
        var ring: [Coordinate] = []
        for (index, corner) in corners.enumerated() {
            let next = corners[(index + 1) % corners.count]
            for step in 0..<6 {
                let t = Double(step) / 6
                let wobble = sin(Double(seed * 31 + index * 6 + step) * 1.9) * 2.2
                ring.append(
                    point(corner.0 + (next.0 - corner.0) * t + wobble, corner.1 + (next.1 - corner.1) * t - wobble))
            }
        }
        ring.append(ring[0])
        return ring
    }
}

struct IntroParcel {
    let color: PlayerColor
    let level: TerritoryLevel
    let relation: TerritoryRelation
    let ring: [Coordinate]
}

/// Кадр сцены в момент `elapsed` секунд от открытия карточки; `nil` — конечный кадр («Уменьшить движение»).
private struct IntroSceneView: View {
    let page: Int
    let elapsed: TimeInterval?
    let theme: Theme
    let project: (Coordinate) -> CGPoint?

    var body: some View {
        ZStack {
            switch page {
            case 0: capture
            case 1: fog
            default: rivals
            }
        }
        .transition(.opacity)
    }

    // MARK: - Сцены

    /// Петля обегает квартал (3 с), заливается цветом игрока, всплывает «+1,25 га»; через пару секунд — заново.
    private var capture: some View {
        let t = cycle(period: 7.6, still: 5)
        let draw = ease(ramp(t, 0.3, 3.3))
        let fill = ease(ramp(t, 3.3, 4))
        let chip = ease(ramp(t, 3.7, 4.3))
        let fade = 1 - ramp(t, 6.8, 7.5)
        let loop = path(IntroScene.loop, closed: true)
        let drawn = loop.trimmedPath(from: 0, to: max(draw, 0.0001))
        let color = PlayerColor.sky
        return ZStack {
            loop.fill(color.fill(.two, theme: theme).color).opacity(fill * fade)
            loop.stroke(color.edge[theme].color, lineWidth: TerritoryRelation.mine.edge.width + 0.6)
                .shadow(color: color.edge[theme].color.opacity(theme == .night ? 0.8 : 0), radius: 3)
                .opacity(fill * fade)
            trail(drawn, color: color).opacity((1 - fill) * fade)
            if draw > 0, draw < 1 {
                runner(at: drawn.currentPoint, color: color)
            }
            if let middle = center(of: IntroScene.loop) {
                IntroChip(text: "+1,25 га", systemImage: "checkmark.seal.fill")
                    .scaleEffect(0.85 + 0.15 * chip)
                    .opacity(chip * fade)
                    .position(x: middle.x, y: middle.y)
            }
        }
    }

    /// Туман над Ареной: вдоль пути открывается коридор ~50 м, в конце над кварталом — «+0,80 га тумана»; потом туман
    /// возвращается.
    private var fog: some View {
        let t = cycle(period: 10, still: 7)
        let reveal = ease(ramp(t, 0.5, 6.5))
        let back = ramp(t, 8.8, 9.8)
        let walk = path(IntroScene.walk, closed: false)
        let opened = walk.trimmedPath(from: 0, to: max(reveal, 0.0001))
        let corridor = metersToPoints(50)
        return ZStack {
            Rectangle()
                .fill(FogStyle.haze[theme].color)
                .mask {
                    Rectangle()
                        .overlay {
                            opened.stroke(style: StrokeStyle(lineWidth: corridor, lineCap: .round, lineJoin: .round))
                                .blendMode(.destinationOut)
                                .opacity(1 - back)
                        }
                        .compositingGroup()
                }
            opened.stroke(
                FogStyle.edge[theme].color,
                style: StrokeStyle(lineWidth: corridor + 3, lineCap: .round, lineJoin: .round)
            )
            .mask {
                opened.stroke(style: StrokeStyle(lineWidth: corridor + 3, lineCap: .round, lineJoin: .round))
                    .overlay {
                        opened.stroke(style: StrokeStyle(lineWidth: corridor, lineCap: .round, lineJoin: .round))
                            .blendMode(.destinationOut)
                    }
                    .compositingGroup()
            }
            .opacity(1 - back)
            if reveal > 0, reveal < 1 {
                runner(at: opened.currentPoint, color: .sky)
            }
            if let middle = center(of: IntroScene.loop), reveal >= 1 {
                IntroChip(text: "+0,80 га тумана", systemImage: "cloud.sun.fill")
                    .opacity(1 - back)
                    .position(x: middle.x, y: middle.y)
            }
        }
    }

    /// Кварталы появляются по одному, у спорного кромка — «бегущие муравьи».
    private var rivals: some View {
        let parcels = IntroScene.parcels
        let antsPhase = elapsed.map { -($0 / MotionSpec.Ants.period) * 8 } ?? 0
        return ZStack {
            ForEach(parcels.indices, id: \.self) { index in
                let parcel = parcels[index]
                let appear = elapsed.map { ease(ramp($0, 0.2 + Double(index) * 0.2, 0.75 + Double(index) * 0.2)) } ?? 1
                parcelView(parcel, antsPhase: antsPhase)
                    .opacity(appear)
                    .scaleEffect(0.96 + 0.04 * appear)
            }
            if let middle = center(of: IntroScene.loop) {
                IntroChip(text: "Твоя земля", systemImage: "figure.run")
                    .position(x: middle.x, y: middle.y)
            }
        }
    }

    // MARK: - Детали

    private func parcelView(_ parcel: IntroParcel, antsPhase: Double) -> some View {
        let ring = path(parcel.ring, closed: true)
        let relation = parcel.relation
        let edge = relation.edge
        let ownerEdge = parcel.color.edge[theme]
        return ZStack {
            ring.fill(relation.fill(levelFill: parcel.color.fill(parcel.level, theme: theme), theme: theme).color)
            if let halo = edge.haloWidth {
                ring.stroke(Palette.antsHalo[theme].color, lineWidth: halo + 0.6)
            }
            ring.stroke(
                relation.edgeColor(ownerEdge: ownerEdge, theme: theme).color,
                style: StrokeStyle(
                    lineWidth: edge.width + 0.6, lineJoin: .round, dash: edge.dash.map { CGFloat($0) },
                    dashPhase: relation.hasMarchingAnts ? antsPhase : 0)
            )
            .shadow(color: ownerEdge.color.opacity(relation.glowRadius(theme: theme) > 0 ? 0.8 : 0), radius: 3)
        }
    }

    /// След бегущего: подложка цвета фона и линия цвета игрока (`TrailStyle`).
    private func trail(_ drawn: Path, color: PlayerColor) -> some View {
        ZStack {
            drawn.stroke(
                Palette.uiBackground.color.opacity(0.9),
                style: StrokeStyle(lineWidth: TrailStyle.caseWidth, lineCap: .round, lineJoin: .round))
            drawn.stroke(
                color.edge[theme].color,
                style: StrokeStyle(lineWidth: TrailStyle.width, lineCap: .round, lineJoin: .round))
        }
    }

    @ViewBuilder
    private func runner(at point: CGPoint?, color: PlayerColor) -> some View {
        if let point {
            Circle()
                .fill(color.edge[theme].color)
                .frame(width: 14, height: 14)
                .overlay { Circle().stroke(Palette.uiBackground.color, lineWidth: 3) }
                .shadow(color: .black.opacity(0.25), radius: 3, y: 1)
                .position(x: point.x, y: point.y)
        }
    }

    private func path(_ coordinates: [Coordinate], closed: Bool) -> Path {
        let points = coordinates.compactMap(project)
        var path = Path()
        guard points.count == coordinates.count, points.count > 1 else { return path }
        path.addLines(points)
        if closed {
            path.closeSubpath()
        }
        return path
    }

    private func center(of ring: [Coordinate]) -> CGPoint? {
        let points = ring.compactMap(project)
        guard !points.isEmpty, points.count == ring.count else { return nil }
        return CGPoint(
            x: points.map(\.x).reduce(0, +) / Double(points.count),
            y: points.map(\.y).reduce(0, +) / Double(points.count))
    }

    /// Сколько точек экрана в `meters` метрах на этой камере.
    private func metersToPoints(_ meters: Double) -> CGFloat {
        guard let a = project(IntroScene.point(0, 0)), let b = project(IntroScene.point(100, 0)) else { return 40 }
        return hypot(b.x - a.x, b.y - a.y) / 100 * meters
    }

    /// Время внутри цикла сцены; конечный кадр — `still`.
    private func cycle(period: Double, still: Double) -> Double {
        guard let elapsed else { return still }
        return elapsed.truncatingRemainder(dividingBy: period)
    }

    private func ramp(_ t: Double, _ from: Double, _ to: Double) -> Double {
        min(max((t - from) / (to - from), 0), 1)
    }

    private func ease(_ x: Double) -> Double {
        x * x * (3 - 2 * x)
    }
}

/// Плашка-подсказка на карте интро: стекло, как плашки HUD.
private struct IntroChip: View {
    let text: String
    let systemImage: String

    var body: some View {
        Label(text, systemImage: systemImage)
            .font(.subheadline.weight(.bold))
            .fontDesign(.rounded)
            .foregroundStyle(Palette.uiInk.color)
            .padding(.horizontal, 12)
            .frame(minHeight: 32)
            .capsuleGlass()
            .fixedSize()
    }
}
