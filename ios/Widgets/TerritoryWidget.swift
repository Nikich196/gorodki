import DesignSystem
import Platform
import SwiftUI
import WidgetKit

/// «Моя земля» — виджет экрана «Домой» и экрана блокировки (дизайн-проход 15.10): день сезона кольцом, открыто за сезон,
/// место в «Кто открыл больше»; средний — с огненной кнопкой «Старт» (`gorodki://start`). Данные — снимок профиля
/// из App Group (`TerritorySnapshot`), его пишет приложение. Снимка нет — виджет зовёт открыть «Городки».
struct TerritoryWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: WidgetKind.territory, provider: TerritoryProvider()) { entry in
            TerritoryWidgetView(entry: entry)
                .containerBackground(for: .widget) { TerritoryBackground() }
                .widgetURL(AppLink.map)
        }
        .configurationDisplayName("Моя земля")
        .description("День сезона, сколько Бреста ты открыл и место в рейтинге. Средний — с кнопкой «Старт».")
        .supportedFamilies([.systemSmall, .systemMedium, .accessoryCircular, .accessoryRectangular, .accessoryInline])
    }
}

struct TerritoryEntry: TimelineEntry {
    let date: Date
    /// `nil` — приложение ещё не загрузило профиль (или игрок вышел).
    let snapshot: TerritorySnapshot?
}

struct TerritoryProvider: TimelineProvider {
    func placeholder(in context: Context) -> TerritoryEntry {
        TerritoryEntry(date: .now, snapshot: .sample)
    }

    func getSnapshot(in context: Context, completion: @escaping (TerritoryEntry) -> Void) {
        completion(TerritoryEntry(date: .now, snapshot: context.isPreview ? .sample : TerritorySnapshot.load()))
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<TerritoryEntry>) -> Void) {
        // Приложение просит перерисовать виджет после каждой загрузки профиля; в полночь по Минску меняется день сезона.
        let entry = TerritoryEntry(date: .now, snapshot: TerritorySnapshot.load())
        completion(Timeline(entries: [entry], policy: .after(TerritoryText.nextMinskMidnight(after: .now))))
    }
}

/// Числа виджета по-русски — при любом языке телефона, как в приложении.
enum TerritoryText {
    static let locale = Locale(identifier: "ru_RU")

    /// «1,11 га».
    static func hectares(_ squareMeters: Double?) -> String {
        guard let squareMeters else { return "—" }
        let value = squareMeters / 10_000
        return value.formatted(.number.precision(.fractionLength(value < 10 ? 2 : 1)).locale(locale)) + "\u{00A0}га"
    }

    /// «день 10 из 46» или «день 10».
    static func day(_ snapshot: TerritorySnapshot) -> String? {
        guard let day = snapshot.seasonDay else { return nil }
        guard let days = snapshot.seasonDays else { return "день \(day)" }
        return "день \(day) из \(days)"
    }

    /// Следующая полночь по Минску — тогда сменится день сезона.
    static func nextMinskMidnight(after date: Date) -> Date {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "Europe/Minsk") ?? .current
        let start = calendar.startOfDay(for: date)
        return calendar.date(byAdding: .day, value: 1, to: start) ?? date.addingTimeInterval(3_600)
    }
}

/// Фон виджета: ночное небо над крепостью и свечение огня снизу — одинаково днём и ночью (как у виджетов Фитнеса).
private struct TerritoryBackground: View {
    var body: some View {
        ZStack {
            LinearGradient(
                colors: [Color(hex: 0x1C1450), Color(hex: 0x0A0B1E)], startPoint: .top, endPoint: .bottom)
            RadialGradient(
                colors: [Brand.ember.night.color.opacity(0.55), .clear], center: .bottomTrailing, startRadius: 4,
                endRadius: 170)
            RadialGradient(
                colors: [Brand.dusk.night.color.opacity(0.35), .clear], center: .topLeading, startRadius: 2,
                endRadius: 140)
        }
    }
}

struct TerritoryWidgetView: View {
    let entry: TerritoryEntry
    @Environment(\.widgetFamily) private var family

    var body: some View {
        switch family {
        case .accessoryCircular: circular
        case .accessoryRectangular: rectangular
        case .accessoryInline: inline
        case .systemMedium: medium
        default: small
        }
    }

    // MARK: - Экран «Домой»

