import SwiftUI

/// Форма значка редкости: круг, шестигранник, огранка или звезда (`BadgeShapeKind`) радиусом `radius` в долях
/// половины рамки.
public struct BadgeShape: Shape {
    public let kind: BadgeShapeKind
    public let radius: Double

    public init(_ kind: BadgeShapeKind, radius: Double) {
        self.kind = kind
        self.radius = radius
    }

    public nonisolated func path(in rect: CGRect) -> Path {
        let center = CGPoint(x: rect.midX, y: rect.midY)
        let scale = min(rect.width, rect.height) / 2 * radius
        guard kind != .circle else {
            return Path(
                ellipseIn: CGRect(x: center.x - scale, y: center.y - scale, width: 2 * scale, height: 2 * scale))
        }
        var path = Path()
        path.addLines(kind.unitVertices.map { CGPoint(x: center.x + scale * $0.x, y: center.y + scale * $0.y) })
        path.closeSubpath()
        return path
    }
}

/// Значок тайника (PLAN.md, §6.5): обод из металла, лицо, глиф SF Symbols. Ненайденный — пунктирный силуэт.
/// Рисуется градиентами из `BadgeMetal`, без картинок: так он чёткий в любом размере.
public struct RarityBadge: View {
    private let metal: BadgeMetal
    private let systemImage: String
    private let found: Bool
    private let size: CGFloat

    public init(_ metal: BadgeMetal, systemImage: String, found: Bool = true, size: CGFloat = 64) {
        self.metal = metal
        self.systemImage = systemImage
        self.found = found
        self.size = size
    }

    public var body: some View {
        Group {
            if found {
                medal
            } else {
                BadgeShape(metal.shape, radius: metal.shape.rimRadius)
                    .stroke(Palette.uiInk3.color, style: StrokeStyle(lineWidth: max(1, size / 40), dash: [4, 3]))
            }
        }
        .frame(width: size, height: size)
    }

    private var medal: some View {
        let rim = BadgeShape(metal.shape, radius: metal.shape.rimRadius)
        let face = BadgeShape(metal.shape, radius: metal.shape.faceRadius)
        let unit = size / 48  // линии — как в макете, где значок нарисован в поле 48
        return ZStack {
            rim.fill(LinearGradient(gradient: metal.rim.gradient, startPoint: .topLeading, endPoint: .bottomTrailing))
            // Металлический отблеск слева сверху.
            rim.fill(
                RadialGradient(
                    colors: [.white.opacity(0.55), .white.opacity(0)], center: UnitPoint(x: 0.32, y: 0.22),
                    startRadius: 0, endRadius: size * 0.7 * 0.55))
            face.fill(faceStyle)
            face.stroke(.white.opacity(0.6), lineWidth: 0.8 * unit)
            Image(systemName: systemImage)
                .font(.system(size: size * 0.36, weight: .semibold))
                .foregroundStyle(metal.glyph.color.opacity(0.88))
            rim.stroke(.black.opacity(0.2), lineWidth: 0.7 * unit)
        }
        .shadow(color: metal == .holo ? metal.glow.color : .clear, radius: 6 * unit)
        .accessibilityHidden(true)
    }

    private var faceStyle: AnyShapeStyle {
        if metal == .holo {
            AnyShapeStyle(
                LinearGradient(gradient: metal.face.gradient, startPoint: .bottomLeading, endPoint: .topTrailing))
        } else {
            AnyShapeStyle(
                RadialGradient(
                    gradient: metal.face.gradient, center: UnitPoint(x: 0.38, y: 0.32), startRadius: 0,
                    endRadius: size * metal.shape.faceRadius * 0.75))
        }
    }
}

/// Полоска прогресса редкости: металл на дорожке `Palette.uiFill`.
public struct MetalProgress: View {
    private let metal: BadgeMetal
    private let fraction: Double

    public init(_ metal: BadgeMetal, fraction: Double) {
        self.metal = metal
        self.fraction = min(max(fraction, 0), 1)
    }

    public var body: some View {
        Capsule()
            .fill(Palette.uiFill.color)
            .overlay(alignment: .leading) {
                GeometryReader { proxy in
                    Capsule()
                        .fill(
                            LinearGradient(gradient: metal.progress.gradient, startPoint: .leading, endPoint: .trailing)
                        )
                        .frame(width: proxy.size.width * fraction)
                }
            }
            .frame(height: 6)
    }
}
