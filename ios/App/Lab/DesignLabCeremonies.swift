import DesignSystem
import GameCore
import SwiftUI

// Церемонии «Лаборатории → Дизайн» (PLAN.md, §6.8; docs/design/tokens.md, §7): всё ≤ 2 с, по числам `MotionSpec`.
// «Уменьшить движение» — итог сразу, хаптика остаётся. В `KeyframeAnimator` — только рисунок: кнопки живут снаружи.

// MARK: - Церемония захвата

/// Церемония захвата: обводка по следу, заливка расходится кругом от точки замыкания, «≈ +1,2 га» → «подтверждено»
/// за 1,9 с. Итог держится ярким (L3); «Продолжить забег» — карточка уходит вниз, заливка оседает до L1.
/// Петля здесь условная, поверх настоящей карты; на экране забега контур даст `mapView.convert(_:toPointTo:)`.
struct CaptureSection: View {
    let player: PlayerColor
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    /// Номер показа: смена запускает церемонию заново.
    @State private var replay = 0
    @State private var confirmations = 0
    @State private var settled = false

    var body: some View {
        Section {
            ZStack(alignment: .top) {
                LabMap()
                loop
                    .padding(.top, 28)
                card
                    .frame(maxHeight: .infinity, alignment: .bottom)
            }
            .frame(height: 470)
            .listRowInsets(EdgeInsets())
            .sensoryFeedback(.success, trigger: confirmations)
            .onAppear {
                if replay == 0 {
                    replay = 1
                }
            }
            .task(id: replay) {
                guard replay > 0 else { return }
                if !reduceMotion {
                    try? await Task.sleep(for: .seconds(MotionSpec.Capture.confirmedAt))
                }
                confirmations += 1
            }

            Button("Повторить церемонию", systemImage: "arrow.counterclockwise") {
                var transaction = Transaction()
                transaction.disablesAnimations = true
                withTransaction(transaction) {
                    settled = false
                }
                replay += 1
            }
        } header: {
            Text("Церемония захвата")
        } footer: {
            Text(
                "Обводка 0–0,6 с · заливка от точки замыкания 0,5–1,3 с · «≈» в 0,75 с · «подтверждено» и хаптика "
                    + "в 1,45 с · итог 1,9 с. Итог держится ярким, до L1 участок оседает после «Продолжить забег». "
                    + "На церемонии тумана нет.")
        }
        .listRowBackground(Palette.uiCell.color)
    }

    /// Контур, заливка и кольцо-импульс.
    @ViewBuilder
    private var loop: some View {
        let settled = self.settled
        let reduceMotion = self.reduceMotion
        let fill = settled ? player.fillColor(.one) : player.fillColor(.three)
        let edge = player.edgeColor
        if reduceMotion {
            LoopLayer(values: .final, fill: fill, edge: edge, settled: settled, reduceMotion: true)
        } else {
            KeyframeAnimator(initialValue: LoopValues.final, trigger: replay) { values in
                LoopLayer(values: values, fill: fill, edge: edge, settled: settled, reduceMotion: reduceMotion)
            } keyframes: { _ in
                KeyframeTrack(\.outline) {
                    MoveKeyframe(0)
                    LinearKeyframe(
                        1, duration: MotionSpec.Capture.outlineDuration, timingCurve: Motion.captureOutlineCurve)
                }
                KeyframeTrack(\.spread) {
                    MoveKeyframe(0)
                    LinearKeyframe(0, duration: MotionSpec.Capture.fillStart)
                    SpringKeyframe(
                        1, duration: MotionSpec.Capture.fillEnd - MotionSpec.Capture.fillStart,
                        spring: Motion.captureFill)
                }
                KeyframeTrack(\.ring) {
                    MoveKeyframe(0)
                    LinearKeyframe(0, duration: MotionSpec.Capture.ringStart)
                    LinearKeyframe(
                        1, duration: MotionSpec.Capture.ringEnd - MotionSpec.Capture.ringStart, timingCurve: .easeOut)
                }
            }
        }
    }

