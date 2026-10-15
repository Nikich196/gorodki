import DesignSystem
import GameCore
import GorodkiAPI
import SwiftUI

/// Карточка недели (PLAN.md, §3.15): км, захвачено, открыто, прирост «% Бреста» — картинкой для Instagram и Telegram.
/// Приватность: без следов, без дома и без точных дат — только неделя.
struct WeeklyCardScreen: View {
    @Environment(SocialScreens.self) private var social
    @Environment(\.displayScale) private var displayScale

    var body: some View {
        let game = social.game
        LoadableContent(state: game.weekly, load: { await game.loadWeekly() }) { card in
            ScrollView {
                VStack(spacing: 18) {
                    WeeklyCardArt(card: card)
                    if let image = render(card) {
                        ShareLink(
                            item: image, preview: SharePreview("Моя неделя в «Городках»", image: image)
                        ) {
                            Label("Поделиться", systemImage: "square.and.arrow.up")
                        }
                        .buttonStyle(.brand)
                    }
                    Text("На карточке нет следов, дома и точных дат — только итог недели.")
                        .font(.footnote)
                        .foregroundStyle(Palette.uiInk2.color)
                }
                .padding(16)
            }
            .refreshable { await game.loadWeekly() }
            .atmosphereBackground()
        }
        .navigationTitle("Карточка недели")
    }

    @MainActor
    private func render(_ card: WeeklyCard) -> Image? {
        let renderer = ImageRenderer(
            content: WeeklyCardArt(card: card).frame(width: 360).environment(\.colorScheme, .dark))
        renderer.scale = max(displayScale, 3)
        return renderer.uiImage.map { Image(uiImage: $0) }
    }
}

/// Сама карточка: ночное небо над крепостью, огонь бренда, четыре числа недели.
struct WeeklyCardArt: View {
    let card: WeeklyCard

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 8) {
                BrandMark(size: 30)
                Text("ГОРОДКИ")
                    .font(.system(size: 15, weight: .black).width(.expanded))
                    .italic()
                    .foregroundStyle(.white)
                Spacer()
                Text(weekTitle)
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.white.opacity(0.75))
            }
            Text("Моя неделя")
                .font(.system(size: 34, weight: .black).width(.expanded))
                .italic()
                .foregroundStyle(.white)
            Grid(alignment: .leading, horizontalSpacing: 18, verticalSpacing: 14) {
                GridRow {
                    stat(
                        NumberText.kilometers(fromMeters: card.distanceMeters, fractionDigits: 1), "пробежал",
                        "figure.run")
                    stat(
                        "+" + NumberText.hectares(fromSquareMeters: card.capturedSquareMeters, fractionDigits: 2),
                        "захватил", "flag.fill")
                }
                GridRow {
                    stat(
                        "+" + NumberText.hectares(fromSquareMeters: card.exploredSquareMeters, fractionDigits: 2),
                        "открыл тумана", "cloud.sun.fill")
                    if let percent = card.brestPercentGained {
                        stat("+" + NumberText.decimal(percent, fractionDigits: 1) + "\u{00A0}%", "Бреста", "map.fill")
                    }
                }
            }
            FortressSkylineView(color: .white.opacity(0.12))
                .frame(height: 40)
            Text("© участники OpenStreetMap")
                .font(.system(size: 9))
                .foregroundStyle(.white.opacity(0.5))
        }
        .padding(22)
        .background {
            ZStack {
                LinearGradient(
                    colors: [Color(hex: 0x1C1450), Color(hex: 0x0A0B1E)], startPoint: .top, endPoint: .bottom)
                RadialGradient(
                    colors: [Brand.ember.night.color.opacity(0.6), .clear], center: .bottomTrailing, startRadius: 4,
                    endRadius: 260)
                RadialGradient(
                    colors: [Brand.dusk.night.color.opacity(0.4), .clear], center: .topLeading, startRadius: 2,
                    endRadius: 220)
            }
        }
        .clipShape(.rect(cornerRadius: 28, style: .continuous))
        .overlay { RoundedRectangle(cornerRadius: 28, style: .continuous).strokeBorder(.white.opacity(0.18)) }
        .shadow(color: Brand.ember.day.color.opacity(0.25), radius: 20, y: 10)
        .accessibilityElement(children: .combine)
    }

    /// «Неделя 16 ноября» — понедельник недели, без дней забегов.
    private var weekTitle: String {
        let parser = DateFormatter()
        parser.dateFormat = "yyyy-MM-dd"
        parser.locale = Locale(identifier: "en_US_POSIX")
        guard let monday = parser.date(from: card.weekStart) else { return card.weekStart }
        return "неделя с " + monday.formatted(.dateTime.day().month(.wide).locale(NumberText.locale))
    }

    private func stat(_ value: String, _ title: String, _ symbol: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Label(title, systemImage: symbol)
                .font(.caption.weight(.semibold))
                .foregroundStyle(Brand.amber.night.color)
            Text(value)
                .font(.system(size: 22, weight: .black).width(.expanded).monospacedDigit())
                .foregroundStyle(.white)
                .minimumScaleFactor(0.7)
                .lineLimit(1)
        }
    }
}

