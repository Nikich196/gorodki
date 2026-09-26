import DesignSystem
import SwiftUI

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
