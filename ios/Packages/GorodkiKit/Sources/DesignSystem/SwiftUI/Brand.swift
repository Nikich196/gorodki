import SwiftUI

// Бренд «Крепость» (Никита 15.10: «дизайн слишком простой — нужен премиальный, индивидуальный, с анимациями и
// эффектами»). Знак — бастионная звезда Брестской крепости, нарисованная одной замкнутой линией бега: и Брест, и главная
// механика игры («замкнул петлю — земля твоя»). Фон — не плоский: днём «рассвет над Бугом», ночью «огни крепости» —
// мягкие свечения и медленно плывущие горизонтали. Главная кнопка — «огонь» (кирпич крепости → янтарь) со свечением.

/// Цвета бренда.
public enum Brand {
    /// «Кирпич» — красный крепостной кирпич, начало градиента «огня».
    public static let ember = Themed<RGBA>(day: 0xF0442C, night: 0xFF5A3A)
    /// «Янтарь» — конец градиента «огня».
    public static let amber = Themed<RGBA>(day: 0xFF9A2E, night: 0xFFB13D)
    /// «Сумерки» — второй цвет бренда: свечения, кольца, вторичные акценты.
    public static let dusk = Themed<RGBA>(day: 0x5B4BFF, night: 0x8A7BFF)
    /// «Бирюза» — третий: свечение у воды (Буг, Мухавец).
    public static let river = Themed<RGBA>(day: 0x14B8C8, night: 0x2EE6D6)
    /// Основа фона.
    public static let base = Themed<RGBA>(day: 0xF7F3F0, night: 0x070914)
    /// Горизонтали на фоне.
    public static let contour = Themed(day: RGBA(0x2A2140, alpha: 0.07), night: RGBA(0xC9C2FF, alpha: 0.07))
    /// Тонкая светлая кромка стеклянных карточек (вверху сильнее).
    public static let rim = Themed(day: RGBA(0xFFFFFF, alpha: 0.95), night: RGBA(0xFFFFFF, alpha: 0.28))

    /// Градиент «огня» для главной кнопки и героев.
    public static func fire(_ theme: Theme) -> LinearGradient {
        LinearGradient(
            colors: [ember[theme].color, amber[theme].color], startPoint: .topLeading, endPoint: .bottomTrailing)
    }

    /// Градиент знака: огонь, уходящий в сумерки.
    public static func mark(_ theme: Theme) -> LinearGradient {
        LinearGradient(
            colors: [amber[theme].color, ember[theme].color, dusk[theme].color], startPoint: .top, endPoint: .bottom)
    }
}

// MARK: - Фон

/// Живой фон экранов вне карты: основа, три мягких свечения (огонь, сумерки, река), которые медленно дышат, и
/// горизонтали — кольца вокруг двух «холмов», как на топографической карте. «Уменьшить движение» — неподвижный кадр.
public struct AtmosphereBackground: View {
    /// Свечение цвета игрока поверх (профиль, итог забега); `nil` — только цвета бренда.
    public var tint: Color?
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    public init(tint: Color? = nil) {
        self.tint = tint
    }

