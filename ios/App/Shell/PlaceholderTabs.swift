import DesignSystem
import SwiftUI

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
