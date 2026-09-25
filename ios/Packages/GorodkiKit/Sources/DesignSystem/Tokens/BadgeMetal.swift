import Foundation

/// Металл значка — это его редкость (PLAN.md, §6.5): форма и огранка плюс металл, а не цвет игрока.
/// Обычный — бронзовый круг, редкий — серебряный шестигранник, эпический — золотая огранка, легендарный —
/// голографическая звезда.
///
/// Градиенты — из утверждённого макета (docs/design/tokens.md, §2); в SwiftUI их рисует `RarityBadge`, а не картинки.
public enum BadgeMetal: CaseIterable, Sendable {
    case bronze, silver, gold, holo

    /// Форма значка.
    public var shape: BadgeShapeKind {
        switch self {
        case .bronze: .circle
        case .silver: .hexagon
        case .gold: .octagon
        case .holo: .star
        }
    }

    /// Обод: металл по диагонали из верхнего левого угла.
    public var rim: [GradientStop] {
        switch self {
        case .bronze:
            [
                GradientStop(0x7A4A28, at: 0), GradientStop(0xC98A58, at: 0.32), GradientStop(0xF1C49B, at: 0.5),
                GradientStop(0xA9693E, at: 0.7), GradientStop(0x633C20, at: 1),
            ]
        case .silver:
            [
                GradientStop(0x848C95, at: 0), GradientStop(0xD9DEE3, at: 0.35), GradientStop(0xFFFFFF, at: 0.5),
                GradientStop(0xAAB2BA, at: 0.7), GradientStop(0x6E757E, at: 1),
            ]
        case .gold:
            [
                GradientStop(0x94680F, at: 0), GradientStop(0xE8B843, at: 0.33), GradientStop(0xFFF0B2, at: 0.5),
                GradientStop(0xD29E28, at: 0.7), GradientStop(0x7C570C, at: 1),
            ]
        case .holo:
            [
                GradientStop(0xFFB0D6, at: 0), GradientStop(0xFFE7A3, at: 0.2), GradientStop(0xB3F4D6, at: 0.4),
                GradientStop(0xA6D2FF, at: 0.6), GradientStop(0xD5B2FF, at: 0.8), GradientStop(0xFFB0D6, at: 1),
            ]
        }
    }

    /// Лицо значка под глифом. У голографики — линейный градиент снизу слева вверх направо, у остальных — радиальный
    /// с бликом слева сверху.
    public var face: [GradientStop] {
        switch self {
        case .bronze: [GradientStop(0xF2C293, at: 0), GradientStop(0x9A5E33, at: 1)]
        case .silver: [GradientStop(0xFAFBFC, at: 0), GradientStop(0x9AA5B1, at: 1)]
        case .gold: [GradientStop(0xFFF1BD, at: 0), GradientStop(0xD39A2E, at: 1)]
        case .holo:
            [
                GradientStop(0xE9D7FF, at: 0), GradientStop(0xD6F6FF, at: 0.35), GradientStop(0xFFF4C8, at: 0.7),
                GradientStop(0xFFD9EE, at: 1),
            ]
        }
    }

    /// Полоска прогресса редкости в «Коллекции» — слева направо.
    public var progress: [GradientStop] {
        switch self {
        case .bronze: [GradientStop(0x9A5E33, at: 0), GradientStop(0xF1C49B, at: 1)]
        case .silver: [GradientStop(0x8C949D, at: 0), GradientStop(0xF2F4F6, at: 1)]
        case .gold: [GradientStop(0xB8860B, at: 0), GradientStop(0xFFE7A0, at: 1)]
        case .holo: [GradientStop(0xFFB0D6, at: 0), GradientStop(0xA6D2FF, at: 0.5), GradientStop(0xB3F4D6, at: 1)]
        }
    }

    /// Глиф на лице значка.
    public var glyph: RGBA {
        switch self {
        case .bronze: 0x4A2A12
        case .silver: 0x2F363F
        case .gold: 0x553A05
        case .holo: 0x3A2B66
        }
    }

    /// Сияние при находке. Золото — `gold-soft`, голографика — `holo-glow` из макета; у бронзы и серебра (в макете
    /// не показаны) — второй цвет их обода.
    public var glow: RGBA {
        switch self {
        case .bronze: 0xC98A58
        case .silver: 0xD9DEE3
        case .gold: 0xF4C448
        case .holo: RGBA(0xBEA0FF, alpha: 0.45)
        }
    }
}

/// Форма значка. Размеры — в долях половины стороны рамки: так значок из макета (поле 48, центр 24) масштабируется
/// в любой размер.
public enum BadgeShapeKind: CaseIterable, Sendable {
    case circle, hexagon, octagon, star

    /// Радиус обода.
    public var rimRadius: Double {
        switch self {
        case .circle: 21.5 / 24
        case .hexagon: 22.6 / 24
        case .octagon: 23 / 24
        case .star: 23.4 / 24
        }
    }

    /// Радиус лица — внутренней формы под глифом.
    public var faceRadius: Double {
        switch self {
        case .circle: 16 / 24
        case .hexagon: 16.6 / 24
        case .octagon: 16.2 / 24
        case .star: 16.4 / 24
        }
    }

    /// Вершины многоугольника единичного радиуса с центром в нуле, ось y — вниз, как на экране. Круг — тоже
    /// многоугольник, из 72 вершин: на экране его не отличить от круга, а все формы рисуются одним путём.
    public var unitVertices: [(x: Double, y: Double)] {
        func polygon(_ count: Int, startDegrees: Double) -> [(x: Double, y: Double)] {
            (0..<count).map { index in
                let angle = (startDegrees + 360 * Double(index) / Double(count)) * .pi / 180
                return (cos(angle), sin(angle))
            }
        }
        switch self {
        case .circle:
            return polygon(72, startDegrees: -90)
        case .hexagon:
            // Острым углом вверх.
            return polygon(6, startDegrees: -90)
        case .octagon:
            // Гранью вверх: «огранка».
            return polygon(8, startDegrees: -67.5)
        case .star:
            // Восемь лучей; впадины — на 0,735 радиуса.
            return polygon(16, startDegrees: -90).enumerated().map { index, point in
                index.isMultiple(of: 2) ? point : (point.x * 0.735, point.y * 0.735)
            }
        }
    }
}