    public var body: some View {
        let theme = Theme(colorScheme)
        TimelineView(.animation(minimumInterval: 1.0 / 24, paused: reduceMotion)) { timeline in
            let t = reduceMotion ? 0 : timeline.date.timeIntervalSinceReferenceDate
            Canvas(rendersAsynchronously: true) { context, size in
                context.fill(Path(CGRect(origin: .zero, size: size)), with: .color(Brand.base[theme].color))
                let glows: [(Color, CGPoint, CGFloat, Double)] =
                    [
                        (Brand.amber[theme].color, CGPoint(x: 0.1, y: 0.02), 0.95, theme == .day ? 0.42 : 0.22),
                        (Brand.dusk[theme].color, CGPoint(x: 0.95, y: 0.35), 0.85, theme == .day ? 0.20 : 0.34),
                        (Brand.river[theme].color, CGPoint(x: 0.15, y: 0.95), 0.8, theme == .day ? 0.16 : 0.20),
                    ] + (tint.map { [($0, CGPoint(x: 0.7, y: 0.12), 0.7, theme == .day ? 0.26 : 0.30)] } ?? [])
                for (index, glow) in glows.enumerated() {
                    let phase = t * 0.12 + Double(index) * 1.9
                    let center = CGPoint(
                        x: (glow.1.x + 0.05 * sin(phase)) * size.width,
                        y: (glow.1.y + 0.04 * cos(phase * 0.8)) * size.height)
                    let radius = glow.2 * max(size.width, size.height) * (0.62 + 0.04 * sin(phase * 1.3))
                    context.fill(
                        Path(
                            ellipseIn: CGRect(
                                x: center.x - radius, y: center.y - radius, width: radius * 2, height: radius * 2)),
                        with: .radialGradient(
                            Gradient(colors: [glow.0.opacity(glow.3), glow.0.opacity(0)]),
                            center: center, startRadius: 0, endRadius: radius))
                }
                let ink = Brand.contour[theme].color
                for hill in [(CGPoint(x: 0.82, y: 0.18), 1.0), (CGPoint(x: 0.12, y: 0.72), -1.0)] {
                    let center = CGPoint(x: hill.0.x * size.width, y: hill.0.y * size.height)
                    for ring in 1...9 {
                        let base = CGFloat(ring) * size.width * 0.075
                        var path = Path()
                        for step in 0...72 {
                            let angle = Double(step) / 72 * 2 * .pi
                            let wobble =
                                sin(angle * 3 + Double(ring) * 0.7 + t * 0.05 * hill.1) * 0.10
                                + sin(angle * 5 - Double(ring) * 0.4) * 0.05
                            let radius = base * (1 + wobble)
                            let point = CGPoint(
                                x: center.x + cos(angle) * radius, y: center.y + sin(angle) * radius * 0.82)
                            if step == 0 {
                                path.move(to: point)
                            } else {
                                path.addLine(to: point)
                            }
                        }
                        path.closeSubpath()
                        context.stroke(path, with: .color(ink), lineWidth: ring % 3 == 0 ? 1.4 : 0.8)
                    }
                }
            }
        }
        .ignoresSafeArea()
        .accessibilityHidden(true)
    }
}

/// Живой градиент — переливается (Никита 15.10: «обои переливающиеся, не статичные»): сетка 3×3 мягких цветов
/// бренда, внутренние точки медленно плавают. Регистрация и документы. «Уменьшить движение» — неподвижный.
public struct LiveGradientBackground: View {
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    public init() {}

    public var body: some View {
        let night = colorScheme == .dark
        TimelineView(.animation(minimumInterval: 1.0 / 30, paused: reduceMotion)) { timeline in
            let t = reduceMotion ? 0 : timeline.date.timeIntervalSinceReferenceDate * 0.32
            MeshGradient(
                width: 3, height: 3,
                points: [
                    [0, 0], [Float(0.5 + 0.12 * sin(t * 0.7)), 0], [1, 0],
                    [0, Float(0.5 + 0.12 * sin(t))],
                    [Float(0.5 + 0.16 * sin(t * 0.9)), Float(0.5 + 0.14 * cos(t * 1.1))],
                    [1, Float(0.5 + 0.12 * cos(t * 0.8))],
                    [0, 1], [Float(0.5 + 0.12 * cos(t * 0.6)), 1], [1, 1],
                ],
                colors: night ? Self.nightColors : Self.dayColors)
        }
        .ignoresSafeArea()
        .accessibilityHidden(true)
    }

    private static let dayColors: [Color] = [
        Color(hex: 0xFFE2CC), Color(hex: 0xFFF3EA), Color(hex: 0xE4DEFF),
        Color(hex: 0xFFD6CF), Color(hex: 0xFFF8F2), Color(hex: 0xD9EEFF),
        Color(hex: 0xE9E3FF), Color(hex: 0xDDF6F1), Color(hex: 0xFFE9D6),
    ]

