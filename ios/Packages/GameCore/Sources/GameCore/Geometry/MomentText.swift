import Foundation

/// Моменты в текстах для игрока — по-русски, как в листе участка: «сегодня в 14:30», «вчера в 09:05», «до завтра,
/// 02:30», «до 28 сентября, 14:00». Дни считаются по календарю часового пояса телефона (в тестах — явно), время — 24 ч.
public enum MomentText {
    /// Прошедший момент: «сегодня в 14:30», «вчера в 09:05», «23 сентября в 18:40»; другого года — «23 сентября
    /// 2025 в 18:40».
    public static func past(_ ms: Int64, now: Int64, timeZone: TimeZone = .current) -> String {
        let (day, time) = parts(ms, now: now, timeZone: timeZone)
        return day + " в " + time
    }

    /// Срок: «до 22:00» (сегодня), «до завтра, 02:30», «до 28 сентября, 14:00».
    public static func until(_ ms: Int64, now: Int64, timeZone: TimeZone = .current) -> String {
        let (day, time) = parts(ms, now: now, timeZone: timeZone)
        return day == today ? "до " + time : "до " + day + ", " + time
    }

    private static let today = "сегодня"

    private static func parts(_ ms: Int64, now: Int64, timeZone: TimeZone) -> (day: String, time: String) {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        calendar.locale = NumberText.locale
        let date = Date(timeIntervalSince1970: Double(ms) / 1_000)
        let reference = Date(timeIntervalSince1970: Double(now) / 1_000)
        let days =
            calendar.dateComponents(
                [.day], from: calendar.startOfDay(for: reference), to: calendar.startOfDay(for: date)
            ).day ?? 0
        let day: String
        switch days {
        case 0: day = today
        case -1: day = "вчера"
        case 1: day = "завтра"
        default:
            let sameYear = calendar.component(.year, from: date) == calendar.component(.year, from: reference)
            day = format(date, sameYear ? "d MMMM" : "d MMMM y", timeZone: timeZone)
        }
        return (day, format(date, "HH:mm", timeZone: timeZone))
    }

    /// Формат по шаблону ICU с русской локалью: «d MMMM» даёт родительный падеж — «23 сентября».
    private static func format(_ date: Date, _ template: String, timeZone: TimeZone) -> String {
        let formatter = DateFormatter()
        formatter.locale = NumberText.locale
        formatter.timeZone = timeZone
        formatter.dateFormat = template
        return formatter.string(from: date)
    }
}
