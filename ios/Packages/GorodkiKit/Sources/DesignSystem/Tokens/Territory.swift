/// Отношение к земле (PLAN.md, §6.3): передаётся узором и толщиной, а не цветом, поэтому различимо и при дальтонизме.
/// Уровень — отдельно, насыщенностью заливки (`PlayerColor.fill`).
///
/// Здесь только числа и цвета — их берут рендереры карты (`MKOverlayRenderer`, `CAShapeLayer`, docs/design/tokens.md,
/// §3.1). Толщины и пунктиры — в экранных pt.
public enum TerritoryRelation: CaseIterable, Sendable {
    /// Моё: заливка уровня как есть, сплошная кромка 2,4 pt, ночью — свечение 3 pt.
    case mine
    /// Клан: заливка бледнее и штриховка 45°.
    case clan
    /// Соперник: просто бледнее, кромка тоньше.
    case rival
    /// Спорное или треснувшее: «бегущие муравьи» по кромке.
    case contested
    /// Потеряно: призрак 3 дня, пунктир.
    case lost
    /// «Ничейные земли».
    case noMansLand

    /// Заливка: `levelFill` — цвет уровня владельца (`PlayerColor.fill`); призрак и ничейные — свои цвета темы.
    public func fill(levelFill: RGBA, theme: Theme) -> RGBA {
        switch self {
        case .mine: levelFill
        case .clan: levelFill.withAlpha(levelFill.alpha * 0.42)
        case .rival, .contested: levelFill.withAlpha(levelFill.alpha * 0.55)
        case .lost: Palette.ghost[theme].withAlpha(0.07)
        case .noMansLand: Palette.neutral[theme].withAlpha(0.22)
        }
    }

    /// Цвет кромки: `ownerEdge` — кромка владельца (`PlayerColor.edge`).
    public func edgeColor(ownerEdge: RGBA, theme: Theme) -> RGBA {
        switch self {
        case .mine, .clan: ownerEdge
        case .rival: ownerEdge.withAlpha(0.8)
        case .contested: Palette.antsInk[theme]
        case .lost: Palette.ghost[theme]
        case .noMansLand: Palette.neutral[theme].withAlpha(0.85)
        }
    }

    /// Толщина и пунктир кромки.
    public var edge: EdgeStroke {
        switch self {
        case .mine: EdgeStroke(width: 2.4)
        case .clan: EdgeStroke(width: 1.5)
        case .rival: EdgeStroke(width: 1.1)
        case .contested: EdgeStroke(width: 1.7, dash: [4, 4], haloWidth: 2.6)
        case .lost: EdgeStroke(width: 1.4, dash: [2.5, 3])
        case .noMansLand: EdgeStroke(width: 1)
        }
    }

    /// Штриховка — только у клана.
    public var hatch: Hatch? {
        self == .clan ? Hatch() : nil
    }

    /// Свечение кромки своей земли: ночью 3 pt того же цвета, днём нет (`glow-r`).
    public func glowRadius(theme: Theme) -> Double {
        self == .mine && theme == .night ? 3 : 0
    }

    /// Пунктир кромки ползёт (`MotionSpec.Ants`). Постоянно на карте двигается только это — и дыхание тумана в HUD.
    public var hasMarchingAnts: Bool {
        self == .contested
    }
}

/// Кромка участка: толщина и пунктир, pt на экране.
public struct EdgeStroke: Hashable, Sendable {
    public var width: Double
    /// Штрих и пробел, pt; пусто — сплошная линия.
    public var dash: [Double]
    /// Подложка под пунктиром цвета `Palette.antsHalo` (спорная земля), толщина в pt.
    public var haloWidth: Double?

    public init(width: Double, dash: [Double] = [], haloWidth: Double? = nil) {
        self.width = width
        self.dash = dash
        self.haloWidth = haloWidth
    }
}

/// Штриховка клана: линии под 45° с постоянным шагом на экране при любом зуме (в рендерере — `6 / zoomScale`,
/// docs/design/tokens.md, §3.1).
public struct Hatch: Hashable, Sendable {
    public var angleDegrees = 45.0
    public var lineWidth = 1.9
    public var spacing = 6.0

    public init() {}

    /// Цвет линий: цвет уровня, альфа уровня ×1,8, но не больше 0,95.
    public func color(levelFill: RGBA) -> RGBA {
        levelFill.withAlpha(min(levelFill.alpha * 1.8, 0.95))
    }
}

/// След бегущего и пунктир до точки замыкания (docs/design/tokens.md, §3).
public enum TrailStyle {
    /// След: цвет — кромка игрока.
    public static let width = 5.5
    /// Подложка следа цвета `Palette.trailCase`.
    public static let caseWidth = 9.0
    /// Пунктир до точки замыкания цвета `Palette.gapInk`, круглые концы.
    public static let gapWidth = 2.4
    /// Штрих и пробел пунктира до точки замыкания: точки с круглыми концами.
    public static let gapDash: [Double] = [0.5, 5.5]
}

/// Туман «Исследование» (PLAN.md, §6.4): своя тёплая палитра, никогда не цвет игрока. Туман есть только на слое
/// «Исследование» и в HUD забега; на «Захвате» и на церемонии захвата его нет.
///
/// Альфы — стартовые: их перенастраивают по снимкам MapKit Бреста в обеих темах на спайке S4 (в пределах
/// `calibrationRange`).
public enum FogStyle {
    /// Дымка с альфой: тёплый серый днём, тёмный тёплый серый ночью.
    public static let haze = Themed(day: RGBA(0xD0C1A6, alpha: 0.76), night: RGBA(0x6A655E, alpha: 0.66))
    /// Пределы альфы дымки при калибровке на устройстве (PLAN.md, §6.4).
    public static let calibrationRange = 0.6...0.8
    /// Кромка открытого: днём охра, ночью янтарь.
    public static let edge = Themed(day: RGBA(0x9A6C35, alpha: 0.9), night: RGBA(0xC4884A, alpha: 0.9))
    /// Прогресс «открыто» и значок «+0,8 га тумана».
    public static let exploreFill = Themed<RGBA>(day: 0x9A6C35, night: 0xC4884A)
    /// Земля под туманом — фон карточки недели.
    public static let foggedLand = Themed<RGBA>(day: 0xD7CCB6, night: 0x4C4B4B)
    /// Полоса кромки сразу снаружи пробежанного, pt на экране.
    public static let edgeBandWidth = 1.8
    /// Размытие полосы кромки, pt.
    public static let edgeBandBlur = 0.5
    /// Мягкий край дымки: σ размытия растра тайла в клетках тумана (≈ 12 м при клетке ≈ 5,87 м).
    public static let softEdgeSigmaCells = 2.0
    /// Кольцо у бегущего «дышит» раз в секунду: радиус, м (`MotionSpec.FogBreath`).
    public static let breathingRadiusMeters = 25.0...28.0
}