    private static let nightColors: [Color] = [
        Color(hex: 0x1A1240), Color(hex: 0x0B0E24), Color(hex: 0x2B1450),
        Color(hex: 0x07202A), Color(hex: 0x080A18), Color(hex: 0x3A1530),
        Color(hex: 0x0B0E24), Color(hex: 0x1C1050), Color(hex: 0x04242A),
    ]
}

extension View {
    /// Живой фон бренда за экраном (`AtmosphereBackground`) вместо плоского `Palette.uiBackground`.
    public func atmosphereBackground(tint: Color? = nil) -> some View {
        background { AtmosphereBackground(tint: tint) }
    }

    /// Переливающийся градиент за экраном (`LiveGradientBackground`) — регистрация и документы.
    public func liveGradientBackground() -> some View {
        background { LiveGradientBackground() }
    }

    /// Стеклянная карточка бренда: Liquid Glass и тонкая светлая кромка, ярче сверху — как блик на стекле.
    public func brandCard(cornerRadius: CGFloat = Radius.card, padding: CGFloat? = nil) -> some View {
        modifier(BrandCard(cornerRadius: cornerRadius, padding: padding))
    }
}

private struct BrandCard: ViewModifier {
    let cornerRadius: CGFloat
    let padding: CGFloat?
    @Environment(\.colorScheme) private var colorScheme

    func body(content: Content) -> some View {
        let theme = Theme(colorScheme)
        content
            .padding(padding ?? 0)
            .glassEffect(.regular, in: .rect(cornerRadius: cornerRadius))
            .overlay {
                RoundedRectangle(cornerRadius: cornerRadius)
                    .strokeBorder(
                        LinearGradient(
                            colors: [Brand.rim[theme].color, Brand.rim[theme].color.opacity(0.08)],
                            startPoint: .top, endPoint: .bottom),
                        lineWidth: 1
                    )
                    .allowsHitTesting(false)
            }
    }
}

// MARK: - Знак

/// Бастионная звезда — план крепости: пять бастионов со срезанными остриями, глубокими фланками и куртинами между
/// ними; внутри знака — кольцо цитадели. Одна замкнутая линия, начало и конец — внизу, у ворот (там точка бегущего).
public struct FortressStar: Shape {
    public init() {}

    nonisolated public func path(in rect: CGRect) -> Path {
        let center = CGPoint(x: rect.midX, y: rect.midY + rect.height * 0.03)
        let outer = min(rect.width, rect.height) / 2
        var points: [CGPoint] = []
        func at(_ radius: CGFloat, _ degrees: Double) -> CGPoint {
            let angle = (degrees - 90) * .pi / 180
            return CGPoint(x: center.x + cos(angle) * radius, y: center.y + sin(angle) * radius)
        }
        // От ворот внизу (180°) по часовой: куртина, фланк, срезанное остриё, фланк — у каждого из пяти бастионов.
        for bastion in 0..<5 {
            let axis = 216.0 + Double(bastion) * 72
            points.append(at(outer * 0.6, axis - 36))
            points.append(at(outer * 0.64, axis - 20))
            points.append(at(outer, axis - 9))
            points.append(at(outer, axis + 9))
            points.append(at(outer * 0.64, axis + 20))
        }
        var path = Path()
        path.move(to: at(outer * 0.6, 180))
        points.forEach { path.addLine(to: $0) }
        path.closeSubpath()
        return path
    }
}

/// Знак «Городков»: звезда-крепость. `animated` — линия бега прорисовывает её (1,4 с), потом она заливается огнём и
/// мягко светится; без анимации («Уменьшить движение», мелкие места) — сразу готовый знак.
public struct BrandMark: View {
    public var size: CGFloat
    public var animated: Bool
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var drawn: CGFloat = 0
    @State private var filled = false
    @State private var breathe = false
    @Environment(\.brandSound) private var sound

