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
                MapTab(player: model.profile.playerColor)
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

/// Состояние оболочки: выбранная вкладка и профиль (из него же цвет «Старта»).
@MainActor
@Observable
final class ShellModel {
    var tab: AppTab
    let profile: ProfileModel

    init(tab: AppTab = .map, profile: ProfileModel) {
        self.tab = tab
        self.profile = profile
    }
}
