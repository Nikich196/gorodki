import SwiftUI

// Стекло — только на слое управления: таб-бар, кнопки и сегменты карты, плашки и панель HUD, карточка церемонии,
// «Старт», карточка «Исследование» (PLAN.md, §6.2; docs/design/tokens.md, §6). Контент — без стекла: `Palette.uiCell`
// на `Palette.uiBackground`. Всё ниже — нативный Liquid Glass iOS 26; своего стекла не рисуем. Соседние стеклянные
// элементы — в одном `GlassEffectContainer`, чтобы сливались, а не наслаивались.

extension View {
    /// Панель HUD и карточка церемонии: `.regular` в прямоугольнике со скруглением 44 pt.
    public func panelGlass() -> some View {
        glassEffect(.regular, in: .rect(cornerRadius: Radius.panel))
    }

    /// Плашки и сегменты на карте — стеклянная капсула. `interactive` — для того, что нажимают: стекло отвечает
    /// на касание.
    public func capsuleGlass(interactive: Bool = false) -> some View {
        glassEffect(.regular.interactive(interactive), in: .capsule)
    }

    /// Системный сегментный `Picker` на стекле: капсула высотой 46 pt («Захват | Исследование»).
    public func segmentedGlass() -> some View {
        padding(4)
            .frame(height: Metrics.segmentHeight)
            .capsuleGlass()
    }
}

/// «Старт» — единственный цветной элемент управления (PLAN.md, §6.2): `.glassProminent` цвета игрока, текст — цвет
/// `PlayerColor.startInk` (контраст ≥ 3:1). Капсула высотой 64 pt. Блик по стеклу рисует сама система — своего нет.
public struct StartButton<Label: View>: View {
    private let player: PlayerColor
    private let action: () -> Void
    private let label: Label

    public init(player: PlayerColor, action: @escaping () -> Void, @ViewBuilder label: () -> Label) {
        self.player = player
        self.action = action
        self.label = label()
    }

    public var body: some View {
        Button(action: action) {
            label
                .font(.role(.startLabel))
                .foregroundStyle(player.startInkColor)
                .padding(.horizontal, 12)
                // Растянуть надпись на всю высоту: тогда капсула стиля ровно 64 pt, какими бы ни были его отступы.
                .frame(maxHeight: .infinity)
        }
        .buttonStyle(.glassProminent)
        .buttonBorderShape(.capsule)
        .controlSize(.extraLarge)
        .tint(player.color)
        .frame(height: Metrics.controlHeight)
    }
}

/// Круглая кнопка карты 50 pt на стекле. Несколько кнопок — в `GlassEffectContainer`, они сольются в колонку.
public struct MapGlassButton: View {
    private let title: LocalizedStringKey
    private let systemImage: String
    private let action: () -> Void

    /// `title` — для VoiceOver: на экране только значок.
    public init(_ title: LocalizedStringKey, systemImage: String, action: @escaping () -> Void) {
        self.title = title
        self.systemImage = systemImage
        self.action = action
    }

    public var body: some View {
        Button(action: action) {
            Label(title, systemImage: systemImage)
                .labelStyle(.iconOnly)
                .font(.title3.weight(.semibold))
                .frame(width: Metrics.mapButton, height: Metrics.mapButton)
                .contentShape(.circle)
        }
        .buttonStyle(.plain)
        .glassEffect(.regular.interactive(), in: .circle)
    }
}