    /// Стеклянная карточка: число, статус и «Продолжить забег».
    private var card: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Петля замкнута")
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(Palette.uiInk2.color)
            if reduceMotion {
                CaptureNumbers(values: .final)
            } else {
                KeyframeAnimator(initialValue: CardValues.final, trigger: replay) { values in
                    CaptureNumbers(values: values)
                } keyframes: { _ in
                    KeyframeTrack(\.estimate) {
                        MoveKeyframe(0)
                        LinearKeyframe(0, duration: MotionSpec.Capture.estimateAt)
                        SpringKeyframe(
                            1, duration: MotionSpec.Capture.estimateSpring.duration, spring: Motion.captureEstimate)
                    }
                    KeyframeTrack(\.swap) {
                        MoveKeyframe(0)
                        LinearKeyframe(0, duration: MotionSpec.Capture.confirmedAt)
                        LinearKeyframe(
                            1, duration: MotionSpec.Capture.total - MotionSpec.Capture.confirmedAt,
                            timingCurve: .easeInOut)
                    }
                }
            }
            Button {
                settled = true
            } label: {
                Text("Продолжить забег")
                    .font(.headline)
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.glass)
            .controlSize(.large)
        }
        .padding(18)
        .panelGlass()
        .padding(Metrics.panelInset)
        .offset(y: settled ? 320 : 0)
        .opacity(settled ? 0 : 1)
        .animation(Motion.captureCardDismiss.unlessReduceMotion(reduceMotion), value: settled)
    }
}

/// Пример из макета: петля без куска соперника — 12 480 м² = 1,25 га = 125 соток.
private enum CaptureSample {
    static let squareMeters = 12_480.0

    /// «подтверждено · 12 480 м² · 125 соток».
    static var confirmedStatus: String {
        let sotki = Int(AreaUnits.sotki(fromSquareMeters: squareMeters).rounded())
        return "подтверждено · \(NumberText.squareMeters(squareMeters)) · \(CountText.sotki(sotki))"
    }
}

private struct LoopValues: Sendable {
    var outline = 1.0
    var spread = 1.0
    /// Кольцо-импульс: 0 — начало, 1 — растаяло.
    var ring = 1.0

    static let final = LoopValues()
}

private struct CardValues: Sendable {
    /// «≈ +1,2 га» появилось (пружина — с перелётом за 1).
    var estimate = 1.0
    /// 0 — оценка на сервере, 1 — подтверждено.
    var swap = 1.0

    static let final = CardValues()
}

private struct LoopLayer: View {
    let values: LoopValues
    let fill: Color
    let edge: Color
    let settled: Bool
    let reduceMotion: Bool

    private static let size = CGSize(width: 250, height: 180)

    var body: some View {
        let closing = LoopGeometry.closingPoint(in: Self.size)
        let reach = LoopGeometry.reach(from: closing, in: Self.size)
        let ring = MotionSpec.Capture.ringScale
        ZStack {
            LoopShape()
                .fill(fill)
                .animation(Motion.captureSettle.unlessReduceMotion(reduceMotion), value: settled)
                .mask {
                    Circle()
                        .frame(width: 2 * reach, height: 2 * reach)
                        .scaleEffect(values.spread)
                        .position(closing)
                }
            LoopShape()
                .trim(from: 0, to: values.outline)
                .stroke(edge, style: StrokeStyle(lineWidth: 3, lineCap: .round, lineJoin: .round))
            Circle()
                .stroke(edge, lineWidth: 2)
                .frame(width: 36, height: 36)
                .scaleEffect(ring.lowerBound + (ring.upperBound - ring.lowerBound) * values.ring)
                .opacity(values.ring < 0.2 ? values.ring / 0.2 : (1 - values.ring) / 0.8)
                .position(closing)
        }
        .frame(width: Self.size.width, height: Self.size.height)
        .accessibilityHidden(true)
    }
}

private struct CaptureNumbers: View {
    let values: CardValues

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            ZStack(alignment: .leading) {
                HectareText(prefix: "≈\u{00A0}+", fractionDigits: 1)
                    .opacity(min(max(values.estimate, 0), 1) * (1 - values.swap))
                    .scaleEffect(0.92 + 0.08 * values.estimate, anchor: .leading)
                    .offset(y: 12 * (1 - values.estimate) - 10 * values.swap)
                HectareText(prefix: "+", fractionDigits: 2)
                    .opacity(values.swap)
                    .offset(y: 12 * (1 - values.swap))
            }
            ZStack(alignment: .leading) {
                Text("проверяем петлю на сервере…")
                    .opacity(1 - values.swap)
                Label {
                    Text(verbatim: CaptureSample.confirmedStatus)
                } icon: {
                    Image(systemName: "checkmark.seal.fill")
                }
                .opacity(values.swap)
            }
            .font(.subheadline.weight(.semibold))
            .lineLimit(1)
            .minimumScaleFactor(0.7)
        }
    }
}

/// «+1,25 га»: число крупно, единица мельче.
private struct HectareText: View {
    let prefix: String
    let fractionDigits: Int

