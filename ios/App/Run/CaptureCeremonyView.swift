import DesignSystem
import GameCore
import MapKit
import SwiftUI
import Sync

/// Церемония захвата в две фазы (PLAN.md, §6.8; решение 25.09, пункт 6; утверждённый дизайн — «Лаборатория →
/// Дизайн», `DesignLabCeremonies.swift`). Фаза 1 — сразу по петле: обводка по следу, заливка расходится от точки
/// замыкания, частицы цвета игрока, «≈ +1,2 га» — за 1,9 с (≤ 2 с). Фаза 2 — решение сервера, без повторной анимации:
/// галочка «подтверждено · 12 480 м² · 125 соток» с отскоком и хаптикой или «не засчитана · причина». На церемонии
/// тумана нет. «Уменьшить движение» — итог сразу.
struct CaptureCeremonyView: View {
    let item: CeremonyItem
    let ring: [Coordinate]
    let player: PlayerColor
    /// Счётчик решений модели: растёт — галочка отскакивает.
    let decisions: Int
    let onContinue: () -> Void
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var camera: MapCameraPosition = .automatic
    /// Кольцо в точках экрана карты (`MapProxy.convert`); пусто — ещё не пересчитано.
    @State private var points: [CGPoint] = []
    /// Запуск чисел — при появлении; запуск контура — когда готовы экранные точки.
    @State private var play = 0
    @State private var loopPlay = 0
    @State private var leaving = false

    var body: some View {
        ZStack {
            MapReader { proxy in
                Map(position: $camera, interactionModes: []) {
                    if points.count < 3, ring.count >= 3 {
                        // Пока экранные точки не готовы — контур средствами MapKit, без анимации.
                        MapPolygon(coordinates: ring.map(\.location))
                            .foregroundStyle(player.fillColor(.three))
                            .stroke(player.edgeColor, lineWidth: 3)
                    }
                }
                .mapStyle(.standard(elevation: .flat, emphasis: .muted, pointsOfInterest: .excludingAll))
                .mapControlVisibility(.hidden)
                .overlay {
                    if points.count >= 3 {
                        CeremonyLoop(points: points, player: player, play: loopPlay, reduceMotion: reduceMotion)
                    }
                }
                .onMapCameraChange(frequency: .onEnd) { _ in
                    convert(proxy)
                }
                .task(id: ring) {
                    frame()
                    // Экранные точки появляются, когда карта разложена: несколько попыток, пока `convert` не ответит.
                    for _ in 0..<15 where points.count < 3 {
                        try? await Task.sleep(for: .milliseconds(120))
                        convert(proxy)
                    }
                }
            }
            .ignoresSafeArea()

            VStack(spacing: 0) {
                Label("Петля замкнута · \(item.title)", systemImage: "figure.run")
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Palette.uiInk.color)
                    .padding(.horizontal, 14)
                    .padding(.vertical, 9)
                    .capsuleGlass()
                    .padding(.top, 8)
                Spacer(minLength: 0)
                CeremonyCard(item: item, decisions: decisions, play: play, reduceMotion: reduceMotion) {
                    withAnimation(Motion.captureCardDismiss.unlessReduceMotion(reduceMotion)) { leaving = true }
                    Task {
                        if !reduceMotion {
                            try? await Task.sleep(for: .seconds(MotionSpec.Capture.cardDismissDuration))
                        }
                        onContinue()
                    }
                }
                .offset(y: leaving ? 420 : 0)
                .opacity(leaving ? 0 : 1)
                .padding(.horizontal, Metrics.panelInset)
                .padding(.bottom, 4)
            }
        }
        .background(Palette.mapLand.color)
        .onChange(of: points.count >= 3) { _, ready in
            if ready, loopPlay == 0 { loopPlay = 1 }
        }
        .onAppear { play = 1 }
    }

    /// Камера — на петлю, с запасом; контур выше карточки.
    private func frame() {
        guard let region = TrackGeometry.region(ring, padding: 2.0, shiftDown: 0.3) else { return }
        camera = .region(region)
        points = []
    }

    private func convert(_ proxy: MapProxy) {
        guard ring.count >= 3 else { return }
        let converted = ring.compactMap { proxy.convert($0.location, to: .local) }
        if converted.count == ring.count {
            points = converted
        }
    }
}

// MARK: - Контур

private struct LoopValues: Sendable {
    var outline = 0.0
    var spread = 0.0
    /// Кольцо-импульс: 0 — начало, 1 — растаяло.
    var ring = 0.0
    /// Частицы: 0…1 их жизни.
    var burst = 0.0

