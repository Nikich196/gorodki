import SwiftUI

/// Готовые анимации шести приёмов движения (docs/design/tokens.md, §7); числа — `MotionSpec`. Пружины (`Spring`)
/// и кривые (`UnitCurve`) — для ключевых кадров `KeyframeAnimator`, `Animation` — для `withAnimation`.
/// «Уменьшить движение» — `Animation.unlessReduceMotion(_:)`: итоговое состояние сразу, хаптика остаётся.
public enum Motion {
    // MARK: - Церемония захвата

    /// Обводка по следу, 0,6 с.
    public static let captureOutline = Animation.timingCurve(
        MotionSpec.Capture.outlineCurve, duration: MotionSpec.Capture.outlineDuration)
    /// Кривая обводки для `LinearKeyframe(…, timingCurve:)`.
    public static var captureOutlineCurve: UnitCurve { UnitCurve(MotionSpec.Capture.outlineCurve) }
    /// Заливка расходится от точки замыкания: `.smooth` на 0,5–1,3 с.
    public static var captureFill: Spring {
        .smooth(duration: MotionSpec.Capture.fillEnd - MotionSpec.Capture.fillStart)
    }
    /// «≈ +1,2 га» появляется пружиной с лёгким перелётом.
    public static var captureEstimate: Spring {
        Spring(duration: MotionSpec.Capture.estimateSpring.duration, bounce: MotionSpec.Capture.estimateSpring.bounce)
    }
    /// После «Продолжить забег»: карточка уходит вниз.
    public static let captureCardDismiss = Animation.timingCurve(
        MotionSpec.Capture.cardDismissCurve, duration: MotionSpec.Capture.cardDismissDuration)
    /// После «Продолжить забег»: заливка оседает с L3 до L1.
    public static let captureSettle = Animation.easeInOut(duration: MotionSpec.Capture.settleDuration)
        .delay(MotionSpec.Capture.settleDelay)

    // MARK: - Туман дышит

    /// Вдох кольца у бегущего: 25 → 28 м.
    public static let fogBreathExpand = Animation.timingCurve(
        MotionSpec.FogBreath.expandCurve, duration: MotionSpec.FogBreath.expandDuration)
    /// Выдох: 28 → 25 м до конца секунды.
    public static let fogBreathContract = Animation.timingCurve(
        MotionSpec.FogBreath.contractCurve,
        duration: MotionSpec.FogBreath.period - MotionSpec.FogBreath.expandDuration)

    // MARK: - Находка тайника

    /// Значок падает с перелётом.
    public static var badgeDrop: Spring {
        Spring(duration: MotionSpec.BadgeDrop.spring.duration, bounce: MotionSpec.BadgeDrop.spring.bounce)
    }
    /// «Уменьшить движение»: значок просто проявляется.
    public static let reducedAppear = Animation.easeOut(duration: MotionSpec.BadgeDrop.reducedFade)

    // MARK: - Цифры и муравьи

    /// Цифры катятся при смене значения: с `.contentTransition(.numericText(value:))`.
    public static let numericRoll = Animation.snappy(duration: MotionSpec.NumericRoll.change)
    /// Цифры накатываются при появлении карточки.
    public static let numericAppear = Animation.smooth(duration: MotionSpec.NumericRoll.appear)
    /// «Бегущие муравьи»: фаза пунктира на `MotionSpec.Ants.phaseShift` за период, по кругу.
    public static let marchingAnts = Animation.linear(duration: MotionSpec.Ants.period)
        .repeatForever(autoreverses: false)
}

extension Animation {
    /// `nil` при «Уменьшить движение»: `withAnimation(Motion.numericRoll.unlessReduceMotion(reduceMotion)) { … }`
    /// меняет состояние сразу. `reduceMotion` — `@Environment(\.accessibilityReduceMotion)`.
    public func unlessReduceMotion(_ reduceMotion: Bool) -> Animation? {
        reduceMotion ? nil : self
    }

    /// Анимация по кривой Безье из `MotionSpec`.
    static func timingCurve(_ curve: CubicBezier, duration: TimeInterval) -> Animation {
        .timingCurve(curve.x1, curve.y1, curve.x2, curve.y2, duration: duration)
    }
}

extension UnitCurve {
    /// Кривая для ключевых кадров (`LinearKeyframe(…, timingCurve:)`) из `MotionSpec`.
    public init(_ curve: CubicBezier) {
        self = .bezier(
            startControlPoint: UnitPoint(x: curve.x1, y: curve.y1),
            endControlPoint: UnitPoint(x: curve.x2, y: curve.y2))
    }
}
