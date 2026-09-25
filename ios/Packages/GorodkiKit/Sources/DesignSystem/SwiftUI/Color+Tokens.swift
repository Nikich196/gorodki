import SwiftUI
import UIKit

// Токены — Swift-данные (`Palette`, `PlayerColor`, `FogStyle`, `BadgeMetal`), а не Color Set в Asset Catalog: один источник,
// который проверяют тесты, без сотни сгенерированных Contents.json и без ресурсов пакета, общего для приложения
// и расширения. Тема выбирается так же, как в Asset Catalog: динамический `UIColor` смотрит на `userInterfaceStyle`.

extension RGBA {
    /// Цвет SwiftUI — один и тот же в обеих темах.
    public var color: Color {
        Color(.sRGB, red: red, green: green, blue: blue, opacity: alpha)
    }

    /// Цвет UIKit — для рендереров карты и Core Graphics.
    public var uiColor: UIColor {
        UIColor(red: red, green: green, blue: blue, alpha: alpha)
    }
}

extension Themed where Value == RGBA {
    /// Цвет, который сам берёт значение темы: светлая тема iOS — «день», тёмная — «ночь». Сменилась тема — SwiftUI
    /// перерисует его сам.
    public var color: Color {
        Color(uiColor: uiColor)
    }

    /// Динамический `UIColor` — как Color Set с вариантами Any / Dark. Рендереру карты, который рисует вне дерева
    /// представлений, надёжнее брать значение темы явно: `Palette.fog[theme].uiColor`.
    public var uiColor: UIColor {
        let day = self.day
        let night = self.night
        return UIColor { traits in
            traits.userInterfaceStyle == .dark ? night.uiColor : day.uiColor
        }
    }
}

extension Theme {
    /// Тема из окружения SwiftUI: `Theme(colorScheme)`.
    public init(_ colorScheme: ColorScheme) {
        self = colorScheme == .dark ? .night : .day
    }
}

extension PlayerColor {
    /// Базовый цвет: «Старт», полоса палитры.
    public var color: Color { base.color }

    /// Кромка участка и след — цвет темы.
    public var edgeColor: Color { edge.color }

    /// Текст и значок на «Старте».
    public var startInkColor: Color { startInk.color }

    /// Заливка уровня — цвет темы, альфа уже внутри. Отношение домножает её `.opacity(…)` (`TerritoryRelation.fill`).
    public func fillColor(_ level: TerritoryLevel) -> Color {
        Themed(day: fill(level, theme: .day), night: fill(level, theme: .night)).color
    }
}

extension Color {
    /// Цвет из шестнадцатеричного RGB, например `Color(hex: 0xD30931)`.
    public init(hex: UInt32) {
        self = RGBA(hex).color
    }
}

extension Array where Element == GradientStop {
    /// Точки градиента SwiftUI.
    public var gradient: Gradient {
        Gradient(stops: map { Gradient.Stop(color: $0.color.color, location: $0.location) })
    }
}
