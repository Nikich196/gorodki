import DesignSystem
import GameCore
import GorodkiAPI
import SwiftUI
import UIKit

// Общее для экранов пунктов листика из «Профиля» (PLAN.md, §4): список на токенах, сезоны с сервера, системные
// Настройки. Модели экранов — `@Observable` с простыми значениями, всё платформенное — за протоколами: режим фикстур
// (`-GorodkiScreen`) подставляет свои значения без системных запросов (docs/architecture/ios-app.md, «Пункты листика»).

/// Список на токенах контентного слоя: фон `Palette.uiBackground`, строки `Palette.uiCell` — как «Профиль», без стекла
/// (docs/design/tokens.md, §6).
struct TokenList<Content: View>: View {
    @ViewBuilder var content: Content

    var body: some View {
        List {
            content.listRowBackground(Palette.uiCell.color)
        }
        .scrollContentBackground(.hidden)
        .background(Palette.uiBackground.color)
    }
}

/// Карточка-пояснение в начале экрана: значок, заголовок и текст — зачем экран и что он делает.
struct SheetIntro: View {
    let systemImage: String
    let title: String
    let text: String

    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: systemImage)
                .font(.title2.weight(.semibold))
                .foregroundStyle(Palette.uiInk.color)
                .frame(width: 44, height: 44)
                .background(Palette.uiCell2.color, in: .rect(cornerRadius: 12))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 4) {
                Text(title)
                    .font(.headline)
                    .foregroundStyle(Palette.uiInk.color)
                Text(text)
                    .font(.subheadline)
                    .foregroundStyle(Palette.uiInk2.color)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(.vertical, 4)
    }
}

/// Строка «название — значение» с моноширинными цифрами: значение перекатывается при смене (`numericText`).
struct ValueRow: View {
    let title: String
    let value: String
    var systemImage: String?

    var body: some View {
        HStack(spacing: 12) {
            if let systemImage {
                Image(systemName: systemImage)
                    .foregroundStyle(Palette.uiInk2.color)
                    .frame(width: 24)
                    .accessibilityHidden(true)
            }
            Text(title)
                .foregroundStyle(Palette.uiInk.color)
            Spacer(minLength: 8)
            Text(value)
                .monospacedDigit()
                .foregroundStyle(Palette.uiInk2.color)
                .multilineTextAlignment(.trailing)
                .contentTransition(.numericText())
        }
        .accessibilityElement(children: .combine)
    }
}

/// Сезоны с сервера (`GET /seasons`) простыми значениями — «Календарь» и напоминания о конце сезона.
enum SeasonsSource {
    static func seasons(from response: Components.Schemas.SeasonsResponse) -> [SeasonInfo] {
        response.seasons.map {
            SeasonInfo(number: Int($0.number), name: $0.name, startsAtMs: $0.startsAtMs, endsAtMs: $0.endsAtMs)
        }
    }

    /// С сервера; адреса нет или нет связи — пусто (экран скажет, что сезонов пока не видно).
    static func live() async -> [SeasonInfo] {
        guard let api = AppDependencies.shared.api, let response = try? await api.getSeasons().ok.body.json else {
            return []
        }
        return seasons(from: response)
    }
}

/// Системные Настройки приложения — туда ведут все «разрешить можно только в Настройках».
enum SystemSettings {
    static var appURL: URL? { URL(string: UIApplication.openSettingsURLString) }
    static var notificationsURL: URL? { URL(string: UIApplication.openNotificationSettingsURLString) }
}

/// Верхний контроллер окна — для системных окон UIKit без SwiftUI-обёртки (выбор фото при ограниченном доступе).
@MainActor
enum TopViewController {
    static var current: UIViewController? {
        let scenes = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
        var top = scenes.flatMap(\.windows).first(where: \.isKeyWindow)?.rootViewController
        while let presented = top?.presentedViewController {
            top = presented
        }
        return top
    }
}

extension Date {
    /// Секунды Unix → дата.
    init(unix seconds: Double) {
        self.init(timeIntervalSince1970: seconds)
    }
}
