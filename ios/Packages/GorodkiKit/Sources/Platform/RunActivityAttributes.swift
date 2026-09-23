import ActivityKit
import Foundation

/// Данные Live Activity забега (экран блокировки и Dynamic Island).
///
/// На этапе 0 это проверочная активность для спайка S2: она показывает, что расширение
/// запускается после установки через Sideloadly. На этапе 3b сюда придут дистанция,
/// «до замыкания» и подсказки. Обновления идут из приложения, без push-уведомлений.
public struct RunActivityAttributes: ActivityAttributes, Sendable {
    public struct ContentState: Codable, Hashable, Sendable {
        public var title: String
        public var detail: String

        public init(title: String, detail: String) {
            self.title = title
            self.detail = detail
        }
    }

    /// Время старта: от него Live Activity сама считает прошедшее время.
    public var startedAt: Date

    public init(startedAt: Date) {
        self.startedAt = startedAt
    }
}
