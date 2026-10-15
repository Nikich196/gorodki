import DesignSystem
import GameCore
import GorodkiAPI
import SwiftUI

// Общие кусочки игровых экранов: загрузка раздела, редкость значков, фишки рюкзака, метрики дуэлей, время отрезка.

/// Раздел: грузится при первом показе, ошибка — с «Повторить», иначе содержимое.
struct LoadableContent<Value, Content: View>: View {
    let state: Loadable<Value>
    let load: () async -> Void
    @ViewBuilder let content: (Value) -> Content

    var body: some View {
        Group {
            switch state {
            case .idle:
                ProgressView()
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .atmosphereBackground()
            case .failed(let failure):
                SocialFailureView(failure: failure, retry: load)
            case .loaded(let value):
                content(value)
            }
        }
        .task {
            if case .idle = state { await load() }
        }
    }
}

/// Цветной кружок со значком — как у системных списков, но в цветах раздела.
struct GameBadge: View {
    let systemImage: String
    let color: Color
    var size: CGFloat = 40

    var body: some View {
        Image(systemName: systemImage)
            .font(.system(size: size * 0.42, weight: .bold))
            .foregroundStyle(.white)
            .frame(width: size, height: size)
            .background {
                Circle().fill(
                    LinearGradient(
                        colors: [color.mix(with: .white, by: 0.25), color], startPoint: .topLeading,
                        endPoint: .bottomTrailing))
            }
            .shadow(color: color.opacity(0.35), radius: 6, y: 3)
            .accessibilityHidden(true)
    }
}

// MARK: - Редкость значков (PLAN.md, §3.7)

extension Components.Schemas.BadgeRarity {
    var title: String {
        switch self {
        case .common: "Обычный"
        case .rare: "Редкий"
        case .epic: "Эпический"
        case .legendary: "Легендарный"
        }
    }

    var color: Color {
        switch self {
        case .common: Color(hex: 0x8E8E93)
        case .rare: Color(hex: 0x2F80ED)
        case .epic: Brand.dusk.day.color
        case .legendary: Color(hex: 0xE0A400)
        }
    }

    /// Значок редкости: чем реже, тем «богаче» силуэт.
    var symbol: String {
        switch self {
        case .common: "seal.fill"
        case .rare: "rosette"
        case .epic: "star.circle.fill"
        case .legendary: "crown.fill"
        }
    }
}

// MARK: - Фишки «Рюкзака» (PLAN.md, §3.11)

extension Components.Schemas.ItemKind {
    var title: String {
        switch self {
        case .magnet: "Магнит"
        case .radar: "Радар"
        case .compass: "Компас"
        case .streakFreeze: "Заморозка серии"
        case .chest: "Сундук"
        }
    }

    var effect: String {
        switch self {
        case .magnet: "Петля замыкается с 20 м дальше — на 2 км забега."
        case .radar: "Туман открывается вдвое шире — на 3 км забега."
        case .compass: "Показывает направление к ближайшему тайнику или сундуку."
        case .streakFreeze: "Серия не сгорит за один пропущенный день."
        case .chest: "Стиль следа, рамка аватара или эмблема — что выпадет."
        }
    }

    var symbol: String {
        switch self {
        case .magnet: "arrow.down.right.and.arrow.up.left.circle.fill"
        case .radar: "antenna.radiowaves.left.and.right"
        case .compass: "location.north.circle.fill"
        case .streakFreeze: "snowflake"
        case .chest: "shippingbox.fill"
        }
    }

    var color: Color {
        switch self {
        case .magnet: Brand.dusk.day.color
        case .radar: Brand.river.day.color
        case .compass: PlayerColor.forest.edge.day.color
        case .streakFreeze: PlayerColor.sky.edge.day.color
        case .chest: Brand.amber.day.color
        }
    }
}

// MARK: - Дуэли (PLAN.md, §3.14)

extension Components.Schemas.DuelMetric {
    var title: String {
        switch self {
        case .capturedArea: "Захват"
        case .exploredArea: "Исследование"
        case .distance: "Дистанция"
        case .segmentTime: "Время на отрезке"
        }
    }

    var symbol: String {
        switch self {
        case .capturedArea: "flag.fill"
        case .exploredArea: "cloud.sun.fill"
        case .distance: "figure.run"
        case .segmentTime: "stopwatch.fill"
        }
    }

    /// На отрезке побеждает меньшее время, в остальных метриках — большее число.
    var lowerIsBetter: Bool { self == .segmentTime }

    func format(_ score: Double?) -> String {
        guard let score else { return "—" }
        switch self {
        case .capturedArea, .exploredArea: return NumberText.hectares(fromSquareMeters: score, fractionDigits: 2)
        case .distance: return NumberText.kilometers(fromMeters: score, fractionDigits: 2)
        case .segmentTime: return SegmentTime.text(milliseconds: Int64(score))
        }
    }
}

// MARK: - Время на отрезке

enum SegmentTime {
    /// «1:11,4» — минуты, секунды и десятые: на коротком отрезке десятые решают корону.
    static func text(milliseconds: Int64) -> String {
        let tenths = max(0, (milliseconds + 50) / 100)
        let minutes = tenths / 600
        let seconds = tenths % 600 / 10
        let tenth = tenths % 10
        return "\(minutes):\(seconds < 10 ? "0" : "")\(seconds),\(tenth)"
    }
}
