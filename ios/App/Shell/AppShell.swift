import DesignSystem
import SwiftUI

/// Вкладки игры (PLAN.md, §5; «Поиск» убран решением 25.09 — друзья по QR и ссылке, кланы по коду).
enum AppTab: String, CaseIterable, Hashable, Sendable {
    case map, leaderboards, clan, profile
}

/// Оболочка после входа: системный `TabView` (Liquid Glass рисует сама система, docs/design/tokens.md, §5) и плавающий
/// «Старт» на карте. Экраны, которых ещё нет, — заглушки в стиле дизайн-системы. Забег (PLAN.md, §5): «Старт» — лист
/// с лигой, HUD — полноэкранно (zoom из «Старта» или плашки), свёрнутый — плашка `tabViewBottomAccessory`.
struct AppShell: View {
    @Bindable var model: ShellModel
    @Environment(\.scenePhase) private var scenePhase
    @Namespace private var runTransition

    var body: some View {
        @Bindable var run = model.run
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
        .runAccessory(run)
        .sheet(isPresented: $run.startSheetShown, onDismiss: { run.startSheetDismissed() }) {
            RunStartSheet(model: run)
                .presentationDetents([.medium, .large])
                .navigationTransition(.zoom(sourceID: RunTransitionID.start, in: runTransition))
        }
        .fullScreenCover(isPresented: $run.coverShown) {
            RunCover(model: run)
                .navigationTransition(.zoom(sourceID: run.coverSource, in: runTransition))
        }
        .environment(\.runScreens, model.run)
        .environment(\.runTransition, runTransition)
        .onChange(of: scenePhase, initial: true) { _, phase in
            run.sceneChanged(active: phase == .active)
        }
        .task(id: ObjectIdentifier(model.run)) {
            model.run.connect?()
        }
    }
}

extension View {
    /// Плашка свёрнутого забега над таб-баром — только пока забег идёт, а HUD закрыт.
    /// С iOS 26.1 плашка включается флагом — вкладки не пересоздаются; на 26.0 модификатор ставится, только пока забег
    /// идёт (`tabViewBottomAccessory` без флага показал бы пустую плашку).
    @ViewBuilder
    fileprivate func runAccessory(_ run: RunScreenModel) -> some View {
        #if compiler(>=6.2.1)
            if #available(iOS 26.1, *) {
                tabViewBottomAccessory(isEnabled: run.isRunning && !run.hudPresented) {
                    RunAccessory(model: run)
                }
            } else {
                legacyRunAccessory(run)
            }
        #else
            legacyRunAccessory(run)
        #endif
    }

    @ViewBuilder
    private func legacyRunAccessory(_ run: RunScreenModel) -> some View {
        if run.isRunning {
            tabViewBottomAccessory {
                RunAccessory(model: run)
            }
        } else {
            self
        }
    }
}

/// Состояние оболочки: выбранная вкладка, профиль (из него же цвет «Старта») и экраны забега.
@MainActor
@Observable
final class ShellModel {
    var tab: AppTab
    let profile: ProfileModel
    let run: RunScreenModel

    /// - Parameter run: `nil` — экраны забега без трекера (режим фикстур, тесты).
    init(tab: AppTab = .map, profile: ProfileModel, run: RunScreenModel? = nil) {
        self.tab = tab
        self.profile = profile
        self.run = run ?? RunScreenModel(profile: profile)
    }

    /// Оболочка приложения: забег — `RunController`.
    static func live() -> ShellModel {
        let profile = ProfileModel()
        return ShellModel(profile: profile, run: .live(profile: profile))
    }
}
