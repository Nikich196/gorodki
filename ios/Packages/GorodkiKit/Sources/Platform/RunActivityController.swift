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

    /// Идентификаторы идущих активностей — например, после перезапуска приложения. Их может быть несколько (забег,
    /// прогулка «Лаборатории», проверка установки), а тип атрибутов у всех один, поэтому «первой попавшейся» нет:
    /// свою узнают по идентификатору, сохранённому при запуске. Закрытые системой или игроком сюда не входят.
    public static var runningIDs: [String] {
        Activity<RunActivityAttributes>.activities
            .filter { $0.activityState == .active || $0.activityState == .stale }
            .map { $0.id }
    }

    /// Запускает Live Activity и возвращает её идентификатор.
    public static func start(startedAt: Date, state: RunActivityAttributes.ContentState) throws -> String {
        let activity = try Activity.request(
            attributes: RunActivityAttributes(startedAt: startedAt),
            content: ActivityContent(state: state, staleDate: nil)
        )
        return activity.id
    }

    /// - Parameter alert: оповещение (заголовок, текст) — экран загорается, телефон вибрирует: так Live Activity
    ///   говорит о событии в кармане (PLAN.md, §6.9: «Захват — Live Activity `AlertConfiguration` + голос»).
    public static func update(
        id: String, state: RunActivityAttributes.ContentState, alert: (title: String, body: String)? = nil
    ) async {
        let configuration = alert.map {
            AlertConfiguration(
                title: LocalizedStringResource(stringLiteral: $0.title),
                body: LocalizedStringResource(stringLiteral: $0.body), sound: .default)
        }
        for activity in Activity<RunActivityAttributes>.activities where activity.id == id {
            await activity.update(ActivityContent(state: state, staleDate: nil), alertConfiguration: configuration)
        }
    }

    public static func end(id: String) async {
        for activity in Activity<RunActivityAttributes>.activities where activity.id == id {
            await activity.end(nil, dismissalPolicy: .immediate)
        }
    }
}