    public init(size: CGFloat = 56, animated: Bool = false) {
        self.size = size
        self.animated = animated
    }

    public var body: some View {
        let theme = Theme(colorScheme)
        let still = !animated || reduceMotion
        let progress = still ? 1 : drawn
        let isFilled = still || filled
        ZStack {
            FortressStar()
                .fill(Brand.mark(theme))
                .opacity(isFilled ? 1 : 0)
                .shadow(
                    color: Brand.ember[theme].color.opacity(breathe ? 0.55 : 0.25),
                    radius: breathe ? size * 0.22 : size * 0.12)
            Circle()
                .stroke(Color.white.opacity(isFilled ? 0.85 : 0), lineWidth: max(1.2, size * 0.035))
                .frame(width: size * 0.36, height: size * 0.36)
                .offset(y: size * 0.03)
            FortressStar()
                .trim(from: 0, to: progress)
                .stroke(
                    isFilled ? Color.white.opacity(0.9) : Brand.ember[theme].color,
                    style: StrokeStyle(lineWidth: max(1.5, size * 0.045), lineCap: .round, lineJoin: .round))
            Circle()
                .fill(.white)
                .frame(width: size * 0.12, height: size * 0.12)
                .overlay { Circle().stroke(Brand.ember[theme].color, lineWidth: max(1, size * 0.03)) }
                .offset(y: size * 0.03 + size / 2 * 0.6)
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
        .onAppear {
            guard !still else { return }
            drawn = 0
            filled = false
            withAnimation(.easeInOut(duration: 1.4)) { drawn = 1 }
            withAnimation(.spring(duration: 0.6).delay(1.3)) { filled = true }
            Task {
                try? await Task.sleep(for: .seconds(1.3))
                sound.play(.chime)
            }
            withAnimation(.easeInOut(duration: 2.4).repeatForever(autoreverses: true).delay(1.8)) { breathe = true }
        }
    }
}

/// Надпись «ГОРОДКИ»: широкий чёрный шрифт с наклоном — как у спортивных брендов.
public struct BrandWordmark: View {
    public var size: CGFloat

    public init(size: CGFloat = 40) {
        self.size = size
    }

    public var body: some View {
        Text(verbatim: "ГОРОДКИ")
            .font(.system(size: size, weight: .black).width(.expanded))
            .italic()
            .tracking(-0.5)
            .lineLimit(1)
            .minimumScaleFactor(0.6)
    }
}

// MARK: - Иллюстрация

/// Силуэт Холмских ворот Брестской крепости: стена с арками казематов, три башенки ворот с зубцами и арка проезда.
/// Декор под карточками и в шапках; рисуется полупрозрачным.
public struct FortressSkyline: Shape {
    public init() {}

    nonisolated public func path(in rect: CGRect) -> Path {
        let w = rect.width
        let h = rect.height
        let ground = rect.maxY
        var path = Path()
        // Стена.
        let wallTop = rect.minY + h * 0.55
        path.addRect(CGRect(x: rect.minX, y: wallTop, width: w, height: ground - wallTop))
        // Ворота: три башенки с зубцами.
        let gateLeft = rect.minX + w * 0.38
        let gateWidth = w * 0.24
        let towerTop = rect.minY + h * 0.12
        path.addRect(
            CGRect(x: gateLeft, y: rect.minY + h * 0.28, width: gateWidth, height: ground - rect.minY - h * 0.28))
        for tower in 0..<3 {
            let towerWidth = gateWidth * 0.2
            let x = gateLeft + CGFloat(tower) * (gateWidth - towerWidth) / 2
            path.addRect(CGRect(x: x, y: towerTop, width: towerWidth, height: ground - towerTop))
            // Зубцы башенки.
            for tooth in 0..<3 {
                let toothWidth = towerWidth / 5
                path.addRect(
                    CGRect(
                        x: x + CGFloat(tooth) * toothWidth * 2, y: towerTop - h * 0.06, width: toothWidth,
                        height: h * 0.06))
            }
        }
        // Зубцы стены.
        var x = rect.minX
        while x < rect.maxX {
            if x + w * 0.02 < gateLeft || x > gateLeft + gateWidth {
                path.addRect(CGRect(x: x, y: wallTop - h * 0.06, width: w * 0.02, height: h * 0.06))
            }
            x += w * 0.04
        }
        return path
    }
}

/// Арки — проезд ворот и окна казематов; вычитаются из силуэта (`FortressSkylineView`).
public struct FortressArches: Shape {
    public init() {}