    private var small: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 6) {
                BrandMark(size: 22)
                Text("ГОРОДКИ")
                    .font(.system(size: 11, weight: .black).width(.expanded))
                    .italic()
                    .foregroundStyle(.white.opacity(0.85))
            }
            Spacer(minLength: 6)
            if let snapshot = entry.snapshot {
                SeasonDial(snapshot: snapshot, size: 46)
                Spacer(minLength: 6)
                Text(TerritoryText.hectares(snapshot.seasonSquareMeters))
                    .font(.system(size: 24, weight: .black).width(.expanded))
                    .italic()
                    .foregroundStyle(.white)
                    .minimumScaleFactor(0.6)
                    .lineLimit(1)
                    .widgetAccentable()
                Text(rankText(snapshot) ?? "открыто за сезон")
                    .font(.caption2.weight(.semibold))
                    .foregroundStyle(.white.opacity(0.7))
                    .lineLimit(1)
            } else {
                signInHint
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
    }

    private var medium: some View {
        HStack(spacing: 14) {
            small
            VStack(alignment: .trailing, spacing: 10) {
                if let snapshot = entry.snapshot {
                    VStack(alignment: .trailing, spacing: 2) {
                        Text(snapshot.displayName)
                            .font(.subheadline.weight(.bold))
                            .foregroundStyle(.white)
                            .lineLimit(1)
                        Text("за всё время " + TerritoryText.hectares(snapshot.allTimeSquareMeters))
                            .font(.caption2)
                            .foregroundStyle(.white.opacity(0.7))
                            .lineLimit(1)
                    }
                }
                Spacer(minLength: 0)
                Link(destination: AppLink.start) {
                    Label("Старт", systemImage: "figure.run")
                        .font(.system(size: 15, weight: .heavy).width(.expanded))
                        .foregroundStyle(.white)
                        .padding(.horizontal, 16)
                        .frame(height: 44)
                        .background(Brand.fire(.night), in: .capsule)
                        .overlay { Capsule().strokeBorder(.white.opacity(0.35), lineWidth: 1) }
                        .shadow(color: Brand.ember.night.color.opacity(0.6), radius: 10, y: 4)
                }
                .accessibilityLabel("Начать забег")
            }
        }
    }

    private var signInHint: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Открой «Городки»")
                .font(.subheadline.weight(.bold))
                .foregroundStyle(.white)
            Text("и войди — здесь будет твоя земля.")
                .font(.caption2)
                .foregroundStyle(.white.opacity(0.7))
        }
    }

    // MARK: - Экран блокировки

    private var circular: some View {
        Gauge(value: Double(entry.snapshot?.seasonDay ?? 0), in: 0...Double(max(1, entry.snapshot?.seasonDays ?? 1))) {
            Image(systemName: "figure.run")
        } currentValueLabel: {
            Text(entry.snapshot?.seasonDay.map(String.init) ?? "—")
        }
        .gaugeStyle(.accessoryCircular)
        .widgetAccentable()
    }

    private var rectangular: some View {
        VStack(alignment: .leading, spacing: 1) {
            Label("Городки", systemImage: "flag.checkered")
                .font(.caption.weight(.bold))
                .widgetAccentable()
            if let snapshot = entry.snapshot {
                Text(TerritoryText.hectares(snapshot.seasonSquareMeters) + " за сезон")
                    .font(.headline)
                Text([TerritoryText.day(snapshot), rankText(snapshot)].compactMap(\.self).joined(separator: " · "))
                    .font(.caption)
            } else {
                Text("Открой приложение и войди")
                    .font(.caption)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var inline: some View {
        Label(
            entry.snapshot.map { TerritoryText.hectares($0.seasonSquareMeters) + " за сезон" } ?? "Городки",
            systemImage: "figure.run")
    }

    private func rankText(_ snapshot: TerritorySnapshot) -> String? {
        snapshot.explorationRank.map { "№\u{00A0}\($0) в рейтинге" }
    }
}

/// День сезона кольцом огня (без анимации: виджет — неподвижная картинка).
private struct SeasonDial: View {
    let snapshot: TerritorySnapshot
    let size: CGFloat

    var body: some View {
        let progress = snapshot.seasonDays.map { Double(snapshot.seasonDay ?? 0) / Double(max(1, $0)) } ?? 1
        ZStack {
            Circle().stroke(.white.opacity(0.14), lineWidth: 5)
            Circle()
                .trim(from: 0, to: min(1, max(0.02, progress)))
                .stroke(
                    AngularGradient(
                        colors: [Brand.amber.night.color, Brand.ember.night.color, Brand.dusk.night.color],
                        center: .center),
                    style: StrokeStyle(lineWidth: 5, lineCap: .round)
                )
                .rotationEffect(.degrees(-90))
                .widgetAccentable()
            VStack(spacing: -2) {
                Text(snapshot.seasonDay.map(String.init) ?? "—")
                    .font(.system(size: 15, weight: .black).width(.expanded))
                    .foregroundStyle(.white)
                Text("день")
                    .font(.system(size: 8, weight: .semibold))
                    .foregroundStyle(.white.opacity(0.7))
            }
        }
        .frame(width: size, height: size)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(TerritoryText.day(snapshot) ?? "сезон")
    }
}
