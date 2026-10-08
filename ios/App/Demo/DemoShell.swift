import DesignSystem
import SwiftUI

/// Вкладки демо (`DemoMode`) и плашка «Демо» над ними: видно, что данные — образцы, и есть выход к онбордингу.
/// Плашка — своей полосой (`safeAreaInset`), а не поверх: иначе закрыла бы переключатели карты и заголовки вкладок.
struct DemoShell: View {
    let model: ShellModel

    var body: some View {
        AppShell(model: model)
            .safeAreaInset(edge: .top, spacing: 0) {
                DemoBadge()
                    .padding(.vertical, 4)
                    .frame(maxWidth: .infinity)
                    .background(Palette.uiBackground.color)
            }
    }
}

private struct DemoBadge: View {
    var body: some View {
        HStack(spacing: 10) {
            Label("Демо: данные-примеры", systemImage: "sparkles")
                .font(.footnote.weight(.semibold))
                .foregroundStyle(Palette.uiInk.color)
            Button("Выйти") {
                AppSession.shared.demo = false
            }
            .font(.footnote.weight(.semibold))
            .accessibilityIdentifier("demo.exit")
        }
        .padding(.horizontal, 14)
        .frame(minHeight: 36)
        .capsuleGlass(interactive: true)
        .accessibilityElement(children: .contain)
    }
}
