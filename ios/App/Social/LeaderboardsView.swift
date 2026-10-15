import DesignSystem
import GameCore
import SwiftUI

/// Вкладка «Рейтинги» (PLAN.md, §5, экран 20; решено 26.09 по делегированию — ios-app.md): «Захват | Исследование |
/// Короли» в панели навигации, лига «Бег» («Вело» — с Сезона 1), свой ряд закреплён снизу, если его не видно;
/// «итог» или «предварительно» — у рейтинга сезона. Строки переезжают при смене мест (стабильные `id`), числа
/// перекатываются (`numericText`).
struct LeaderboardsTab: View {
    @Bindable var model: LeaderboardsModel
    /// Цвет игрока — метка своего ряда.
    let myColor: PlayerColor
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    /// Свой ряд среди первых виден на экране — закреплять его снизу не нужно.
    @State private var mineVisible = true

    var body: some View {
        NavigationStack {
            content
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .atmosphereBackground()
                .demoBadgeInset()
                .navigationTitle("Рейтинги")
                .navigationBarTitleDisplayMode(.inline)
                .toolbar {
                    ToolbarItem(placement: .principal) {
                        Picker("Рейтинг", selection: $model.kind) {
                            ForEach(LeaderboardKind.allCases) { kind in
                                Text(kind.title).tag(kind)
                            }
                        }
                        .pickerStyle(.segmented)
                        // 360: «Исследование» целиком (на 320 обрезалось до «Исследован…»).
                        .frame(maxWidth: 360)
                    }
                }
                .safeAreaInset(edge: .bottom) {
                    if model.kind != .kings, let pinned = model.pinnedRow(mineRowVisible: mineVisible) {
                        LeaderboardRowView(row: pinned, myColor: myColor)
                            .brandCard()
                            .overlay {
                                RoundedRectangle(cornerRadius: Radius.card).stroke(myColor.edgeColor, lineWidth: 1.5)
                            }
                            .padding(.horizontal, 16)
                            .padding(.bottom, 8)
                            .transition(.move(edge: .bottom).combined(with: .opacity))
                    }
                }
                .animation(Motion.numericRoll.unlessReduceMotion(reduceMotion), value: mineVisible)
                .task(id: "\(model.kind.rawValue)-\(model.league.rawValue)") { await model.load() }
                .refreshable { await model.load() }
        }
    }

