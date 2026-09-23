import DesignSystem
import SwiftUI

/// Стартовый экран этапа 0: название, палитра игроков и «Проверка установки» (спайк S2).
/// На этапе 2 его место займёт карта.
struct RootView: View {
    var body: some View {
        NavigationStack {
            List {
                Section {
                    HeroHeader()
                        .listRowInsets(EdgeInsets(top: 20, leading: 20, bottom: 20, trailing: 20))
                }
                InstallCheckSections()
            }
        }
    }
}

private struct HeroHeader: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Городки")
                .font(.system(.largeTitle, design: .rounded, weight: .black))
            Text("Обеги участок — и он твой, ровно по контуру следа.")
                .font(.body)
                .foregroundStyle(.secondary)
            PaletteStrip()
                .frame(height: 10)
                .accessibilityHidden(true)
        }
    }
}

/// Полоса из двенадцати цветов игроков.
private struct PaletteStrip: View {
    var body: some View {
        HStack(spacing: 3) {
            ForEach(PlayerColor.allCases) { player in
                Capsule().fill(player.color)
            }
        }
    }
}

#Preview {
    RootView()
}
