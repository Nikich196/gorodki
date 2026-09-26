import Foundation
import GameCore
import UserNotifications

/// Разрешение на уведомления, как его видит экран.
enum NotificationAccess: Equatable, Sendable {
    case notDetermined, denied, allowed
}

/// Локальные уведомления (пункт 4 листика без платного аккаунта: APNs нет — напоминания ставит сам телефон).
/// За протоколом: режим фикстур показывает экран без системного запроса, тесты проверяют модель без iOS.
@MainActor
protocol LocalNotifying: AnyObject {
    func access() async -> NotificationAccess
    /// Системный запрос — только по кнопке игрока (PLAN.md, §6.6: разрешения — в момент надобности).
    func requestAccess() async -> NotificationAccess
    func schedule(_ reminder: ReminderDraft) async throws
    func cancel(_ id: String)
}

/// `UNUserNotificationCenter`.
@MainActor
final class SystemNotifications: LocalNotifying {
    static let shared = SystemNotifications()

    private var center: UNUserNotificationCenter { .current() }

    func access() async -> NotificationAccess {
        switch await center.notificationSettings().authorizationStatus {
        case .notDetermined: .notDetermined
        case .denied: .denied
        default: .allowed
        }
    }

    func requestAccess() async -> NotificationAccess {
        _ = try? await center.requestAuthorization(options: [.alert, .sound, .badge])
        return await access()
    }

    func schedule(_ reminder: ReminderDraft) async throws {
        let content = UNMutableNotificationContent()
        content.title = reminder.title
        content.body = reminder.body
        content.sound = .default
        let delay = max(reminder.fireAt - Date.now.timeIntervalSince1970, 1)
        let trigger = UNTimeIntervalNotificationTrigger(timeInterval: delay, repeats: false)
        try await center.add(UNNotificationRequest(identifier: reminder.id, content: content, trigger: trigger))
    }

    func cancel(_ id: String) {
        center.removePendingNotificationRequests(withIdentifiers: [id])
    }
}

/// Уведомления видны и в открытом приложении: пробное уведомление («через 5 секунд») иначе молча пропало бы.
final class NotificationPresenter: NSObject, UNUserNotificationCenterDelegate, Sendable {
    static let shared = NotificationPresenter()

    func userNotificationCenter(
        _ center: UNUserNotificationCenter, willPresent notification: UNNotification
    ) async -> UNNotificationPresentationOptions {
        [.banner, .list, .sound]
    }
}

/// Какие напоминания включил игрок — в UserDefaults, переживают перезапуск.
struct ReminderSettings {
    static let seasonEndingKey = "reminders.seasonEnding"
    static let runStillGoingKey = "reminders.runStillGoing"

    let defaults: UserDefaults

    static var standard: ReminderSettings { ReminderSettings(defaults: .standard) }

    var seasonEnding: Bool {
        get { defaults.bool(forKey: Self.seasonEndingKey) }
        nonmutating set { defaults.set(newValue, forKey: Self.seasonEndingKey) }
    }

    var runStillGoing: Bool {
        get { defaults.bool(forKey: Self.runStillGoingKey) }
        nonmutating set { defaults.set(newValue, forKey: Self.runStillGoingKey) }
    }
}

/// «Забег всё ещё идёт»: приложение ушло в фон с идущим забегом — напомнить через два часа; вернулось или забег
/// закончился — снять (`GorodkiApp`, `AppDependencies.archiveFinishedRuns`).
@MainActor
enum RunReminder {
    static var notifications: any LocalNotifying = SystemNotifications.shared

    static func appWentToBackground(runIsGoing: Bool, settings: ReminderSettings = .standard) {
        guard runIsGoing, settings.runStillGoing else { return }
        let reminder = Reminders.runStillGoing(now: Date.now.timeIntervalSince1970)
        Task { try? await notifications.schedule(reminder) }
    }

    static func cancel() {
        notifications.cancel(Reminders.runStillGoingID)
    }
}
