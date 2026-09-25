/// Двенадцать цветов игроков (PLAN.md, §6.5). Набор подобран так, чтобы цвета
/// различались и при дальтонизме. Редкости значков цветами игроков не обозначаются.
///
/// Кромки, текст на «Старте» и заливки уровней — готовые значения из утверждённого макета
/// (docs/design/tokens.md, §2): в рантайме ничего не считается.
public enum PlayerColor: String, CaseIterable, Identifiable, Sendable {
    case red, orange, sun, lime, forest, mint, teal, sky, blue, violet, magenta, coral

    public var id: String { rawValue }

    /// Цвет в формате RGB, как в макетах: 0xRRGGBB. Им закрашен «Старт».
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

    /// Базовый цвет — «Старт» и полоса палитры.
    public var base: RGBA { RGBA(hex) }

    /// Цвет по номеру с сервера (`colorIndex` 0–11 в `GET /me` и у участков) — номера идут в порядке `allCases`.
    /// Номер вне 0–11 берётся по модулю: неожиданный ответ не роняет экран.
    public init(index: Int) {
        let all = Self.allCases
        self = all[(index % all.count + all.count) % all.count]
    }

    /// Светлые цвета: у них база альфы днём 0,55, а не 0,40 (PLAN.md, §6.5), и заливка днём на тон глубже —
    /// тот же оттенок с OKLCH-светлотой 0,76, иначе L1 на светлой земле не виден.
    public var isLight: Bool {
        switch self {
        case .sun, .lime, .mint, .sky, .coral: true
        default: false
        }
    }

    /// Кромка участка и цвет следа. Днём — не меньше 3:1 к земле, ночью — не меньше 4,5:1: светлоту сдвинули,
    /// тон сохранили.
    public var edge: Themed<RGBA> {
        switch self {
        case .red: Themed(day: 0xD30931, night: 0xF62D55)
        case .orange: Themed(day: 0xE85916, night: 0xE66122)
        case .sun: Themed(day: 0xAE8007, night: 0xF8DB8F)
        case .lime: Themed(day: 0x799301, night: 0xA8CC06)
        case .forest: Themed(day: 0x0F7C5D, night: 0x12936E)
        case .mint: Themed(day: 0x089684, night: 0x7EF6E7)
        case .teal: Themed(day: 0x589198, night: 0x5F99A0)
        case .sky: Themed(day: 0x0C8CEC, night: 0x90CBF7)
        case .blue: Themed(day: 0x1A5AE1, night: 0x497DEA)
        case .violet: Themed(day: 0x8E6EFE, night: 0xA38AFC)
        case .magenta: Themed(day: 0xB70388, night: 0xF304B5)
        case .coral: Themed(day: 0xD96554, night: 0xE7A69C)
        }
    }

    /// Текст и значок на «Старте» — одинаковы в обеих темах: «Старт» всегда базового цвета. Контраст не меньше 3:1:
    /// надпись крупная (`TypeRole.startLabel`, 21 pt Heavy), а для крупного текста WCAG требует 3:1.
    public var startInk: RGBA {
        switch self {
        case .red, .orange, .forest, .teal, .blue, .magenta: 0xFFFFFF
        case .sun, .lime, .mint, .sky, .violet, .coral: 0x1C1C1E
        }
    }

    /// Заливка участка уровня `level`: цвет и альфа вместе (уровень — воспринимаемая насыщенность, PLAN.md §6.3).
    public func fill(_ level: TerritoryLevel, theme: Theme) -> RGBA {
        fills[theme][level]
    }

    /// Заливки L1–L3 в двух темах: цвета уровней этого цвета и общие альфы `LevelFills.alphas`.
    public var fills: Themed<LevelFills> {
        let hexes = fillHexes
        return Themed(
            day: LevelFills(hexes.day, alphas: LevelFills.alphas(.day, light: isLight)),
            night: LevelFills(hexes.night, alphas: LevelFills.alphas(.night, light: isLight)))
    }

