/// Цвета тем из утверждённого макета (docs/design/tokens.md, §2). Цвета игроков — `PlayerColor`, туман — `FogStyle`,
/// металлы редкостей — `BadgeMetal`.
///
/// Стекла здесь нет: на телефоне это нативный Liquid Glass (`Glass.swift`), его цвета задаёт система.
public enum Palette {
    // MARK: - Интерфейс: контентный слой, без стекла

    /// Фон списков (grouped).
    public static let uiBackground = Themed<RGBA>(day: 0xF2F2F7, night: 0x0A0E15)
    /// Ячейки и карточки.
    public static let uiCell = Themed<RGBA>(day: 0xFFFFFF, night: 0x141A24)
    /// Вложенные плашки.
    public static let uiCell2 = Themed<RGBA>(day: 0xF2F2F7, night: 0x1B2330)
    /// Основной текст.
    public static let uiInk = Themed<RGBA>(day: 0x1C1C1E, night: 0xF2F5FA)
    /// Вторичный текст (≥ 4,5:1).
    public static let uiInk2 = Themed(day: RGBA(0x3C3C43, alpha: 0.74), night: RGBA(0xE2E9F4, alpha: 0.62))
    /// Третичный текст, силуэты ненайденного.
    public static let uiInk3 = Themed(day: RGBA(0x3C3C43, alpha: 0.36), night: RGBA(0xE2E9F4, alpha: 0.34))
    /// Разделители.
    public static let uiSeparator = Themed(day: RGBA(0x3C3C43, alpha: 0.13), night: RGBA(0xAABEDC, alpha: 0.12))
    /// Дорожка сегмента и прогресса.
    public static let uiFill = Themed(day: RGBA(0x767680, alpha: 0.12), night: RGBA(0x788CAA, alpha: 0.16))
    /// Нейтральная главная кнопка вне карты («Поделиться», «Готово») — не цвет игрока.
    public static let uiButton = Themed<RGBA>(day: 0x1C1C1E, night: 0xF2F5FA)
    /// Текст на `uiButton`.
    public static let uiButtonInk = Themed<RGBA>(day: 0xFFFFFF, night: 0x0A0E15)
    /// Вторичный текст на карточке недели (фон — `FogStyle.foggedLand`).
    public static let cardInk2 = Themed(day: RGBA(0x28241E, alpha: 0.78), night: RGBA(0xF5F2EC, alpha: 0.80))

    // MARK: - Карта

    /// Земля стилизованной карты (`map-land`): к ней подобраны кромки игроков и альфы заливок. На iPhone подложка —
    /// Apple Maps `.muted`, её цвета задаёт Apple (PLAN.md, D4); остальные токены `map-*` — стиль запасного MapLibre,
    /// в коде их нет, пока спайк S4 его не выберет.
    public static let mapLand = Themed<RGBA>(day: 0xECEEEA, night: 0x111925)

    // MARK: - Земли и служебное

    /// Призрак потерянной земли (3 дня).
    public static let ghost = Themed<RGBA>(day: 0x7C838B, night: 0x98A2B1)
    /// «Ничейные земли».
    public static let neutral = Themed<RGBA>(day: 0x8E8E93, night: 0x7D8594)
    /// Пунктир «бегущих муравьёв» на спорной земле.
    public static let antsInk = Themed<RGBA>(day: 0x1C1C1E, night: 0xF2F5FA)
    /// Подложка под муравьями.
    public static let antsHalo = Themed<RGBA>(day: 0xFFFFFF, night: 0x0A0F17)
    /// Обводка следа бегущего.
    public static let trailCase = Themed<RGBA>(day: 0xFFFFFF, night: 0x0A0F17)
    /// Пунктир до точки замыкания.
    public static let gapInk = Themed<RGBA>(day: 0x1C1C1E, night: 0xF2F5FA)

    // MARK: - Семантика (не цвета кнопок)

    /// Предупреждение о точности GPS.
    public static let warn = Themed<RGBA>(day: 0xC27400, night: 0xFFD25E)
    /// Знак «!» на `warn`.
    public static let warnInk = Themed<RGBA>(day: 0xFFFFFF, night: 0x1C1C1E)
    /// «Горячо», значок тайника рядом.
    public static let hot = Themed<RGBA>(day: 0xE0482B, night: 0xFF7A59)
    /// Шкала «горячо»: деления 1 (холодно) … 4 (горячо); пятое, пустое, — трек.
    public static let hotScale: [Themed<RGBA>] = [
        Themed(day: 0x3F7CC0, night: 0x8FB3D9),
        Themed(day: 0xA8740C, night: 0xE9C07A),
        Themed(day: 0xC85C16, night: 0xF09A55),
        Themed(day: 0xD93A1E, night: 0xFF6A4D),
    ]
    /// Короны, эпик — только значки и глифы.
    public static let gold = Themed<RGBA>(day: 0xB8860B, night: 0xF2C85B)
    /// Золотой текст («НОВАЯ НАХОДКА»): днём темнее `gold`, чтобы держать 5:1.
    public static let goldInk = Themed<RGBA>(day: 0x946500, night: 0xF2C85B)
    /// Сияние находки.
    public static let goldSoft = Themed<RGBA>(0xF4C448)

    // MARK: - Постоянные (одинаковы в обеих темах)

    /// Плашка «первый» — градиент под 135°.
    public static let firstBackground: [GradientStop] = [
        GradientStop(0xE8B843, at: 0), GradientStop(0xFFF0B2, at: 0.5), GradientStop(0xD29E28, at: 1),
    ]
    /// Текст на плашке «первый».
    public static let firstInk: RGBA = 0x553A05
}

/// Точка градиента: цвет и место от 0 до 1.
public struct GradientStop: Hashable, Sendable {
    public var color: RGBA
    public var location: Double

    public init(_ color: RGBA, at location: Double) {
        self.color = color
        self.location = location
    }
}
