import Foundation

/// Сезон, как его отдаёт `GET /seasons` (docs/architecture: `SeasonsResponse`), — простыми значениями, чтобы
/// напоминания считались без клиента API.
public struct SeasonInfo: Hashable, Sendable {
    public var number: Int
    public var name: String
    /// Начало и конец, мс Unix. Конец `nil` — сезон без даты окончания (ещё не назначена).
    public var startsAtMs: Int64
    public var endsAtMs: Int64?

    public init(number: Int, name: String, startsAtMs: Int64, endsAtMs: Int64?) {
        self.number = number
        self.name = name
        self.startsAtMs = startsAtMs
        self.endsAtMs = endsAtMs
    }

    /// Конец, секунды Unix.
    public var endsAt: Double? { endsAtMs.map { Double($0) / 1_000 } }
}

/// Событие для Календаря (EventKit, только запись — пункт 7 листика).
public struct CalendarEventDraft: Equatable, Sendable {
    public var title: String
    /// Начало и конец, секунды Unix.
    public var start: Double
    public var end: Double
    public var notes: String
    /// Напомнить за столько секунд до начала.
    public var alarmBefore: Double
}

/// Локальное уведомление (пункт 4 листика без платного аккаунта: APNs нет — напоминает сам телефон).
public struct ReminderDraft: Equatable, Sendable {
    /// Идентификатор: новое с тем же заменяет прежнее.
    public var id: String
    public var title: String
    public var body: String
    /// Когда показать, секунды Unix.
    public var fireAt: Double
}

/// Напоминания игры: конец сезона в Календаре и уведомлением накануне, «забег всё ещё идёт» (PLAN.md, §3.17).
public enum Reminders {
    /// Уведомления — не позже этого часа (дневная сводка ~19:00; тишина 22:00–08:00, PLAN.md, §3.17).
    public static let eveningHour = 19
    /// «Забег всё ещё идёт» — через столько секунд после ухода приложения в фон с идущим забегом.
    public static let runStillGoingDelay: Double = 2 * 3_600
    public static let runStillGoingID = "run.still-going"

    /// Сезон, конец которого впереди: текущий или ближайший следующий; `nil` — у всех конец прошёл или не назначен.
    public static func upcomingEnd(in seasons: [SeasonInfo], now: Double) -> SeasonInfo? {
        seasons.filter { ($0.endsAt ?? 0) > now }.min { ($0.endsAt ?? 0) < ($1.endsAt ?? 0) }
    }

    /// «Конец сезона» в Календаре: последний час сезона, напоминание за сутки. Без даты конца — `nil`.
    public static func calendarEvent(for season: SeasonInfo) -> CalendarEventDraft? {
        guard let end = season.endsAt else { return nil }
        return CalendarEventDraft(
            title: "Городки: конец сезона «\(season.name)»",
            start: max(end - 3_600, Double(season.startsAtMs) / 1_000), end: end,
            // Мягкий сброс — PLAN.md, §3.4.
            notes: "Последний час сезона. Потом — мягкий сброс: уровни участков вернутся к первому, очки обнулятся, "
                + "щиты снимутся, а лучшие попадут в Зал славы.",
            alarmBefore: 24 * 3_600)
    }

    /// «Сезон заканчивается завтра»: накануне последнего дня сезона в 19:00 по местному времени. Сезон кончается
    /// в полночь — последний день тот, что перед ней. Время уже прошло или конца нет — `nil`.
    public static func seasonEndingTomorrow(_ season: SeasonInfo, now: Double, timeZone: TimeZone) -> ReminderDraft? {
        guard let end = season.endsAt else { return nil }
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        let lastDay = calendar.startOfDay(for: Date(timeIntervalSince1970: end - 1))
        guard let dayBefore = calendar.date(byAdding: .day, value: -1, to: lastDay),
            let fire = calendar.date(bySettingHour: eveningHour, minute: 0, second: 0, of: dayBefore),
            fire.timeIntervalSince1970 > now
        else { return nil }
        return ReminderDraft(
            id: "season.\(season.number).ending",
            title: "Сезон заканчивается завтра",
            body: "Завтра — последний день сезона «\(season.name)»: успей поднять участки и очки до сброса.",
            fireAt: fire.timeIntervalSince1970)
    }

    /// «Забег всё ещё идёт»: игрок ушёл из приложения с идущим забегом — не забыть «Финиш».
    public static func runStillGoing(now: Double) -> ReminderDraft {
        ReminderDraft(
            id: runStillGoingID,
            title: "Забег всё ещё идёт",
            body: "Забег окончен? Открой «Городки» и нажми «Финиш» — иначе в забег попадёт и дорога домой.",
            fireAt: now + runStillGoingDelay)
    }
}