    /// Цвета заливок L1, L2, L3 из макета: хрома ×0,6 / ×0,8 / ×1. Днём у светлых цветов — на тон глубже.
    private var fillHexes: (day: LevelHexes, night: LevelHexes) {
        switch self {
        case .red: ((0xB14D4E, 0xC33840, 0xD30931), (0xB14D4E, 0xC33840, 0xD30931))
        case .orange: ((0xC7795A, 0xD76E43, 0xE66122), (0xC7795A, 0xD76E43, 0xE66122))
        case .sun: ((0xC0B085, 0xC5AF75, 0xCAAE63), (0xEDDCB1, 0xF3DCA0, 0xF8DB8F))
        case .lime: ((0xA6BC6B, 0xA2BF4B, 0x9FC207), (0xAFC671, 0xABC94F, 0xA8CC06))
        case .forest: ((0x447562, 0x317960, 0x0F7C5D), (0x447562, 0x317960, 0x0F7C5D))
        case .mint: ((0x80BFB6, 0x6AC4B8, 0x4CC8BA), (0xACEDE3, 0x97F1E5, 0x7EF6E7))
        case .teal: ((0x749599, 0x6A979C, 0x5F99A0), (0x749599, 0x6A979C, 0x5F99A0))
        case .sky: ((0x94B6D0, 0x89B7DA, 0x7EB8E4), (0xA6C8E3, 0x9BCAED, 0x90CBF7))
        case .blue: ((0x3E65B2, 0x2E61C9, 0x1A5AE1), (0x3E65B2, 0x2E61C9, 0x1A5AE1))
        case .violet: ((0xA195D8, 0xA290EA, 0xA38AFC), (0xA195D8, 0xA290EA, 0xA38AFC))
        case .magenta: ((0x9C457C, 0xAA3182, 0xB70388), (0x9C457C, 0xAA3182, 0xB70388))
        case .coral: ((0xCEA6A0, 0xD6A29B, 0xDF9E95), (0xD6AEA8, 0xDEAAA2, 0xE7A69C))
        }
    }
}

/// Цвета заливок L1, L2, L3.
typealias LevelHexes = (UInt32, UInt32, UInt32)

/// Уровень участка (PLAN.md, §6.3): на карте — насыщенностью заливки, без подписи «L3».
public enum TerritoryLevel: Int, CaseIterable, Sendable {
    case one = 1, two, three
}

/// Заливки трёх уровней одного цвета в одной теме.
public struct LevelFills: Hashable, Sendable {
    public var one: RGBA
    public var two: RGBA
    public var three: RGBA

    public init(_ one: RGBA, _ two: RGBA, _ three: RGBA) {
        self.one = one
        self.two = two
        self.three = three
    }

    /// Цвета L1–L3 с альфами уровней.
    init(_ hexes: LevelHexes, alphas: (Double, Double, Double)) {
        self.init(RGBA(hexes.0, alpha: alphas.0), RGBA(hexes.1, alpha: alphas.1), RGBA(hexes.2, alpha: alphas.2))
    }

    /// Альфы L1, L2, L3. L2 — база PLAN.md §6.5: 0,40 днём (0,55 у светлых цветов), 0,50 ночью. L1 — база ×0,62,
    /// L3 — база ×1,4, но не больше 0,75 (docs/design/tokens.md, §2). Посчитаны заранее и округлены до сотых.
    public static func alphas(_ theme: Theme, light: Bool) -> (Double, Double, Double) {
        switch (theme, light) {
        case (.day, false): (0.25, 0.40, 0.56)
        case (.day, true): (0.34, 0.55, 0.75)
        case (.night, _): (0.31, 0.50, 0.70)
        }
    }

    public subscript(_ level: TerritoryLevel) -> RGBA {
        switch level {
        case .one: one
        case .two: two
        case .three: three
        }
    }
}
