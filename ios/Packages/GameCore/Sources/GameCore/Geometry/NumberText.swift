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

    /// «140 м» — целые метры.
    public static func meters(_ meters: Int) -> String {
        integer(meters) + unitSeparator + "м"
    }

    /// Время как на часах забега: «17:42», с часа — «1:02:03» (PLAN.md, §6.10). Секунды — вниз до целой: таймер
    /// не забегает вперёд. Отрицательное — ноль.
    public static func clock(seconds: Double) -> String {
        let total = Int(max(0, seconds.isFinite ? seconds : 0).rounded(.down))
        let (hours, minutes, rest) = (total / 3_600, total % 3_600 / 60, total % 60)
        let tail = twoDigits(rest)
        return hours > 0 ? "\(hours):\(twoDigits(minutes)):\(tail)" : "\(minutes):\(tail)"
    }

    /// Темп без единицы: «5:32» — минуты и секунды на километр, до ближайшей секунды (§6.10: «5:32 /км»).
    public static func pace(secondsPerKilometer: Double) -> String {
        let total = Int(max(0, secondsPerKilometer.isFinite ? secondsPerKilometer : 0).rounded())
        return "\(total / 60):\(twoDigits(total % 60))"
    }

    /// Единица темпа — «/км» (через неразрывный пробел после числа: «5:32 /км»).
    public static let paceUnit = "/км"

    private static func twoDigits(_ value: Int) -> String {
        value < 10 ? "0\(value)" : "\(value)"
    }

    /// Размер файлов, как в «Хранилище iPhone» — десятичными единицами (1 КБ = 1 000 байт): «812 Б», «4,2 МБ»,
    /// «37 МБ», «1,3 ГБ». До 10 — один знак после запятой, дальше — целые.
    public static func bytes(_ count: Int64) -> String {
        let units = ["КБ", "МБ", "ГБ", "ТБ"]
        guard count >= 1_000 else { return integer(Int(max(count, 0))) + unitSeparator + "Б" }
        var value = Double(count) / 1_000
        var unit = 0
        // Округлённое до показа значение не должно выйти за 1 000 («1 000 КБ» — это уже «1,0 МБ»).
        while unit < units.count - 1 && value.rounded() >= 1_000 {
            value /= 1_000
            unit += 1
        }
        return decimal(value, fractionDigits: value < 9.95 ? 1 : 0) + unitSeparator + units[unit]
    }
}