    var body: some View {
        let hectares = AreaUnits.hectares(fromSquareMeters: CaptureSample.squareMeters)
        HStack(alignment: .firstTextBaseline, spacing: 4) {
            Text(verbatim: prefix + NumberText.decimal(hectares, fractionDigits: fractionDigits))
                .font(.role(.ceremony))
            Text("га")
                .font(.role(.ceremonyUnit))
        }
        .lineLimit(1)
        .minimumScaleFactor(0.6)
    }
}

/// Условная петля вокруг квартала: сглаженный многоугольник; контур начинается и кончается в точке замыкания.
private struct LoopShape: Shape {
    nonisolated func path(in rect: CGRect) -> Path {
        LoopGeometry.path(in: rect)
    }
}

private enum LoopGeometry {
    /// Вершины в долях рамки.
    static let points = [
        CGPoint(x: 0.16, y: 0.80), CGPoint(x: 0.07, y: 0.42), CGPoint(x: 0.30, y: 0.12), CGPoint(x: 0.64, y: 0.07),
        CGPoint(x: 0.93, y: 0.30), CGPoint(x: 0.87, y: 0.70), CGPoint(x: 0.52, y: 0.93),
    ]

    /// Кривая через середины сторон: гладкая, как след после сглаживания.
    static func path(in rect: CGRect) -> Path {
        let corners = points.map { CGPoint(x: rect.minX + $0.x * rect.width, y: rect.minY + $0.y * rect.height) }
        var path = Path()
        path.move(to: midpoint(corners[corners.count - 1], corners[0]))
        for index in corners.indices {
            path.addQuadCurve(
                to: midpoint(corners[index], corners[(index + 1) % corners.count]), control: corners[index])
        }
        path.closeSubpath()
        return path
    }

    /// Точка замыкания — начало и конец контура.
    static func closingPoint(in size: CGSize) -> CGPoint {
        let point = midpoint(points[points.count - 1], points[0])
        return CGPoint(x: point.x * size.width, y: point.y * size.height)
    }

    /// Радиус круга, который из `point` накрывает всю рамку.
    static func reach(from point: CGPoint, in size: CGSize) -> CGFloat {
        let corners = [
            CGPoint.zero, CGPoint(x: size.width, y: 0), CGPoint(x: 0, y: size.height),
            CGPoint(x: size.width, y: size.height),
        ]
        return corners.map { hypot($0.x - point.x, $0.y - point.y) }.max() ?? 0
    }

    private static func midpoint(_ first: CGPoint, _ second: CGPoint) -> CGPoint {
        CGPoint(x: (first.x + second.x) / 2, y: (first.y + second.y) / 2)
    }
}

// MARK: - Находка тайника

/// Церемония находки: значок падает с перелётом и лёгким поворотом, сияние цвета металла, металлический блик, текст
/// поднимается — 1,6 с; в момент касания — тяжёлый удар хаптики. Открывается `.fullScreenCover` без анимации перехода.
struct FindCeremonyDemo: View {
    let metal: BadgeMetal
    let systemImage: String
    /// Капс-метка: «Эпический · золото».
    let caption: String
    @Environment(\.dismiss) private var dismiss
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var play = 0
    @State private var touches = 0
    @State private var shown = false