    @ViewBuilder
    private var content: some View {
        if model.kind == .kings {
            TabPlaceholder(
                systemImage: "crown",
                title: "Короли участков — скоро",
                text: "Короткие отрезки города: кто быстрее всех в сезоне, тот и король. Экран — в следующей версии."
            )
        } else if let failure = model.failure, model.rows.isEmpty {
            SocialFailureView(failure: failure) { await model.load() }
        } else if !model.loaded && model.rows.isEmpty {
            ProgressView()
        } else {
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    header
                    if model.rows.isEmpty {
                        Text("Рейтинг появится после первого ночного среза — в 00:00 по Минску.")
                            .font(.body)
                            .foregroundStyle(Palette.uiInk2.color)
                            .contentCard()
                    } else {
                        rows
                    }
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 12)
            }
        }
    }

    /// Лига (меню, «Вело» — с замком) и подпись среза: сезон, «итог / предварительно», день.
    private var header: some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            Menu {
                ForEach(LeaderboardLeague.allCases) { league in
                    Button {
                        model.league = league
                    } label: {
                        if league.locked {
                            Label(league.title + " — с Сезона 1", systemImage: "lock")
                        } else {
                            Label(league.title, systemImage: model.league == league ? "checkmark" : "figure.run")
                        }
                    }
                    .disabled(league.locked)
                }
            } label: {
                Label(model.league.title, systemImage: "chevron.down")
                    .labelStyle(TrailingIconLabelStyle())
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Palette.uiInk.color)
                    .padding(.horizontal, 12)
                    .frame(minHeight: 32)
                    .background(Palette.uiFill.color, in: .capsule)
            }
            .accessibilityLabel("Лига: \(model.league.title)")
            if let caption = model.caption {
                Text(caption)
                    .font(.footnote)
                    .foregroundStyle(Palette.uiInk2.color)
                    .lineLimit(2)
            }
            Spacer(minLength: 0)
            if let status = model.statusText {
                Text(status)
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(model.isFinal == true ? Palette.goldInk.color : Palette.uiInk2.color)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 3)
                    .background(Palette.uiFill.color, in: .capsule)
            }
        }
    }

    /// Первые три места — на пьедестале (`LeaderboardPodium`), в списке — с четвёртого.
    private var podiumCount: Int {
        model.rows.count >= 3 ? 3 : 0
    }

    private var rows: some View {
        VStack(spacing: 12) {
            if podiumCount > 0 {
                LeaderboardPodium(rows: Array(model.rows.prefix(podiumCount)), myColor: myColor)
            }
            rowList
        }
    }

    private var rowList: some View {
        VStack(spacing: 0) {
            ForEach(model.rows.dropFirst(podiumCount)) { row in
                LeaderboardRowView(row: row, myColor: myColor)
                    .onScrollVisibilityChange(threshold: 0.6) { visible in
                        if row.me { mineVisible = visible }
                    }
                if row.id != model.rows.last?.id {
                    Divider().padding(.leading, 64)
                }
            }
        }
        .brandCard()
        .animation(Motion.numericRoll.unlessReduceMotion(reduceMotion), value: model.rows)
    }
}

/// Строка рейтинга: место (у первого — корона), ник, число. Свой ряд — жирный, с меткой цвета игрока.
struct LeaderboardRowView: View {
    let row: LeaderboardRow
    let myColor: PlayerColor

    var body: some View {
        HStack(spacing: 12) {
            ZStack {
                if row.rank == 1 {
                    Image(systemName: "crown.fill")
                        .font(.title3)
                        .foregroundStyle(Palette.gold.color)
                } else {
                    Text(verbatim: "\(row.rank)")
                        .font(.headline.monospacedDigit())
                        .fontDesign(.rounded)
                        .foregroundStyle(row.rank <= 3 ? Palette.uiInk.color : Palette.uiInk2.color)
                        .contentTransition(.numericText(value: Double(row.rank)))
                }
            }
            .frame(width: 36)
            if row.me {
                Circle()
                    .fill(myColor.color)
                    .overlay { Circle().stroke(myColor.edgeColor, lineWidth: 1.5) }
                    .frame(width: 10, height: 10)
                    .accessibilityHidden(true)
            }
            Text(verbatim: row.name)
                .font(row.me ? .body.weight(.bold) : .body)
                .foregroundStyle(Palette.uiInk.color)
                .lineLimit(1)
            if row.me {
                Text("ты")
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(Palette.uiInk2.color)
            }
            Spacer(minLength: 8)
            Text(verbatim: row.value)
                .font(.body.weight(.semibold).monospacedDigit())
                .fontDesign(.rounded)
                .foregroundStyle(Palette.uiInk.color)
                .contentTransition(.numericText(value: row.number))
        }
        .padding(.horizontal, 12)
        .frame(minHeight: 52)
        .accessibilityElement(children: .combine)
        .accessibilityLabel(Text(verbatim: "\(row.rank) место, \(row.name)\(row.me ? ", ты" : ""), \(row.value)"))
    }
}

/// Значок справа от надписи: «Бег ⌄».
private struct TrailingIconLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 4) {
            configuration.title
            configuration.icon.font(.caption.weight(.bold))
        }
    }
}

/// Пьедестал первой тройки (бренд «Крепость», 15.10): столбики поднимаются по очереди, у первого — огонь, корона
/// и свечение; кольца аватаров — золото, серебро, бронза.
private struct LeaderboardPodium: View {
    let rows: [LeaderboardRow]
    let myColor: PlayerColor
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var raised = false

