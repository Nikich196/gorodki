import SwiftUI

extension Font {
    /// Шрифт роли: SF Pro Rounded, моноширинные цифры (docs/design/tokens.md, §4). Размер постоянный; надпись,
    /// которая должна расти с Dynamic Type, — `View.scaledFont(_:)`.
    public static func role(_ role: TypeRole) -> Font {
        .system(size: role.size, weight: role.fontWeight, design: .rounded).monospacedDigit()
    }
}

extension View {
    /// Шрифт роли, который растёт с Dynamic Type, но не дальше accessibility2 — так HUD не разваливается
    /// (docs/design/tokens.md, §4).
    public func scaledFont(_ role: TypeRole) -> some View {
        modifier(ScaledRoleFont(role: role))
            .dynamicTypeSize(...DynamicTypeSize.accessibility2)
    }

    /// Капс-метка: «НОВАЯ НАХОДКА» — caption semibold, трекинг +0,8, золотой текст.
    public func capsLabel() -> some View {
        font(.caption.weight(.semibold))
            .tracking(0.8)
            .textCase(.uppercase)
            .foregroundStyle(Palette.goldInk.color)
    }
}

extension TypeRole {
    var fontWeight: Font.Weight {
        switch weight {
        case .bold: .bold
        case .heavy: .heavy
        case .black: .black
        }
    }
}

/// Размер роли через `@ScaledMetric`. Предел Dynamic Type ставит `scaledFont` снаружи: так `@ScaledMetric` видит уже
/// ограниченный размер.
private struct ScaledRoleFont: ViewModifier {
    let role: TypeRole
    @ScaledMetric private var size: Double

    init(role: TypeRole) {
        self.role = role
        _size = ScaledMetric(wrappedValue: role.size, relativeTo: .largeTitle)
    }

    func body(content: Content) -> some View {
        content.font(.system(size: size, weight: role.fontWeight, design: .rounded).monospacedDigit())
    }
}
