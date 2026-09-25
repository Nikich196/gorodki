import DesignSystem
import SwiftUI

/// Вкладки игры (PLAN.md, §5; «Поиск» убран решением 25.09 — друзья по QR и ссылке, кланы по коду).
enum AppTab: String, CaseIterable, Hashable, Sendable {
    case map, leaderboards, clan, profile
}

/// Оболочка после входа: системный `TabView` (Liquid Glass рисует сама система, docs/design/tokens.md, §5) и плавающий
/// «Старт» на карте. Экраны, которых ещё нет, — заглушки в стиле дизайн-системы.
struct AppShell: View {
    @Bindable var model: ShellModel

    var body: some View {
        TabView(selection: $model.tab) {
            Tab("Карта", systemImage: "map", value: AppTab.map) {
                MapScreen(model: model.map)
            }
            Tab("Рейтинги", systemImage: "trophy", value: AppTab.leaderboards) {
                LeaderboardsTab()
            }
            Tab("Клан", systemImage: "person.3", value: AppTab.clan) {
                ClanTab()
            }
            Tab("Профиль", systemImage: "person.crop.circle", value: AppTab.profile) {
                ProfileTab(model: model.profile)
            }
        }
        .tint(Palette.uiInk.color)  // активная вкладка — нейтральная (tokens.md, §6, п. 3)
    }
}

/// Состояние оболочки: выбранная вкладка, профиль (из него же цвет «Старта» и свой номер для карты) и карта.
@MainActor
@Observable
final class ShellModel {
    var tab: AppTab
    let profile: ProfileModel
    let map: MapModel

    /// - Parameter map: `nil` — карта без данных (экраны без сервера).
    init(tab: AppTab = .map, profile: ProfileModel, map: MapModel? = nil) {
        self.tab = tab
        self.profile = profile
        self.map = map ?? MapModel(profile: profile)
    }

    /// Оболочка приложения: карта берёт землю и туман из кэшей `AppDependencies`.
    static func live() -> ShellModel {
        let profile = ProfileModel()
        return ShellModel(profile: profile, map: .live(profile: profile))
    }
}
