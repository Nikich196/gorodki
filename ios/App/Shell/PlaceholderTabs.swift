import DesignSystem
import SwiftUI

/// «Рейтинги» до этапа 7: что здесь будет (PLAN.md, §5, экран 20).
struct LeaderboardsTab: View {
    var body: some View {
        NavigationStack {
            TabPlaceholder(
                systemImage: "trophy",
                title: "Рейтинги — скоро",
                text: "Места игроков по открытому туману и по захваченной земле — за сезон и за всё время."
            )
            .navigationTitle("Рейтинги")
        }
    }
}

/// «Клан» — после серверной части кланов (#64).
struct ClanTab: View {
    var body: some View {
        NavigationStack {
            TabPlaceholder(
                systemImage: "person.3",
                title: "Кланы — скоро",
                text: "Создай клан или вступи по коду друга: земли клана держатся вместе."
            )
            .navigationTitle("Клан")
        }
    }
}

/// Заглушка экрана «скоро» — общий компонент состояний (`ContentStateView`, PLAN.md, §5, экран 27) на фоне контентного
/// слоя, без стекла.
struct TabPlaceholder: View {
    let systemImage: String
    let title: String
    let text: String

    var body: some View {
        ContentStateView(title, systemImage: systemImage, message: text)
            .background(Palette.uiBackground.color)
    }
}
