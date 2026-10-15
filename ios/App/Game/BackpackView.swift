import DesignSystem
import GameCore
import GorodkiAPI
import SwiftUI

/// «Рюкзак» (PLAN.md, §3.11): 12 ячеек, срок фишки — 7 дней; эффекты меряются дистанцией. Активировать — только
/// стоя или перед «Стартом»: пока забег идёт, кнопки нет. «Припасы» — за каждые 2 км валидного движения, до 4 в день.
struct BackpackScreen: View {
    @Environment(SocialScreens.self) private var social
    @Environment(\.runScreens) private var run
    @State private var selected: InventoryItem?

    private let columns = Array(repeating: GridItem(.flexible(), spacing: 10), count: 4)

    var body: some View {
        let game = social.game
        LoadableContent(state: game.inventory, load: { await game.loadInventory() }) { info in
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    Text(CountText.items(info.items.count) + " из \(info.slots) ячеек")
                        .font(.headline)
                        .foregroundStyle(Palette.uiInk.color)
                    LazyVGrid(columns: columns, spacing: 10) {
                        ForEach(0..<Int(info.slots), id: \.self) { index in
                            if index < info.items.count {
                                let item = info.items[index]
                                Button {
                                    selected = item
                                } label: {
                                    ItemSlot(item: item)
                                }
                                .buttonStyle(.plain)
                            } else {
                                EmptySlot()
                            }
                        }
                    }
                    if let error = game.actionError {
                        Text(error)
                            .font(.footnote)
                            .foregroundStyle(Palette.uiInk2.color)
                    }
                    Label(
                        "«Припасы» — за каждые 2 км забега, до 4 в день. Фишки на карте и сундуки у края тумана — тоже сюда.",
                        systemImage: "info.circle"
                    )
                    .font(.footnote)
                    .foregroundStyle(Palette.uiInk2.color)
                }
                .padding(16)
            }
            .refreshable { await game.loadInventory() }
            .atmosphereBackground()
        }
        .navigationTitle("Рюкзак")
        .sheet(item: $selected) { item in
            ItemSheet(item: item, running: run?.isRunning == true) {
                Task {
                    await game.activate(item)
                    selected = nil
                }
            }
            .presentationDetents([.medium])
        }
    }
}

extension InventoryItem: @retroactive Identifiable {}

private struct ItemSlot: View {
    let item: InventoryItem

    var body: some View {
        VStack(spacing: 6) {
            GameBadge(systemImage: item.kind.symbol, color: item.kind.color, size: 42)
                .overlay(alignment: .topTrailing) {
                    if item.active {
                        Circle()
                            .fill(PlayerColor.forest.edge.day.color)
                            .frame(width: 12, height: 12)
                            .overlay { Circle().strokeBorder(.white, lineWidth: 2) }
                            .accessibilityLabel("Работает")
                    }
                }
            Text(item.kind.title)
                .font(.caption2.weight(.semibold))
                .foregroundStyle(Palette.uiInk.color)
                .lineLimit(1)
                .minimumScaleFactor(0.7)
        }
        .frame(maxWidth: .infinity, minHeight: 86)
        .brandCard(cornerRadius: 18, padding: 6)
        .accessibilityElement(children: .combine)
    }
}

private struct EmptySlot: View {
    var body: some View {
        RoundedRectangle(cornerRadius: 18, style: .continuous)
            .strokeBorder(Palette.uiInk3.color.opacity(0.35), style: StrokeStyle(lineWidth: 1.5, dash: [5, 4]))
            .frame(maxWidth: .infinity, minHeight: 86)
            .accessibilityHidden(true)
    }
}

/// Фишка: что делает, срок, сколько осталось, «Активировать».
private struct ItemSheet: View {
    let item: InventoryItem
    let running: Bool
    let activate: () -> Void

    var body: some View {
        VStack(spacing: 16) {
            GameBadge(systemImage: item.kind.symbol, color: item.kind.color, size: 72)
                .padding(.top, 24)
            Text(item.kind.title)
                .font(.system(size: 26, weight: .black).width(.expanded))
                .italic()
                .foregroundStyle(Palette.uiInk.color)
            Text(item.kind.effect)
                .font(.body)
                .multilineTextAlignment(.center)
                .foregroundStyle(Palette.uiInk2.color)
            Text(details)
                .font(.footnote.monospacedDigit())
                .foregroundStyle(Palette.uiInk2.color)
            Spacer(minLength: 0)
            if item.active {
                Label("Работает", systemImage: "checkmark.seal.fill")
                    .font(.headline)
                    .foregroundStyle(PlayerColor.forest.edge.day.color)
            } else {
                Button("Активировать", action: activate)
                    .buttonStyle(.brand)
                    .disabled(running)
                if running {
                    Text("Во время забега — нельзя: активируй стоя или перед «Стартом».")
                        .font(.footnote)
                        .foregroundStyle(Palette.uiInk2.color)
                }
            }
        }
        .padding(20)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .atmosphereBackground()
    }

    private var details: String {
        var parts: [String] = []
        if let meters = item.remainingMeters {
            parts.append("осталось " + NumberText.kilometers(fromMeters: meters, fractionDigits: 1))
        }
        let expires = Date(timeIntervalSince1970: Double(item.expiresAtMs) / 1_000)
        parts.append("годна до " + expires.formatted(.dateTime.day().month(.wide).locale(NumberText.locale)))
        return parts.joined(separator: " · ")
    }
}
