import SwiftUI

// Элементы управления вне карты: контентный слой, без стекла (docs/design/tokens.md, §6). Цветной у игры только
// «Старт» (`StartButton`); здесь — нейтральные цвета темы.

/// Главная кнопка вне карты — «Дальше», «Готово», «Войти»: с 15.10 выглядит как кнопка бренда (`BrandButtonStyle`),
/// имя `.neutral` оставлено, чтобы не трогать экраны.
public struct NeutralButtonStyle: ButtonStyle {
    public init() {}

    public func makeBody(configuration: Configuration) -> some View {
        // Бренд «Крепость» (15.10): главная кнопка вне карты — «огонь» бренда (`BrandButtonStyle`).
        BrandButtonStyle().makeBody(configuration: configuration)
    }
}

extension ButtonStyle where Self == NeutralButtonStyle {
    /// `.buttonStyle(.neutral)` — нейтральная главная кнопка.
    public static var neutral: NeutralButtonStyle { NeutralButtonStyle() }
}

/// Отметка «☐ / ☑» — согласия при регистрации (docs/legal): нажимается вся строка с текстом, VoiceOver читает её как
/// переключатель. Своя, потому что системный флажок (`.checkbox`) есть только на Mac.
public struct CheckmarkToggleStyle: ToggleStyle {
    public init() {}

    public func makeBody(configuration: Configuration) -> some View {
        CheckmarkToggle(configuration: configuration)
    }
}

/// Отметка со щелчком и вибрацией выбора (бренд «Крепость», 15.10).
private struct CheckmarkToggle: View {
    let configuration: ToggleStyleConfiguration
    @Environment(\.brandSound) private var sound

    var body: some View {
        Button {
            configuration.isOn.toggle()
            sound.play(.toggle)
        } label: {
            HStack(alignment: .firstTextBaseline, spacing: 12) {
                Image(systemName: configuration.isOn ? "checkmark.square.fill" : "square")
                    .font(.title3)
                    .foregroundStyle(configuration.isOn ? Palette.uiInk.color : Palette.uiInk2.color)
                configuration.label
                    .font(.body)
                    .foregroundStyle(Palette.uiInk.color)
                    .multilineTextAlignment(.leading)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .sensoryFeedback(.selection, trigger: configuration.isOn)
        .accessibilityAddTraits(.isToggle)
        .accessibilityValue(configuration.isOn ? Text("отмечено") : Text("не отмечено"))
    }
}

extension View {
    /// Карточка контента: стекло бренда со светлой кромкой (`brandCard`) поверх живого фона.
    public func contentCard(padding: CGFloat = 16) -> some View {
        self.padding(padding)
            .frame(maxWidth: .infinity, alignment: .leading)
            .brandCard()
    }
}
