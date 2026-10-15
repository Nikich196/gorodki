import DesignSystem
import GameCore
import GorodkiAPI
import SwiftUI

/// «Коллекция» (PLAN.md, §3.12): тайники Бреста. Найденные — значок цвета редкости, «№ N нашедших», у первого —
/// золотая рамка; ненайденные — силуэт и подсказка-загадка о месте. На карте тайников нет; найти — пройти в 30 м
/// в валидном забеге.
struct CollectionScreen: View {
    @Environment(SocialScreens.self) private var social

    private let columns = [GridItem(.flexible(), spacing: 12), GridItem(.flexible(), spacing: 12)]

    var body: some View {
        let game = social.game
        LoadableContent(state: game.collection, load: { await game.loadCollection() }) { info in
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    if let summary = game.collectionSummary {
                        Text(summary)
                            .font(.system(size: 24, weight: .black).width(.expanded))
                            .italic()
                            .foregroundStyle(Palette.uiInk.color)
                    }
                    Text(
                        "Тайники спрятаны в парках, на набережной и у арт-объектов. Подсказка яснеет, когда открываешь туман рядом."
                    )
                    .font(.footnote)
                    .foregroundStyle(Palette.uiInk2.color)
                    LazyVGrid(columns: columns, spacing: 12) {
                        ForEach(info.caches, id: \.id) { cache in
                            CacheTile(cache: cache)
                        }
                    }
                }
                .padding(16)
            }
            .refreshable { await game.loadCollection() }
            .atmosphereBackground()
        }
        .navigationTitle("Коллекция")
    }
}

private struct CacheTile: View {
    let cache: CacheBadge
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let found = cache.foundAtMs != nil
        let color = cache.rarity.color
        VStack(spacing: 10) {
            ZStack {
                Circle()
                    .fill(
                        found
                            ? AnyShapeStyle(
                                RadialGradient(
                                    colors: [color.mix(with: .white, by: 0.35), color], center: .topLeading,
                                    startRadius: 2, endRadius: 70))
                            : AnyShapeStyle(Palette.uiCell2.color)
                    )
                    .overlay {
                        if cache.goldFrame {
                            Circle().strokeBorder(
                                AngularGradient(
                                    colors: [Color(hex: 0xFFE27A), Color(hex: 0xC9921A), Color(hex: 0xFFE27A)],
                                    center: .center), lineWidth: 4)
                        }
                    }
                    .shadow(color: found ? color.opacity(0.45) : .clear, radius: 10, y: 4)
                Image(systemName: found ? cache.rarity.symbol : "questionmark")
                    .font(.system(size: 30, weight: .bold))
                    .foregroundStyle(found ? .white : Palette.uiInk3.color)
                    .symbolEffect(.bounce, options: .nonRepeating, isActive: found && !reduceMotion)
            }
            .frame(width: 76, height: 76)
            Text(cache.rarity.title.uppercased())
                .font(.system(size: 10, weight: .heavy).width(.expanded))
                .kerning(0.6)
                .foregroundStyle(color)
            Text(cache.name ?? "Не найден")
                .font(.subheadline.weight(.bold))
                .foregroundStyle(found ? Palette.uiInk.color : Palette.uiInk2.color)
                .multilineTextAlignment(.center)
            if let number = cache.finderNumber {
                Text(number == 1 ? "Ты нашёл первым" : "№ \(number) нашедших")
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(cache.goldFrame ? Color(hex: 0xC9921A) : Palette.uiInk2.color)
            } else {
                Text("«\(cache.hint)»")
                    .font(.caption.italic())
                    .foregroundStyle(Palette.uiInk2.color)
                    .multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .frame(maxWidth: .infinity, alignment: .top)
        .brandCard(padding: 12)
        .accessibilityElement(children: .combine)
    }
}
