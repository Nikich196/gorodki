import SwiftUI

/// Двенадцать цветов игроков (PLAN.md, §6.5). Набор подобран так, чтобы цвета
/// различались и при дальтонизме. Редкости значков цветами игроков не обозначаются.
public enum PlayerColor: String, CaseIterable, Identifiable, Sendable {
    case red, orange, sun, lime, forest, mint, teal, sky, blue, violet, magenta, coral

    public var id: String { rawValue }

    /// Цвет в формате RGB, как в макетах: 0xRRGGBB.
    public var hex: UInt32 {
        switch self {
        case .red: 0xD30931
        case .orange: 0xE66122
        case .sun: 0xF8DB8F
        case .lime: 0xA8CC06
        case .forest: 0x0F7C5D
        case .mint: 0x7EF6E7
        case .teal: 0x5F99A0
        case .sky: 0x90CBF7
        case .blue: 0x1A5AE1
        case .violet: 0xA38AFC
        case .magenta: 0xB70388
        case .coral: 0xE7A69C
        }
    }

    public var color: Color { Color(hex: hex) }
}

extension Color {
    /// Цвет из шестнадцатеричного RGB, например `Color(hex: 0xD30931)`.
    public init(hex: UInt32) {
        self.init(
            .sRGB,
            red: Double((hex >> 16) & 0xFF) / 255,
            green: Double((hex >> 8) & 0xFF) / 255,
            blue: Double(hex & 0xFF) / 255,
            opacity: 1
        )
    }
}