    nonisolated public func path(in rect: CGRect) -> Path {
        let w = rect.width
        let h = rect.height
        var path = Path()
        func arch(_ x: CGFloat, _ width: CGFloat, _ top: CGFloat) {
            let radius = width / 2
            path.move(to: CGPoint(x: x, y: rect.maxY))
            path.addLine(to: CGPoint(x: x, y: top + radius))
            path.addArc(
                center: CGPoint(x: x + radius, y: top + radius), radius: radius, startAngle: .degrees(180),
                endAngle: .degrees(0), clockwise: false)
            path.addLine(to: CGPoint(x: x + width, y: rect.maxY))
            path.closeSubpath()
        }
        arch(rect.minX + w * 0.465, w * 0.07, rect.minY + h * 0.55)
        var x = rect.minX + w * 0.03
        while x < rect.maxX - w * 0.03 {
            if x + w * 0.03 < rect.minX + w * 0.38 || x > rect.minX + w * 0.62 {
                arch(x, w * 0.025, rect.minY + h * 0.7)
            }
            x += w * 0.055
        }
        return path
    }
}

/// Силуэт ворот одним цветом: стена минус арки.
public struct FortressSkylineView: View {
    public var color: Color

    public init(color: Color) {
        self.color = color
    }

    public var body: some View {
        FortressSkyline()
            .fill(color)
            .mask {
                Rectangle()
                    .overlay {
                        FortressArches().fill(.black).blendMode(.destinationOut)
                    }
                    .compositingGroup()
            }
            .accessibilityHidden(true)
    }
}

// MARK: - Главная кнопка

/// Главная кнопка бренда: капсула «огня» со свечением и бликом, который пробегает по ней; нажатие — пружина.
public struct BrandButtonStyle: ButtonStyle {
    public init() {}

    public func makeBody(configuration: Configuration) -> some View {
        BrandButtonBody(configuration: configuration)
    }
}

extension ButtonStyle where Self == BrandButtonStyle {
    /// `.buttonStyle(.brand)` — главная кнопка бренда.
    public static var brand: BrandButtonStyle { BrandButtonStyle() }
}

private struct BrandButtonBody: View {
    let configuration: ButtonStyleConfiguration
    @Environment(\.isEnabled) private var isEnabled
    @Environment(\.brandSound) private var sound
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let theme = Theme(colorScheme)
        configuration.label
            // Без .textCase(.uppercase): он меняет и подпись для VoiceOver («ДАЛЬШЕ»), а по ней кнопку находят и тесты.
            .font(.system(size: 17, weight: .heavy).width(.expanded))
            .foregroundStyle(.white)
            .frame(maxWidth: .infinity, minHeight: Metrics.controlHeight)
            .background {
                Capsule()
                    .fill(Brand.fire(theme))
                    .overlay {
                        if !reduceMotion && isEnabled {
                            ShineSweep().clipShape(.capsule)
                        }
                    }
                    .overlay {
                        Capsule().strokeBorder(.white.opacity(0.35), lineWidth: 1)
                    }
                    .shadow(color: Brand.ember[theme].color.opacity(isEnabled ? 0.45 : 0), radius: 16, y: 8)
            }
            .scaleEffect(configuration.isPressed ? 0.96 : 1)
            .animation(.spring(duration: 0.3, bounce: 0.4), value: configuration.isPressed)
            .saturation(isEnabled ? 1 : 0)
            .opacity(isEnabled ? 1 : 0.45)
            .contentShape(.capsule)
            .sensoryFeedback(.impact(weight: .light), trigger: configuration.isPressed) { _, pressed in pressed }
            .onChange(of: configuration.isPressed) { _, pressed in
                if pressed && isEnabled { sound.play(.tap) }
            }
    }
}

