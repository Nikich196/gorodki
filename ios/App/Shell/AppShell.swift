import DesignSystem
import SwiftUI

/// Вкладки игры (PLAN.md, §5; «Поиск» убран решением 25.09 — друзья по QR и ссылке, кланы по коду).
enum AppTab: String, CaseIterable, Hashable, Sendable {
    case map, leaderboards, clan, profile
}

/// Оболочка после входа: системный `TabView` (Liquid Glass рисует сама система, docs/design/tokens.md, §5) и плавающий
/// «Старт» на карте. Экраны, которых ещё нет, — заглушки в стиле дизайн-системы. Забег (PLAN.md, §5): «Старт» — лист
/// с лигой, HUD — полноэкранно (zoom из «Старта» или плашки), свёрнутый — плашка `tabViewBottomAccessory`.
/// «Рейтинги» и «Клан | Друзья | Лента» — `App/Social`; «Входящие» — колокольчик на «Карте» (лист) и строка в «Профиле».
struct AppShell: View {
    @Bindable var model: ShellModel
    @Environment(\.scenePhase) private var scenePhase
    @Namespace private var runTransition

    var body: some View {
        @Bindable var run = model.run
        @Bindable var social = model.social
        TabView(selection: $model.tab) {
            Tab("Карта", systemImage: "map", value: AppTab.map) {
                MapScreen(model: model.map)
            }
            Tab("Рейтинги", systemImage: "trophy", value: AppTab.leaderboards) {
                LeaderboardsTab(model: model.social.leaderboards, myColor: model.profile.playerColor)
            }
            Tab("Клан", systemImage: "person.3", value: AppTab.clan) {
                ClanTab(social: model.social)
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
        .sheet(isPresented: $social.inboxShown) {
            InboxSheet(model: model.social.inbox)
                .navigationTransition(.zoom(sourceID: SocialTransitionID.inbox, in: runTransition))
        }
        .environment(\.runScreens, model.run)
        .environment(model.social)
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

/// Состояние оболочки: выбранная вкладка, профиль (из него же цвет «Старта» и свой номер для карты), карта
/// и экраны забега.
@MainActor
@Observable
final class ShellModel {
    var tab: AppTab
    let profile: ProfileModel
    let run: RunScreenModel
    let map: MapModel
    let social: SocialScreens

    /// - Parameters:
    ///   - run: `nil` — экраны забега без трекера (режим фикстур, тесты).
    ///   - map: `nil` — карта без данных (экраны без сервера).
    ///   - social: `nil` — рейтинги, клан, лента и «Входящие» без сервера.
    init(
        tab: AppTab = .map, profile: ProfileModel, run: RunScreenModel? = nil, map: MapModel? = nil,
        social: SocialScreens? = nil
    ) {
        self.tab = tab
        self.profile = profile
        self.run = run ?? RunScreenModel(profile: profile)
        self.map = map ?? MapModel(profile: profile)
        self.social = social ?? SocialScreens(backend: nil)
    }

    /// Оболочка приложения: забег — `RunController`, земля и туман карты — кэши `AppDependencies`, социальные
    /// экраны — клиент API.
    static func live() -> ShellModel {
        let profile = ProfileModel()
        return ShellModel(
            profile: profile, run: .live(profile: profile), map: .live(profile: profile),
            social: SocialScreens(backend: AppDependencies.shared.api.map { APISocialSource(api: $0) }))
    }
}