    var body: some View {
        let metal = self.metal
        let systemImage = self.systemImage
        let caption = self.caption
        VStack(spacing: 0) {
            Spacer()
            if reduceMotion {
                FindStage(metal: metal, systemImage: systemImage, caption: caption, values: .final)
                    .opacity(shown ? 1 : 0)
                    .animation(Motion.reducedAppear, value: shown)
            } else {
                KeyframeAnimator(initialValue: FindValues.start, trigger: play) { values in
                    FindStage(metal: metal, systemImage: systemImage, caption: caption, values: values)
                } keyframes: { _ in
                    let spec = MotionSpec.BadgeDrop.self
                    KeyframeTrack(\.drop) {
                        MoveKeyframe(0)
                        SpringKeyframe(1, duration: spec.spring.duration, spring: Motion.badgeDrop)
                    }
                    KeyframeTrack(\.badgeOpacity) {
                        MoveKeyframe(0)
                        LinearKeyframe(1, duration: 0.16)
                    }
                    KeyframeTrack(\.glowOpacity) {
                        MoveKeyframe(0)
                        LinearKeyframe(0, duration: spec.touchAt)
                        LinearKeyframe(1, duration: 0.5, timingCurve: .easeOut)
                        LinearKeyframe(0.75, duration: spec.glowEnd - spec.touchAt - 0.5, timingCurve: .easeOut)
                    }
                    KeyframeTrack(\.glowScale) {
                        MoveKeyframe(0.3)
                        LinearKeyframe(0.3, duration: spec.touchAt)
                        LinearKeyframe(1.18, duration: 0.5, timingCurve: .easeOut)
                        LinearKeyframe(1, duration: spec.glowEnd - spec.touchAt - 0.5, timingCurve: .easeOut)
                    }
                    KeyframeTrack(\.glint) {
                        MoveKeyframe(0)
                        LinearKeyframe(0, duration: spec.glintStart)
                        LinearKeyframe(1, duration: spec.glintEnd - spec.glintStart, timingCurve: .easeInOut)
                    }
                    KeyframeTrack(\.text) {
                        MoveKeyframe(0)
                        LinearKeyframe(0, duration: spec.textStart)
                        LinearKeyframe(1, duration: spec.textEnd - spec.textStart, timingCurve: .easeOut)
                    }
                }
            }
            Spacer()
            Button {
                dismiss()
            } label: {
                Text("Готово")
                    .font(.headline)
                    .foregroundStyle(Palette.uiButtonInk.color)
                    .frame(maxWidth: .infinity, minHeight: 36)
            }
            .buttonStyle(.borderedProminent)
            .buttonBorderShape(.capsule)
            .controlSize(.large)
            .tint(Palette.uiButton.color)
            .padding(.horizontal, 28)
            .padding(.bottom, 24)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Palette.uiBackground.color)
        .foregroundStyle(Palette.uiInk.color)
        .sensoryFeedback(.impact(weight: .heavy), trigger: touches)
        .task {
            shown = true
            play += 1
            if !reduceMotion {
                try? await Task.sleep(for: .seconds(MotionSpec.BadgeDrop.touchAt))
            }
            touches += 1
        }
    }
}

private struct FindValues: Sendable {
    /// Падение: 0 — наверху с поворотом, 1 — на месте (пружина — с перелётом за 1).
    var drop = 1.0
    var badgeOpacity = 1.0
    var glowOpacity = 0.75
    var glowScale = 1.0
    /// Блик: 0 — слева за краем, 1 — справа за краем.
    var glint = 1.0
    var text = 1.0

    static let final = FindValues()
    static let start = FindValues(drop: 0, badgeOpacity: 0, glowOpacity: 0, glowScale: 0.3, glint: 0, text: 0)
}

private struct FindStage: View {
    let metal: BadgeMetal
    let systemImage: String
    let caption: String
    let values: FindValues

    var body: some View {
        let spec = MotionSpec.BadgeDrop.self
        let glint = -0.3 + 1.6 * values.glint
        VStack(spacing: 18) {
            ZStack {
                Circle()
                    .fill(
                        RadialGradient(
                            colors: [metal.glow.color.opacity(0.7), metal.glow.color.opacity(0.22), .clear],
                            center: .center, startRadius: 0, endRadius: 135)
                    )
                    .frame(width: 270, height: 270)
                    .scaleEffect(values.glowScale)
                    .opacity(values.glowOpacity)
                RarityBadge(metal, systemImage: systemImage, size: 160)
                    .overlay {
                        // Металлический блик: светлая полоса проходит по форме значка.
                        BadgeShape(metal.shape, radius: metal.shape.rimRadius)
                            .fill(
                                LinearGradient(
                                    colors: [.white.opacity(0), .white.opacity(0.85), .white.opacity(0)],
                                    startPoint: UnitPoint(x: glint - 0.17, y: 0.6),
                                    endPoint: UnitPoint(x: glint + 0.17, y: 0.4)))
                    }
                    .scaleEffect(1 + 0.08 * (1 - values.drop))
                    .rotationEffect(.degrees(spec.startRotationDegrees * (1 - values.drop)))
                    .offset(y: spec.startOffsetY * (1 - values.drop))
                    .opacity(values.badgeOpacity)
            }
            .frame(height: 280)
            VStack(spacing: 6) {
                Text(verbatim: caption)
                    .capsLabel()
                Text("Ты нашёл «Фонарщика»")
                    .font(.title2.bold())
                Text("ул. Советская · ты 7-й нашедший")
                    .font(.subheadline)
                    .foregroundStyle(Palette.uiInk2.color)
            }
            .multilineTextAlignment(.center)
            .opacity(values.text)
            .offset(y: 12 * (1 - values.text))
        }
        .padding(.horizontal, 28)
    }
}