    private static let metals: [Color] = [
        Color(red: 0.96, green: 0.77, blue: 0.26), Color(red: 0.78, green: 0.82, blue: 0.87),
        Color(red: 0.82, green: 0.54, blue: 0.31),
    ]

    var body: some View {
        let theme = Theme(colorScheme)
        // Слева второй, в центре первый, справа третий.
        let order = [1, 0, 2].filter { $0 < rows.count }
        HStack(alignment: .bottom, spacing: 10) {
            ForEach(order, id: \.self) { index in
                column(rows[index], place: index, theme: theme)
            }
        }
        .padding(.top, 8)
        .onAppear {
            guard !reduceMotion else {
                raised = true
                return
            }
            raised = false
            withAnimation(.spring(duration: 0.9, bounce: 0.35).delay(0.15)) { raised = true }
        }
        .accessibilityElement(children: .contain)
    }

    private func column(_ row: LeaderboardRow, place: Int, theme: Theme) -> some View {
        let heights: [CGFloat] = [118, 88, 68]
        let fills: [LinearGradient] = [
            Brand.fire(theme),
            LinearGradient(
                colors: [Brand.dusk[theme].color, Brand.dusk[theme].color.opacity(0.7)], startPoint: .top,
                endPoint: .bottom),
            LinearGradient(
                colors: [Brand.river[theme].color, Brand.river[theme].color.opacity(0.7)], startPoint: .top,
                endPoint: .bottom),
        ]
        return VStack(spacing: 6) {
            if place == 0 {
                Image(systemName: "crown.fill")
                    .font(.title2)
                    .foregroundStyle(Self.metals[0])
                    .shadow(color: Self.metals[0].opacity(0.7), radius: 8)
                    .scaleEffect(raised ? 1 : 0.4)
                    .opacity(raised ? 1 : 0)
                    .accessibilityHidden(true)
            }
            Text(String(row.name.prefix(1)))
                .font(.system(size: place == 0 ? 26 : 20, weight: .black).width(.expanded))
                .foregroundStyle(row.me ? myColor.startInkColor : Palette.uiInk.color)
                .frame(width: place == 0 ? 62 : 50, height: place == 0 ? 62 : 50)
                .background(row.me ? myColor.color : Palette.uiCell2.color, in: .circle)
                .overlay { Circle().strokeBorder(Self.metals[place], lineWidth: 3) }
                .shadow(color: Self.metals[place].opacity(place == 0 ? 0.6 : 0.3), radius: place == 0 ? 12 : 6)
                .accessibilityHidden(true)
            Text(row.name)
                .font(.footnote.weight(.bold))
                .foregroundStyle(Palette.uiInk.color)
                .lineLimit(1)
                .minimumScaleFactor(0.7)
            Text(row.value)
                .font(.caption.weight(.heavy).width(.expanded))
                .foregroundStyle(Palette.uiInk2.color)
                .lineLimit(1)
                .minimumScaleFactor(0.6)
            UnevenRoundedRectangle(topLeadingRadius: 14, topTrailingRadius: 14)
                .fill(fills[place])
                .overlay(alignment: .top) {
                    Text(NumberText.integer(row.rank))
                        .font(.system(size: place == 0 ? 40 : 30, weight: .black).width(.expanded))
                        .italic()
                        .foregroundStyle(.white)
                        .padding(.top, 8)
                        .accessibilityHidden(true)
                }
                .frame(height: raised ? heights[place] : 10)
                .shadow(color: place == 0 ? Brand.ember[theme].color.opacity(0.4) : .clear, radius: 14, y: 6)
        }
        .frame(maxWidth: .infinity)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("\(row.rank) место: \(row.name), \(row.value)\(row.me ? ", это ты" : "")")
    }
}
