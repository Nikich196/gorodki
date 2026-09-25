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

/// Заглушка экрана: значок, заголовок и строка — на фоне контентного слоя, без стекла.
struct TabPlaceholder: View {
    let systemImage: String
    let title: String
    let text: String

    var body: some View {
        VStack(spacing: 14) {
            Image(systemName: systemImage)
                .font(.system(size: 44, weight: .semibold))
                .foregroundStyle(Palette.uiInk3.color)
                .frame(width: 96, height: 96)
                .background(Palette.uiCell.color, in: .rect(cornerRadius: Radius.tile))
                .accessibilityHidden(true)
            Text(title)
                .font(.title2.bold())
                .fontDesign(.rounded)
                .foregroundStyle(Palette.uiInk.color)
            Text(text)
                .font(.body)
                .foregroundStyle(Palette.uiInk2.color)
                .multilineTextAlignment(.center)
        }
        .padding(32)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Palette.uiBackground.color)
    }
}
