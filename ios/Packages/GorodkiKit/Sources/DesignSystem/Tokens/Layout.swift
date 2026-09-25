/// Радиусы скругления, pt (docs/design/tokens.md, §5).
public enum Radius {
    /// Панель HUD и карточка церемонии.
    public static let panel = 44.0
    /// Плашки HUD.
    public static let plaque = 22.0
    /// Карточки контента (24–26).
    public static let card = 24.0
    /// Плитки профиля и коллекции.
    public static let tile = 22.0
    /// Мини-прогресс.
    public static let miniProgress = 16.0
    /// Карточка недели.
    public static let weeklyCard = 26.0
}

/// Размеры, pt (docs/design/tokens.md, §5).
public enum Metrics {
    /// Кнопки HUD, «Старт», «Финиш» — капсула высотой 64 (PLAN.md, §6.7: «кнопки ≥ 64 pt»).
    public static let controlHeight = 64.0
    /// Сегменты на стекле.
    public static let segmentHeight = 46.0
    /// Строка режимов карты («Игроки | Кланы | Отношения»).
    public static let modeRowHeight = 38.0
    /// Кнопка карты — круг в колонке шириной 50.
    public static let mapButton = 50.0
    /// Навигационные кружки.
    public static let navigationCircle = 44.0
    /// Отступ панели HUD и карточки церемонии от краёв экрана.
    public static let panelInset = 10.0
    /// Деление шкалы «горячо»: пять делений 24 × 5 с зазором 3.
    public static let hotScaleSegment = (width: 24.0, height: 5.0, gap: 3.0)
    /// Карточка недели 9:16: превью на экране и экспорт в пикселях.
    public static let weeklyCardPreview = (width: 306.0, height: 544.0)
    public static let weeklyCardExport = (width: 1_080.0, height: 1_920.0)
}

/// Роли крупных надписей: SF Pro Rounded, у цифр — моноширинные цифры (docs/design/tokens.md, §4). Остальной текст —
/// системные стили: `.largeTitle.bold()` для заголовков экранов, `.headline` для блоков, `.body`, `.caption`.
public enum TypeRole: CaseIterable, Sendable {
    /// Главная цифра HUD: «До замыкания 140 м» (PLAN.md, §6.7: Rounded Bold 64–80).
    case hudHero
    /// Единица рядом с главной цифрой: «м».
    case hudHeroUnit
    /// Число церемонии захвата: «≈ +1,2 га» → «+1,25 га».
    case ceremony
    /// Единица числа церемонии.
    case ceremonyUnit
    /// Герой карточки недели: «+2,1 га».
    case weeklyHero
    /// Метрики HUD: 3,2 км · 5:32 /км · 17:42.
    case hudMetric
    /// Единица метрики HUD.
    case hudMetricUnit
    /// Очки сезона: «2 340 очков».
    case seasonPoints
    /// Плитки профиля: «6,8 га».
    case statTile
    /// Надпись «Старт».
    case startLabel

    /// Размер, pt.
    public var size: Double {
        switch self {
        case .hudHero: 78
        case .hudHeroUnit: 32
        case .ceremony: 62
        case .ceremonyUnit: 28
        case .weeklyHero: 72
        case .hudMetric: 29
        case .hudMetricUnit: 15
        case .seasonPoints: 44
        case .statTile: 28
        case .startLabel: 21
        }
    }

    public var weight: TypeWeight {
        switch self {
        case .hudHero, .hudHeroUnit, .hudMetricUnit: .bold
        case .ceremony, .ceremonyUnit, .hudMetric, .seasonPoints, .statTile, .startLabel: .heavy
        case .weeklyHero: .black
        }
    }
}

/// Насыщенность шрифта ролей.
public enum TypeWeight: Sendable {
    case bold, heavy, black
}