/// Блик, который раз в несколько секунд пробегает по кнопке.
private struct ShineSweep: View {
    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 30)) { timeline in
            let cycle = timeline.date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: 3.6)
            let progress = min(max(cycle / 1.1, 0), 1)
            GeometryReader { proxy in
                LinearGradient(
                    colors: [.white.opacity(0), .white.opacity(0.45), .white.opacity(0)],
                    startPoint: .leading, endPoint: .trailing
                )
                .frame(width: proxy.size.width * 0.35)
                .rotationEffect(.degrees(18))
                .offset(x: -proxy.size.width * 0.4 + progress * proxy.size.width * 1.5)
                .opacity(progress < 1 ? 1 : 0)
            }
        }
        .allowsHitTesting(false)
    }
}

// MARK: - Кольцо и конфетти

/// Кольцо прогресса: дорожка и дуга градиента огня, при появлении набегает от нуля.
public struct BrandRing: View {
    public var progress: Double
    public var lineWidth: CGFloat
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var shown: Double = 0

    public init(progress: Double, lineWidth: CGFloat = 10) {
        self.progress = progress
        self.lineWidth = lineWidth
    }

    public var body: some View {
        let theme = Theme(colorScheme)
        ZStack {
            Circle().stroke(Palette.uiFill[theme].color, lineWidth: lineWidth)
            Circle()
                .trim(from: 0, to: shown)
                .stroke(
                    AngularGradient(
                        colors: [
                            Brand.amber[theme].color, Brand.ember[theme].color, Brand.dusk[theme].color,
                            Brand.amber[theme].color,
                        ],
                        center: .center),
                    style: StrokeStyle(lineWidth: lineWidth, lineCap: .round)
                )
                .rotationEffect(.degrees(-90))
                .shadow(color: Brand.ember[theme].color.opacity(0.35), radius: 6)
        }
        .onAppear {
            if reduceMotion {
                shown = progress
            } else {
                withAnimation(.spring(duration: 1.4, bounce: 0.15).delay(0.2)) { shown = progress }
            }
        }
        .onChange(of: progress) { _, value in
            withAnimation(.spring(duration: 0.8)) { shown = value }
        }
        .accessibilityHidden(true)
    }
}

/// Вспышка частиц, когда меняется `trigger`: конфетти (церемония захвата) или искры (петля на интро замкнулась) —
/// цвета игрока, золото и белое. `origin` — откуда летят (точка в своём кадре); по умолчанию — чуть выше середины.
public struct ConfettiBurst<Trigger: Equatable>: View {
    public enum Style: Sendable {
        /// Бумажки: крупные, кружатся, падают.
        case confetti
        /// Искры: мелкие, быстрые, со свечением, гаснут быстрее.
        case sparks
    }

    public var trigger: Trigger
    public var colors: [Color]
    public var origin: CGPoint?
    public var style: Style
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var startedAt: Date?

    public init(trigger: Trigger, colors: [Color], origin: CGPoint? = nil, style: Style = .confetti) {
        self.trigger = trigger
        self.colors = colors
        self.origin = origin
        self.style = style
    }

