import DesignSystem
import Networking
import SwiftUI

// Пустые, офлайн и ошибочные состояния (PLAN.md, §5, экран 27): один компонент дизайн-системы (`ContentStateView`),
// тексты — `RequestFailure` (пакет Networking, тесты на Linux), значки — здесь.

extension RequestFailure {
    /// Значок состояния.
    var symbolName: String {
        switch self {
        case .notConfigured: "antenna.radiowaves.left.and.right.slash"
        case .offline: "wifi.slash"
        case .serverUnavailable: "icloud.slash"
        case .serverError: "exclamationmark.icloud"
        case .signedOut: "person.crop.circle.badge.xmark"
        case .notFound: "person.crop.circle.badge.questionmark"
        case .rejected: "hand.raised"
        case .unexpected: "exclamationmark.triangle"
        }
    }
}

extension ContentStateView {
    /// Состояние по ошибке запроса: «Повторить» — только если повтор может помочь.
    init(_ failure: RequestFailure, style: Style = .page, attempt: Int = 0, retry: (() -> Void)? = nil) {
        self.init(
            failure.title, systemImage: failure.symbolName, message: failure.message, style: style, attempt: attempt,
            retry: failure.isRetryable ? retry : nil)
    }
}

/// Этап загрузки экрана: загружается, загружено, не удалось.
enum LoadPhase: Equatable {
    case loading
    case loaded
    case failed(RequestFailure)
}
