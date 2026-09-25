/// Движение — шесть приёмов, каждый что-то сообщает (docs/design/tokens.md, §7). Здесь — числа: секунды от начала
/// приёма; готовые `Animation` — в `Motion.swift`. Всё укладывается в 2 с (PLAN.md, §6.8). «Уменьшить движение» —
/// итоговое состояние сразу, хаптика остаётся.
public enum MotionSpec {
    /// Предел любой церемонии, с (PLAN.md, §6.8).
    public static let ceremonyLimit = 2.0

    /// Церемония захвата: обводка по следу → заливка расходится кругом от точки замыкания → «≈ +1,2 га» →
    /// «подтверждено». Итог держится ярким (L3); до L1 участок оседает только после «Продолжить забег».
    public enum Capture {
        /// Обводка: 0–0,6 с по кривой `outlineCurve`.
        public static let outlineDuration = 0.6
        public static let outlineCurve = CubicBezier(0.45, 0.05, 0.25, 1)
        /// Заливка расходится от точки замыкания: 0,5–1,3 с, `.smooth`.
        public static let fillStart = 0.5
        public static let fillEnd = 1.3
        /// Кольцо-импульс из точки замыкания: 0,5–1,5 с, масштаб 0,5 → 3,4.
        public static let ringStart = 0.5
        public static let ringEnd = 1.5
        public static let ringScale = 0.5...3.4
        /// «≈ +1,2 га» появляется пружиной.
        public static let estimateAt = 0.75
        public static let estimateSpring = (duration: 0.35, bounce: 0.3)
        /// «подтверждено» — и хаптика `.success`.
        public static let confirmedAt = 1.45
        /// Итог на месте.
        public static let total = 1.9
        /// После «Продолжить забег»: карточка уходит вниз, заливка L3 → L1.
        public static let cardDismissDuration = 0.38
        public static let cardDismissCurve = CubicBezier(0.3, 0.7, 0.2, 1)
        public static let settleDelay = 0.15
        public static let settleDuration = 0.7
    }

    /// Туман «дышит» у бегущего: кольцо 25 → 28 → 25 м раз в секунду, в такт тику GPS. Тайл тумана обновляется
    /// раз в секунду без анимации.
    public enum FogBreath {
        public static let period = 1.0
        /// Вдох: 0–0,35 с, ease-out.
        public static let expandDuration = 0.35
        public static let expandCurve = CubicBezier(0.2, 0.7, 0.3, 1)
        /// Выдох: 0,35–1 с, ease-in-out.
        public static let contractCurve = CubicBezier(0.5, 0, 0.5, 1)
    }

    /// Находка тайника: значок падает с перелётом и лёгким поворотом, сияние цвета металла, блик, текст поднимается.
    public enum BadgeDrop {
        /// Падение: пружина 0–0,72 с сверху с поворотом.
        public static let spring = (duration: 0.7, bounce: 0.35)
        public static let startOffsetY = -320.0
        public static let startRotationDegrees = -16.0
        /// Касание: хаптика `.impact(weight: .heavy)` и начало сияния.
        public static let touchAt = 0.5
        /// Сияние: 0,5–1,6 с.
        public static let glowEnd = 1.6
        /// Металлический блик: 1,0–1,6 с.
        public static let glintStart = 1.0
        public static let glintEnd = 1.6
        /// Текст поднимается: 0,78–1,35 с.
        public static let textStart = 0.78
        public static let textEnd = 1.35
        /// «Уменьшить движение»: просто плавное появление.
        public static let reducedFade = 0.2
    }

    /// Цифры катятся (`.contentTransition(.numericText())`): на смену ≈ 0,35 с, при появлении карточки ≈ 0,9 с.
    public enum NumericRoll {
        public static let change = 0.35
        public static let appear = 0.9
    }

    /// «Бегущие муравьи» на спорной земле: пунктир ползёт на −8 pt за 1,2 с, линейно, по кругу.
    public enum Ants {
        public static let phaseShift = -8.0
        public static let period = 1.2
    }
}

/// Кривая Безье для анимации: опорные точки (x1, y1) и (x2, y2), как у CSS `cubic-bezier`.
public struct CubicBezier: Hashable, Sendable {
    public var x1: Double
    public var y1: Double
    public var x2: Double
    public var y2: Double

    public init(_ x1: Double, _ y1: Double, _ x2: Double, _ y2: Double) {
        self.x1 = x1
        self.y1 = y1
        self.x2 = x2
        self.y2 = y2
    }
}
