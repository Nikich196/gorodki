import DesignSystem
import SwiftUI

/// Три карточки интро (PLAN.md, §3.18: «Захватывай город шагами», обучение). Картинка карточки — живая сцена на карте
/// Бреста за панелью (`IntroBackdrop`), у каждой — свой цвет точки страниц: своя земля, туман, соперник.
struct IntroCard: Identifiable {
    let id: Int
    let title: String
    let text: String

    /// Метка над заголовком: тема и номер карточки.
    var tag: String {
        ["ЗАХВАТ · 1 ИЗ 3", "ИССЛЕДОВАНИЕ · 2 ИЗ 3", "СОПЕРНИКИ · 3 ИЗ 3"][min(id, 2)]
    }

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

    /// Цвет точки страницы: своя земля (цвет игрока), кромка тумана, соперник.
    func accent(_ theme: Theme) -> Color {
        switch id {
        case 0: PlayerColor.sky.edge[theme].color
        case 1: FogStyle.edge[theme].color
        default: PlayerColor.coral.edge[theme].color
        }
    }
}

/// Текст карточки в панели интро.
struct IntroCardView: View {
    let card: IntroCard

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(card.title)
                .font(.system(.title, design: .rounded, weight: .heavy))
                .foregroundStyle(Palette.uiInk.color)
                .minimumScaleFactor(0.8)
            Text(card.text)
                .font(.body)
                .foregroundStyle(Palette.uiInk2.color)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .accessibilityElement(children: .combine)
    }
}

/// «ГОРОДКИ» над картой: знак-крепость (прорисовывается при запуске), надпись бренда, город и полоса из 12 цветов
/// игроков — у каждого игрока свой цвет на карте.
struct IntroWordmark: View {
    var body: some View {
        HStack(alignment: .center, spacing: 14) {
            BrandMark(size: 62, animated: true)
            VStack(alignment: .leading, spacing: 6) {
                BrandWordmark(size: 36)
                    .foregroundStyle(Palette.uiInk.color)
                HStack(spacing: 10) {
                    Label("Брест", systemImage: "mappin.and.ellipse")
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(Palette.uiInk.color)
                    PlayerPaletteStrip()
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Городки. Брест")
        .accessibilityAddTraits(.isHeader)
    }
}

/// Полоса цветов игроков — точки с кромкой, как участки на карте.
private struct PlayerPaletteStrip: View {
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        let theme = Theme(colorScheme)
        HStack(spacing: 3) {
            ForEach(PlayerColor.allCases) { color in
                Circle()
                    .fill(color.fill(.three, theme: theme).color)
                    .overlay { Circle().stroke(color.edge[theme].color, lineWidth: 1.2) }
                    .frame(width: 9, height: 9)
            }
        }
    }
}

/// Точки страниц интро: текущая — вытянутая, цвета карточки.
struct IntroPageDots: View {
    let current: Int
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let theme = Theme(colorScheme)
        HStack(spacing: 6) {
            ForEach(IntroCard.all) { card in
                Capsule()
                    .fill(card.id == current ? card.accent(theme) : Palette.uiInk3.color.opacity(0.5))
                    .frame(width: card.id == current ? 24 : 8, height: 8)
            }
        }
        .animation(Motion.layerSwitch.unlessReduceMotion(reduceMotion), value: current)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Карточка \(current + 1) из \(IntroCard.all.count)")
    }
}