    public var body: some View {
        let sparks = style == .sparks
        let lifetime = sparks ? 1.1 : 2.2
        TimelineView(.animation(paused: startedAt == nil || reduceMotion)) { timeline in
            Canvas { context, size in
                guard let startedAt, !colors.isEmpty else { return }
                let t = timeline.date.timeIntervalSince(startedAt)
                guard t < lifetime else { return }
                let from = origin ?? CGPoint(x: size.width / 2, y: size.height * 0.42)
                if sparks {
                    context.addFilter(.shadow(color: .white.opacity(0.9), radius: 3))
                }
                for index in 0..<(sparks ? 48 : 64) {
                    let seed = Double(index)
                    let angle = (seed * 137.508).truncatingRemainder(dividingBy: 360) * .pi / 180
                    let speed = (sparks ? 180 : 260) + (seed * 53).truncatingRemainder(dividingBy: sparks ? 160 : 220)
                    let x = from.x + cos(angle) * speed * t
                    let y = from.y + sin(angle) * speed * t * 0.8 + (sparks ? 120 : 420) * t * t
                    let fade = max(0, 1 - t / lifetime)
                    var piece = context
                    piece.translateBy(x: x, y: y)
                    piece.opacity = fade
                    if sparks {
                        // Искра — короткий штрих вдоль полёта.
                        piece.rotate(by: .radians(angle))
                        piece.fill(
                            Path(
                                roundedRect: CGRect(x: -6 * fade - 1, y: -1, width: 8 * fade + 2, height: 2),
                                cornerRadius: 1),
                            with: .color(colors[index % colors.count]))
                    } else {
                        piece.rotate(by: .degrees(seed * 47 + t * (200 + seed * 3)))
                        let rect =
                            index % 3 == 0
                            ? CGRect(x: -3, y: -3, width: 6, height: 6) : CGRect(x: -2, y: -5, width: 4, height: 10)
                        piece.fill(
                            index % 3 == 0 ? Path(ellipseIn: rect) : Path(roundedRect: rect, cornerRadius: 1),
                            with: .color(colors[index % colors.count]))
                    }
                }
            }
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
        .onChange(of: trigger) { _, _ in
            guard !reduceMotion else { return }
            startedAt = .now
        }
    }
}

// MARK: - Пульс

/// Пульс за капсулой «Старт»: два кольца цвета игрока расходятся и гаснут — как сердцебиение перед забегом.
public struct PulseRings: View {
    public var color: Color
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    public init(color: Color) {
        self.color = color
    }

    public var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 30, paused: reduceMotion)) { timeline in
            let t = reduceMotion ? 0 : timeline.date.timeIntervalSinceReferenceDate
            ZStack {
                ForEach(0..<2, id: \.self) { ring in
                    let phase = (t / 1.8 + Double(ring) * 0.5).truncatingRemainder(dividingBy: 1)
                    Capsule()
                        .stroke(color, lineWidth: 3)
                        .scaleEffect(x: 1 + phase * 0.22, y: 1 + phase * 0.6)
                        .opacity(reduceMotion ? 0 : (1 - phase) * 0.7)
                }
            }
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}

// MARK: - Звуки

/// Звуки бренда (синтез, `ios/scripts/make-sounds.py`). Дизайн-система только сообщает, когда звучать; играет приложение
/// (`SoundBoard`) — оно знает про беззвучный режим и переключатель «Звуки» в «Настройках».
public enum BrandSound: String, CaseIterable, Sendable {
    /// Старт забега: два коротких сигнала и длинный — как на стадионе.
    case start
    /// Захват подтверждён: восходящий колокольный аккорд.
    case capture
    /// Пьедестал рейтинга: короткие фанфары.
    case rank
    /// Знак прорисовался: мягкий колокольчик.
    case chime
    /// Листание интро.
    case swoosh
    /// Нажатие главной кнопки.
    case tap
    /// Отметка.
    case toggle
}

/// Кто играет звуки бренда; по умолчанию — никто (превью, тесты, расширения).
public struct BrandSoundPlayer: Sendable {
    public var play: @MainActor @Sendable (BrandSound) -> Void

    public init(play: @escaping @MainActor @Sendable (BrandSound) -> Void) {
        self.play = play
    }

    public static let silent = BrandSoundPlayer { _ in }
}

extension EnvironmentValues {
    /// Проигрыватель звуков бренда — его ставит корень приложения.
    @Entry public var brandSound: BrandSoundPlayer = .silent
}
