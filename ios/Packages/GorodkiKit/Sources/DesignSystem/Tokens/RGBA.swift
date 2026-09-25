import Foundation

/// Цвет sRGB с непрозрачностью — так токены записаны в макете: `0xD30931`, `RGBA(0x3C3C43, alpha: 0.74)`.
///
/// Токены — данные без SwiftUI и UIKit: тесты проверяют их прямо по hex (контраст, альфы из PLAN.md §6.5), а в `Color`
/// и `UIColor` их переводит `Color+Tokens.swift`.
public struct RGBA: Hashable, Sendable, ExpressibleByIntegerLiteral {
    /// 0xRRGGBB.
    public var hex: UInt32
    /// Непрозрачность 0…1.
    public var alpha: Double

    public init(_ hex: UInt32, alpha: Double = 1) {
        self.hex = hex
        self.alpha = alpha
    }

    /// Непрозрачный цвет из литерала: `let ink: RGBA = 0x1C1C1E`.
    public init(integerLiteral hex: UInt32) {
        self.init(hex)
    }

    public var red: Double { Double((hex >> 16) & 0xFF) / 255 }
    public var green: Double { Double((hex >> 8) & 0xFF) / 255 }
    public var blue: Double { Double(hex & 0xFF) / 255 }

    /// Тот же цвет с другой непрозрачностью.
    public func withAlpha(_ alpha: Double) -> RGBA {
        RGBA(hex, alpha: alpha)
    }

    /// «#D30931» — как в макете и в docs/design/tokens.md.
    public var hexString: String {
        let digits = String(hex, radix: 16, uppercase: true)
        return "#" + String(repeating: "0", count: max(0, 6 - digits.count)) + digits
    }

    /// Относительная яркость по WCAG 2 (непрозрачность не учитывается).
    public var relativeLuminance: Double {
        func linear(_ channel: Double) -> Double {
            channel <= 0.040_45 ? channel / 12.92 : pow((channel + 0.055) / 1.055, 2.4)
        }
        return 0.2126 * linear(red) + 0.7152 * linear(green) + 0.0722 * linear(blue)
    }

    /// Контраст по WCAG 2 — от 1 (1:1) до 21 (21:1). Для непрозрачных цветов.
    public func contrastRatio(to other: RGBA) -> Double {
        let lighter = max(relativeLuminance, other.relativeLuminance)
        let darker = min(relativeLuminance, other.relativeLuminance)
        return (lighter + 0.05) / (darker + 0.05)
    }
}

/// Тема: светлая тема iOS — «день», тёмная — «ночь». Игра следует теме системы, своего переключателя у неё нет
/// (docs/design/tokens.md, §1).
public enum Theme: CaseIterable, Sendable {
    case day, night
}

/// Значение токена в двух темах: `Palette.uiBackground[.night]`.
public struct Themed<Value> {
    public var day: Value
    public var night: Value

    public init(day: Value, night: Value) {
        self.day = day
        self.night = night
    }

    /// Одно значение для обеих тем.
    public init(_ both: Value) {
        self.init(day: both, night: both)
    }

    public subscript(_ theme: Theme) -> Value {
        switch theme {
        case .day: day
        case .night: night
        }
    }
}

extension Themed: Sendable where Value: Sendable {}
extension Themed: Equatable where Value: Equatable {}
extension Themed: Hashable where Value: Hashable {}
