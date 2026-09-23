import ActivityKit
import Foundation

/// Запуск, обновление и завершение Live Activity забега.
///
/// Объект `Activity` из ActivityKit не `Sendable`, и Swift 6 не даёт передавать его между потоками.
/// Поэтому наружу отдаём только идентификатор (строку), а сам объект находим здесь, где с ним работаем.
public enum RunActivityController {
    /// Разрешены ли Live Activities для приложения (Настройки → Городки).
    public static var areActivitiesEnabled: Bool {
        ActivityAuthorizationInfo().areActivitiesEnabled
    }

    /// Идентификатор уже запущенной активности — например, если приложение перезапускали.
    public static var currentActivityID: String? {
        Activity<RunActivityAttributes>.activities.first?.id
    }

    /// Запускает Live Activity и возвращает её идентификатор.
    public static func start(startedAt: Date, state: RunActivityAttributes.ContentState) throws -> String {
        let activity = try Activity.request(
            attributes: RunActivityAttributes(startedAt: startedAt),
            content: ActivityContent(state: state, staleDate: nil)
        )
        return activity.id
    }

    public static func update(id: String, state: RunActivityAttributes.ContentState) async {
        for activity in Activity<RunActivityAttributes>.activities where activity.id == id {
            await activity.update(ActivityContent(state: state, staleDate: nil))
        }
    }

    public static func end(id: String) async {
        for activity in Activity<RunActivityAttributes>.activities where activity.id == id {
            await activity.end(nil, dismissalPolicy: .immediate)
        }
    }
}
