import DesignSystem
import SwiftUI

/// Три карточки интро (PLAN.md, §3.18: «Захватывай город шагами», обучение) — картинка из токенов дизайна и две строки.
struct IntroCard: Identifiable {
    let id: Int
    let title: String
    let text: String

    static let all = [
        IntroCard(
            id: 0, title: "Обеги — и участок твой",
            text: "Замкни петлю на пробежке или прогулке — земля ляжет ровно по контуру следа."),
        IntroCard(
            id: 1, title: "Открой Брест из тумана",
            text: "Город скрыт туманом. Там, где ты прошёл, он рассеивается. Сколько города откроешь ты?"),
        IntroCard(
            id: 2, title: "Держи свою землю",
            text: "Соседи могут отбить участок. Возвращайся, поднимай уровень, зови друзей в клан."),
    ]
}

struct IntroCardView: View {
    let card: IntroCard

    var body: some View {
        VStack(spacing: 28) {
            IntroIllustration(kind: card.id)
                .frame(width: 260, height: 260)
                .clipShape(.rect(cornerRadius: Radius.card))
                .accessibilityHidden(true)
            VStack(spacing: 12) {
                Text(card.title)
                    .font(.largeTitle.bold())
                    .fontDesign(.rounded)
                    .foregroundStyle(Palette.uiInk.color)
                Text(card.text)
                    .font(.body)
                    .foregroundStyle(Palette.uiInk2.color)
            }
            .multilineTextAlignment(.center)
            .padding(.horizontal, 28)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// Картинки интро — теми же цветами, что карта: земля `mapLand`, участки — цвета игроков с кромкой, туман — `FogStyle`.
private struct IntroIllustration: View {
    let kind: Int
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        let theme = Theme(colorScheme)
        ZStack {
            Palette.mapLand.color
            StreetGrid()
                .stroke(Palette.uiSeparator.color, lineWidth: 6)
            switch kind {
            case 0:
                parcel(.sky, level: .two, theme: theme, in: CGRect(x: 0.2, y: 0.22, width: 0.6, height: 0.56))
                Circle()
                    .fill(PlayerColor.sky.edge[theme].color)
                    .frame(width: 16, height: 16)
                    .offset(x: 78, y: 70)
            case 1:
                FogWithClearing()
            default:
                parcel(.coral, level: .one, theme: theme, in: CGRect(x: 0.08, y: 0.12, width: 0.5, height: 0.44))
                parcel(.forest, level: .three, theme: theme, in: CGRect(x: 0.42, y: 0.34, width: 0.5, height: 0.5))
                parcel(.violet, level: .two, theme: theme, in: CGRect(x: 0.12, y: 0.58, width: 0.36, height: 0.32))
            }
        }
    }

    private func parcel(_ color: PlayerColor, level: TerritoryLevel, theme: Theme, in unit: CGRect) -> some View {
        GeometryReader { proxy in
            let rect = CGRect(
                x: unit.minX * proxy.size.width, y: unit.minY * proxy.size.height,
                width: unit.width * proxy.size.width, height: unit.height * proxy.size.height)
            ParcelShape(rect: rect)
                .fill(color.fill(level, theme: theme).color)
            ParcelShape(rect: rect)
                .stroke(color.edge[theme].color, style: StrokeStyle(lineWidth: 3, lineJoin: .round))
        }
    }
}

/// Кварталы: несколько улиц поперёк картинки.
private struct StreetGrid: Shape {
    nonisolated func path(in rect: CGRect) -> Path {
        var path = Path()
        for fraction in [0.3, 0.68] {
            path.move(to: CGPoint(x: rect.minX, y: rect.height * fraction))
            path.addLine(to: CGPoint(x: rect.maxX, y: rect.height * (fraction + 0.08)))
            path.move(to: CGPoint(x: rect.width * fraction, y: rect.minY))
            path.addLine(to: CGPoint(x: rect.width * (fraction - 0.06), y: rect.maxY))
        }
        return path
    }
}

/// Участок неровной формы — как контур следа, а не квадрат.
private struct ParcelShape: Shape {
    let rect: CGRect

    nonisolated func path(in _: CGRect) -> Path {
        let points = [
            CGPoint(x: 0.12, y: 0.08), CGPoint(x: 0.7, y: 0), CGPoint(x: 1, y: 0.38), CGPoint(x: 0.86, y: 0.94),
            CGPoint(x: 0.3, y: 1), CGPoint(x: 0, y: 0.56),
        ].map { CGPoint(x: rect.minX + $0.x * rect.width, y: rect.minY + $0.y * rect.height) }
        var path = Path()
        path.addLines(points)
        path.closeSubpath()
        return path
    }
}

/// Туман с открытой полосой вдоль пройденного пути и кромкой открытого.
private struct FogWithClearing: View {
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        let theme = Theme(colorScheme)
        let trail = TrailPath()
        ZStack {
            Rectangle()
                .fill(FogStyle.haze[theme].color)
                .mask {
                    Rectangle()
                        .overlay {
                            trail.stroke(style: StrokeStyle(lineWidth: 46, lineCap: .round, lineJoin: .round))
                                .blendMode(.destinationOut)
                        }
                        .compositingGroup()
                }
            trail.stroke(FogStyle.edge[theme].color, style: StrokeStyle(lineWidth: 50, lineCap: .round))
                .mask {
                    trail.stroke(style: StrokeStyle(lineWidth: 50, lineCap: .round, lineJoin: .round))
                        .overlay {
                            trail.stroke(style: StrokeStyle(lineWidth: 46, lineCap: .round, lineJoin: .round))
                                .blendMode(.destinationOut)
                        }
                        .compositingGroup()
                }
        }
    }
}

/// Путь пешехода через картинку.
private struct TrailPath: Shape {
    nonisolated func path(in rect: CGRect) -> Path {
        var path = Path()
        path.move(to: CGPoint(x: rect.width * 0.12, y: rect.height * 0.86))
        path.addCurve(
            to: CGPoint(x: rect.width * 0.86, y: rect.height * 0.18),
            control1: CGPoint(x: rect.width * 0.2, y: rect.height * 0.3),
            control2: CGPoint(x: rect.width * 0.62, y: rect.height * 0.7))
        return path
    }
}