    static let final = LoopValues(outline: 1, spread: 1, ring: 1, burst: 1)
}

/// Контур петли поверх карты: обводка по следу, заливка расходится кругом от точки замыкания, кольцо-импульс и частицы
/// цвета игрока — по числам `MotionSpec.Capture` (как в «Лаборатории → Дизайн»).
private struct CeremonyLoop: View {
    let points: [CGPoint]
    let player: PlayerColor
    let play: Int
    let reduceMotion: Bool

    var body: some View {
        if reduceMotion {
            LoopLayer(points: points, player: player, values: .final)
        } else {
            KeyframeAnimator(initialValue: LoopValues(), trigger: play) { values in
                LoopLayer(points: points, player: player, values: values)
            } keyframes: { _ in
                let spec = MotionSpec.Capture.self
                KeyframeTrack(\.outline) {
                    MoveKeyframe(0)
                    LinearKeyframe(1, duration: spec.outlineDuration, timingCurve: Motion.captureOutlineCurve)
                }
                KeyframeTrack(\.spread) {
                    MoveKeyframe(0)
                    LinearKeyframe(0, duration: spec.fillStart)
                    SpringKeyframe(1, duration: spec.fillEnd - spec.fillStart, spring: Motion.captureFill)
                }
                KeyframeTrack(\.ring) {
                    MoveKeyframe(0)
                    LinearKeyframe(0, duration: spec.ringStart)
                    LinearKeyframe(1, duration: spec.ringEnd - spec.ringStart, timingCurve: .easeOut)
                }
                KeyframeTrack(\.burst) {
                    MoveKeyframe(0)
                    LinearKeyframe(0, duration: spec.ringStart)
                    LinearKeyframe(1, duration: spec.total - spec.ringStart, timingCurve: .easeOut)
                }
            }
        }
    }
}

private struct LoopLayer: View {
    let points: [CGPoint]
    let player: PlayerColor
    let values: LoopValues

    var body: some View {
        let closing = points.last ?? .zero
        let shape = RingShape(points: points)
        GeometryReader { proxy in
            let reach = Self.reach(from: closing, in: proxy.size)
            let pulse = MotionSpec.Capture.ringScale
            ZStack {
                shape
                    .fill(player.fillColor(.three))
                    .mask {
                        Circle()
                            .frame(width: 2 * reach, height: 2 * reach)
                            .scaleEffect(values.spread)
                            .position(closing)
                    }
                shape
                    .trim(from: 0, to: values.outline)
                    .stroke(
                        player.edgeColor, style: StrokeStyle(lineWidth: 3.5, lineCap: .round, lineJoin: .round))
                Circle()
                    .stroke(player.edgeColor, lineWidth: 2)
                    .frame(width: 40, height: 40)
                    .scaleEffect(pulse.lowerBound + (pulse.upperBound - pulse.lowerBound) * values.ring)
                    .opacity(values.ring <= 0 ? 0 : values.ring < 0.2 ? values.ring / 0.2 : (1 - values.ring) / 0.8)
                    .position(closing)
                Particles(origin: closing, player: player, progress: values.burst)
            }
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

    /// Радиус круга, который из `point` накрывает всю рамку.
    private static func reach(from point: CGPoint, in size: CGSize) -> CGFloat {
        let corners = [
            CGPoint.zero, CGPoint(x: size.width, y: 0), CGPoint(x: 0, y: size.height),
            CGPoint(x: size.width, y: size.height),
        ]
        return corners.map { hypot($0.x - point.x, $0.y - point.y) }.max() ?? 0
    }
}

/// Контур по точкам экрана: кривая через середины сторон — гладкая, как след после сглаживания; начинается
/// и кончается в точке замыкания (последняя точка кольца).
private struct RingShape: Shape {
    let points: [CGPoint]

    nonisolated func path(in rect: CGRect) -> Path {
        var path = Path()
        guard points.count >= 3, let closing = points.last else { return path }
        path.move(to: closing)
        for index in points.indices.dropLast() {
            let next = points[index + 1]
            path.addQuadCurve(
                to: CGPoint(x: (points[index].x + next.x) / 2, y: (points[index].y + next.y) / 2),
                control: points[index])
        }
        path.addLine(to: closing)
        path.closeSubpath()
        return path
    }
}

/// Частицы цвета игрока из точки замыкания: разлетаются и гаснут (≈1,4 с, в пределах 2 с церемонии). Узор —
/// детерминированный: одна и та же картинка на каждом показе.
private struct Particles: View {
    let origin: CGPoint
    let player: PlayerColor
    /// 0…1 их жизни.
    let progress: Double

    private static let count = 30

    var body: some View {
        Canvas { context, _ in
            guard progress > 0, progress < 1 else { return }
            let colors = [player.color, player.edgeColor, player.fillColor(.three), FogStyle.edge.color]
            for index in 0..<Self.count {
                let angle = Double(index) * 2.399_963  // золотой угол — ровный разлёт без случайности
                let speed = 70 + Double((index * 37) % 90)
                let distance = speed * progress * (1.4 - 0.4 * progress)
                let size = 3 + Double(index % 4)
                let point = CGPoint(x: origin.x + cos(angle) * distance, y: origin.y + sin(angle) * distance)
                let rect = CGRect(x: point.x - size / 2, y: point.y - size / 2, width: size, height: size)
                context.opacity = max(0, 1 - progress * 1.05)
                context.fill(Path(ellipseIn: rect), with: .color(colors[index % colors.count]))
            }
        }
    }
}

// MARK: - Карточка

/// Стеклянная карточка церемонии: число, статус и «Продолжить забег».
private struct CeremonyCard: View {
    let item: CeremonyItem
    let decisions: Int
    let play: Int
    let reduceMotion: Bool
    let onContinue: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Петля замкнута")
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(Palette.uiInk2.color)
            number
            status
            Button(action: onContinue) {
                Text("Продолжить забег")
                    .font(.headline)
                    .frame(maxWidth: .infinity, minHeight: 36)
            }
            .buttonStyle(.glass)
            .controlSize(.large)
        }
        .foregroundStyle(Palette.uiInk.color)
        .padding(20)
        .panelGlass()
    }

