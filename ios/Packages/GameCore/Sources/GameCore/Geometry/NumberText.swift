import Foundation

/// Числа в текстах для игрока — по-русски (PLAN.md, §6.10: «1,2 га», «3,2 км», «12 480 м²»): десятичная запятая
/// и неразрывный пробел между разрядами и перед единицей.
///
/// Локаль — русская при любом языке и регионе телефона: игра говорит только по-русски, и «1.23 км» среди русских слов
/// читается как ошибка. `String(format: "%.2f")` для этого не годится — он всегда ставит точку
/// (ios/scripts/check-localization.py не пускает его в приложение).
///
/// Какие единицы показывать (м², сотки или гектары) и сколько знаков после запятой — решают экраны, здесь только запись.
public enum NumberText {
    public static let locale = Locale(identifier: "ru")
    /// Неразрывный пробел: число не отрывается от единицы при переносе строки.
    public static let unitSeparator = "\u{00A0}"

    /// «1,23» — ровно `fractionDigits` знаков после запятой, разряды через неразрывный пробел: «1 234,50».
    public static func decimal(_ value: Double, fractionDigits: Int) -> String {
        value.formatted(.number.precision(.fractionLength(fractionDigits)).locale(locale))
    }

    /// «12 480» — разряды через неразрывный пробел.
    public static func integer(_ value: Int) -> String {
        value.formatted(.number.locale(locale))
    }

    /// «1,23 км».
    public static func kilometers(fromMeters meters: Double, fractionDigits: Int) -> String {
        decimal(meters / 1_000, fractionDigits: fractionDigits) + unitSeparator + "км"
    }

    /// «0,12 га».
    public static func hectares(fromSquareMeters squareMeters: Double, fractionDigits: Int) -> String {
        decimal(AreaUnits.hectares(fromSquareMeters: squareMeters), fractionDigits: fractionDigits) + unitSeparator
            + "га"
    }

    /// «12 480 м²» — до целого квадратного метра.
    public static func squareMeters(_ squareMeters: Double) -> String {
        decimal(squareMeters, fractionDigits: 0) + unitSeparator + "м²"
    }

    /// «3,1 с».
    public static func seconds(_ seconds: Double, fractionDigits: Int) -> String {
        decimal(seconds, fractionDigits: fractionDigits) + unitSeparator + "с"
    }
}
