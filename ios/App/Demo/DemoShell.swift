import DesignSystem
import SwiftUI

/// Вкладки демо (`DemoMode`) и плашка «Демо» на каждой: видно, что данные — образцы, и есть выход к онбордингу.
struct DemoShell: View {
    let model: ShellModel

    var body: some View {
        AppShell(model: model)
            .environment(\.demoBadgeShown, true)
    }
}

extension EnvironmentValues {
    /// Показывать ли плашку «Демо» (`DemoShell`) — `demoBadgeInset()` экранов вкладок.
    @Entry var demoBadgeShown = false
}

extension View {
    /// Плашка «Демо» сверху экрана вкладки — под панелью навигации, своей полосой, а не поверх содержимого. Вешается на
    /// корень каждой вкладки (внутри её `NavigationStack`), а не на всю оболочку: в iOS 26 панели навигации верхняя
    /// вставка `TabView` не сдвигает, и плашка закрывала заголовки и переключатели («Захват | Исследование | Короли»).
    func demoBadgeInset() -> some View {
        modifier(DemoBadgeInset())
    }
}

private struct DemoBadgeInset: ViewModifier {
    @Environment(\.demoBadgeShown) private var shown

    func body(content: Content) -> some View {
        content.safeAreaInset(edge: .top, spacing: 0) {
            if shown {
                DemoBadge()
                    .padding(.top, 4)
                    .padding(.bottom, 8)
                    .frame(maxWidth: .infinity)
            }
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