    /// «≈ +1,2 га» пружиной в 0,75 с → после решения «+1,25 га».
    @ViewBuilder
    private var number: some View {
        let applied = item.decision?.applied == true
        let value = applied ? item.decision?.takenSquareMeters ?? 0 : item.estimatedSquareMeters
        let text = applied ? CeremonyText.taken(value) : CeremonyText.estimate(value)
        let hectares = text.replacingOccurrences(of: NumberText.unitSeparator + "га", with: "")
        let number = HStack(alignment: .firstTextBaseline, spacing: 4) {
            Text(hectares)
                .font(.role(.ceremony))
                .contentTransition(.numericText(value: value))
            Text("га")
                .font(.role(.ceremonyUnit))
        }
        .lineLimit(1)
        .minimumScaleFactor(0.6)
        .opacity(item.decision?.applied == false ? 0.55 : 1)
        .animation(Motion.numericAppear.unlessReduceMotion(reduceMotion), value: applied)
        if reduceMotion {
            number
        } else {
            number.keyframeAnimator(initialValue: 0.0, trigger: play) { content, appear in
                content
                    .opacity(play == 0 ? 0 : min(max(appear, 0), 1))
                    .scaleEffect(0.92 + 0.08 * appear, anchor: .leading)
                    .offset(y: 12 * (1 - appear))
            } keyframes: { _ in
                KeyframeTrack {
                    LinearKeyframe(0, duration: MotionSpec.Capture.estimateAt)
                    SpringKeyframe(
                        1, duration: MotionSpec.Capture.estimateSpring.duration, spring: Motion.captureEstimate)
                }
            }
        }
    }

    @ViewBuilder
    private var status: some View {
        Group {
            if let decision = item.decision {
                Label {
                    Text(decision.applied ? CeremonyStatus.confirmed(decision.takenSquareMeters) : item.status)
                } icon: {
                    Image(systemName: decision.applied ? "checkmark.seal.fill" : "xmark.octagon.fill")
                        .symbolEffect(.bounce, value: decisions)
                }
                .transition(.move(edge: .bottom).combined(with: .opacity))
            } else {
                Label {
                    Text(CeremonyText.checking)
                } icon: {
                    ProgressView()
                        .controlSize(.small)
                }
                .transition(.opacity)
            }
        }
        .font(.subheadline.weight(.semibold))
        .lineLimit(2)
        .minimumScaleFactor(0.8)
        .animation(Motion.numericRoll.unlessReduceMotion(reduceMotion), value: item.decision)
    }
}

/// «подтверждено · 12 480 м² · 125 соток» — сотки по-русски из каталога строк (`CountText`).
enum CeremonyStatus {
    static func confirmed(_ takenSquareMeters: Double) -> String {
        let sotki = Int(AreaUnits.sotki(fromSquareMeters: takenSquareMeters).rounded())
        return CeremonyText.confirmed + " · " + NumberText.squareMeters(takenSquareMeters) + " · "
            + CountText.sotki(sotki)
    }
}
