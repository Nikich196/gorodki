import Foundation
import GameCore

/// Число с существительным по-русски (PLAN.md, §6.10 «Русские множественные формы»): «1 петля», «2 петли», «5 петель»,
/// «11 петель», «21 петля».
///
/// Формы — в каталоге строк (`Resources/Localizable.xcstrings`, варианты one/few/many/other; полноту проверяет
/// ios/scripts/check-localization.py), здесь — только ключи. Правило выбора формы — русское при любом языке телефона
/// (`NumberText.locale`): игра говорит только по-русски, а по английскому правилу вышло бы «2 петель». Та же локаль
/// пишет и само число, как NumberText: «5 000 участков», а не «5,000».
enum CountText {
    static func loops(_ count: Int) -> String {
        String(localized: "\(count) петель", locale: NumberText.locale)
    }

    static func runs(_ count: Int) -> String {
        String(localized: "\(count) забегов", locale: NumberText.locale)
    }

    static func points(_ count: Int) -> String {
        String(localized: "\(count) точек", locale: NumberText.locale)
    }

    static func fogCells(_ count: Int) -> String {
        String(localized: "\(count) клеток", locale: NumberText.locale)
    }

    static func parcels(_ count: Int) -> String {
        String(localized: "\(count) участков", locale: NumberText.locale)
    }

    static func pieces(_ count: Int) -> String {
        String(localized: "\(count) кусков", locale: NumberText.locale)
    }

    static func groups(_ count: Int) -> String {
        String(localized: "\(count) групп", locale: NumberText.locale)
    }

    static func tiles(_ count: Int) -> String {
        String(localized: "\(count) тайлов", locale: NumberText.locale)
    }

    /// Сотка = 100 м² (PLAN.md, §6.10): «12 480 м² = 125 соток».
    static func sotki(_ count: Int) -> String {
        String(localized: "\(count) соток", locale: NumberText.locale)
    }

    /// Очки сезона: «2 340 очков» — вместо «SP» (docs/design/tokens.md, §4).
    static func score(_ count: Int) -> String {
        String(localized: "\(count) очков", locale: NumberText.locale)
    }

    /// Состав клана: «3 участника».
    static func members(_ count: Int) -> String {
        String(localized: "\(count) участников", locale: NumberText.locale)
    }

    /// Серия и срок дуэли: «1 день», «3 дня», «5 дней».
    static func days(_ count: Int) -> String {
        String(localized: "\(count) дней", locale: NumberText.locale)
    }

    /// Местная легенда отрезка: «14 проходов».
    static func efforts(_ count: Int) -> String {
        String(localized: "\(count) проходов", locale: NumberText.locale)
    }

    /// Рюкзак: «2 фишки».
    static func items(_ count: Int) -> String {
        String(localized: "\(count) фишек", locale: NumberText.locale)
    }

    /// Респекты поста в ленте: «1 респект», «3 респекта».
    static func respects(_ count: Int) -> String {
        String(localized: "\(count) респектов", locale: NumberText.locale)
    }
}
