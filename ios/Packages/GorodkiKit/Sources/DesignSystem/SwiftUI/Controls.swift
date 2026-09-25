import SwiftUI

// Элементы управления вне карты: контентный слой, без стекла (docs/design/tokens.md, §6). Цветной у игры только
// «Старт» (`StartButton`); здесь — нейтральные цвета темы.

/// Нейтральная главная кнопка вне карты — «Дальше», «Готово», «Войти» (docs/design/tokens.md, §6, п. 5): сплошная
/// `Palette.uiButton`, текст `Palette.uiButtonInk`, капсула высотой `Metrics.controlHeight` на всю ширину.
public struct NeutralButtonStyle: ButtonStyle {
    public init() {}

    public func makeBody(configuration: Configuration) -> some View {
        NeutralButtonBody(configuration: configuration)
    }
}

extension ButtonStyle where Self == NeutralButtonStyle {
    /// `.buttonStyle(.neutral)` — нейтральная главная кнопка.
    public static var neutral: NeutralButtonStyle { NeutralButtonStyle() }
}

private struct NeutralButtonBody: View {
    let configuration: ButtonStyleConfiguration
    @Environment(\.isEnabled) private var isEnabled

    var body: some View {
        configuration.label
            .font(.headline)
            .foregroundStyle(Palette.uiButtonInk.color)
            .frame(maxWidth: .infinity, minHeight: Metrics.controlHeight)
            .background(Palette.uiButton.color, in: .capsule)
            .opacity(isEnabled ? (configuration.isPressed ? 0.8 : 1) : 0.3)
            .contentShape(.capsule)
    }
}

/// Отметка «☐ / ☑» — согласия при регистрации (docs/legal): нажимается вся строка с текстом, VoiceOver читает её как
/// переключатель. Своя, потому что системный флажок (`.checkbox`) есть только на Mac.
public struct CheckmarkToggleStyle: ToggleStyle {
    public init() {}

    public func makeBody(configuration: Configuration) -> some View {
        Button {
            configuration.isOn.toggle()
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
        .accessibilityAddTraits(.isToggle)
        .accessibilityValue(configuration.isOn ? Text("отмечено") : Text("не отмечено"))
    }
}

extension View {
    /// Карточка контента: `Palette.uiCell` со скруглением `Radius.card` на фоне `Palette.uiBackground` — без стекла.
    public func contentCard(padding: CGFloat = 16) -> some View {
        self.padding(padding)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Palette.uiCell.color, in: .rect(cornerRadius: Radius.card))
    }
}