/// Зал славы (PLAN.md, §3.5–§3.6): итог каждого закрытого сезона — первые места игроков и кланов по лигам.
struct HallOfFameScreen: View {
    @Environment(SocialScreens.self) private var social

    var body: some View {
        let game = social.game
        LoadableContent(state: game.hallOfFame, load: { await game.loadHallOfFame() }) { hall in
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    if hall.seasons.isEmpty {
                        Text("Зал славы заполнится, когда закончится первый сезон — 29 ноября.")
                            .font(.body)
                            .foregroundStyle(Palette.uiInk2.color)
                            .contentCard()
                    }
                    ForEach(hall.seasons, id: \.season) { season in
                        SectionTitle(season.name)
                        VStack(spacing: 0) {
                            ForEach(Array(season.entries.enumerated()), id: \.offset) { index, entry in
                                HallRow(entry: entry)
                                if index < season.entries.count - 1 {
                                    Divider().padding(.leading, 60)
                                }
                            }
                        }
                        .brandCard(padding: 0)
                    }
                }
                .padding(16)
            }
            .refreshable { await game.loadHallOfFame() }
            .atmosphereBackground()
        }
        .navigationTitle("Зал славы")
    }
}

private struct HallRow: View {
    let entry: Components.Schemas.HallOfFameEntry

    private static let metals = [Color(hex: 0xE0A400), Color(hex: 0xA7B0BA), Color(hex: 0xC27A44)]

    var body: some View {
        let place = Int(entry.place)
        let metal = Self.metals[min(max(place - 1, 0), 2)]
        HStack(spacing: 12) {
            ZStack {
                Circle().fill(metal.opacity(0.18))
                Image(systemName: entry.kind == .clan ? "shield.fill" : place == 1 ? "crown.fill" : "medal.fill")
                    .foregroundStyle(metal)
            }
            .frame(width: 36, height: 36)
            VStack(alignment: .leading, spacing: 2) {
                Text((entry.name ?? "Игрок скрыл ник") + (entry.me ? " · ты" : ""))
                    .font(entry.me ? .body.weight(.bold) : .body.weight(.semibold))
                    .foregroundStyle(Palette.uiInk.color)
                Text(
                    (entry.kind == .clan ? "Клан · " : "") + (entry.league == .bike ? "Вело" : "Бег")
                        + " · \(place) место"
                )
                .font(.caption)
                .foregroundStyle(Palette.uiInk2.color)
            }
            Spacer(minLength: 8)
            Text(CountText.score(Int(entry.value)))
                .font(.subheadline.weight(.bold).monospacedDigit())
                .foregroundStyle(Palette.uiInk.color)
        }
        .padding(.horizontal, 14)
        .frame(minHeight: 56)
        .background(entry.me ? Brand.ember.day.color.opacity(0.10) : .clear)
        .accessibilityElement(children: .combine)
    }
}
