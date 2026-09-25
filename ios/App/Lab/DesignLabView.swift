import DesignSystem
import GameCore
import MapKit
import SwiftUI

/// «Лаборатория → Дизайн»: утверждённая дизайн-система (design/APPROVALS.md, 25.09.2026) на настоящем телефоне —
/// палитра с уровнями, отношения, шрифт, стекло, редкости, церемонии и перекатка цифр. Настоящих экранов карты и HUD
/// здесь нет — они следующие. Токены — `DesignSystem`, описание — docs/design/tokens.md.
struct DesignLabView: View {
    @State private var theme = ThemeChoice.system
    /// Цвет игрока: им закрашены «Старт», своя земля и церемония.
    @State private var player = PlayerColor.orange

    var body: some View {
        List {
            Section {
                Picker("Тема", selection: $theme) {
                    ForEach(ThemeChoice.allCases) { choice in
                        Text(choice.title).tag(choice)
                    }
                }
                .pickerStyle(.segmented)
            } footer: {
                Text(
                    "Игра следует теме системы, своего переключателя у неё нет. Здесь он — только чтобы сравнить "
                        + "день и ночь на одном экране.")
            }
            .listRowBackground(Palette.uiCell.color)

            PaletteSection()
            RelationSection(player: player)
            TypeSection()
            GlassSection(player: $player)
            RaritySection()
            CaptureSection(player: player)
            NumericSection()
        }
        .scrollContentBackground(.hidden)
        .background(Palette.uiBackground.color)
        .foregroundStyle(Palette.uiInk.color)
        .navigationTitle("Дизайн")
        .preferredColorScheme(theme.colorScheme)
    }
}

private enum ThemeChoice: String, CaseIterable, Identifiable {
    case system, day, night

    var id: Self { self }

    var title: String {
        switch self {
        case .system: "Как в системе"
        case .day: "День"
        case .night: "Ночь"
        }
    }

    var colorScheme: ColorScheme? {
        switch self {
        case .system: nil
        case .day: .light
        case .night: .dark
        }
    }
}

// MARK: - Палитра

private struct PaletteSection: View {
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        let theme = Theme(colorScheme)
        Section {
            ForEach(PlayerColor.allCases) { color in
                PaletteRow(color: color, theme: theme)
            }
            FogRow(theme: theme)
        } header: {
            Text("12 цветов игроков · L1, L2, L3")
        } footer: {
            Text(
                "Уровень — насыщенность: хрома и прозрачность вместе (§6.3). Днём у светлых цветов заливка на тон "
                    + "глубже, иначе её не видно. Образцы — на земле стилизованной карты; на телефоне подложка — "
                    + "Apple Maps, альфы донастраиваются на спайке S4.")
        }
        .listRowBackground(Palette.uiCell.color)
    }
}

private struct PaletteRow: View {
    let color: PlayerColor
    let theme: Theme

    var body: some View {
        HStack(spacing: 12) {
            Circle()
                .fill(color.color)
                .frame(width: 28, height: 28)
            VStack(alignment: .leading, spacing: 2) {
                Text(color.title)
                    .font(.headline)
                Text(verbatim: "\(color.base.hexString) · кромка \(color.edge[theme].hexString)")
                    .font(.caption.monospaced())
                    .foregroundStyle(Palette.uiInk2.color)
            }
            Spacer(minLength: 8)
            HStack(spacing: 4) {
                ForEach(TerritoryLevel.allCases, id: \.self) { level in
                    LandSwatch(fill: color.fillColor(level), edge: color.edgeColor)
                        .frame(width: 34, height: 26)
                }
            }
        }
        .accessibilityElement(children: .combine)
    }
}

/// Кусочек земли: заливка уровня на земле стилизованной карты и чёткая кромка.
private struct LandSwatch: View {
    let fill: Color
    let edge: Color

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 6)
        shape.fill(Palette.mapLand.color)
            .overlay { shape.fill(fill) }
            .overlay { shape.stroke(edge, lineWidth: 1.5) }
    }
}

private struct FogRow: View {
    let theme: Theme

    var body: some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text("Туман «Исследование»")
                    .font(.headline)
                Text(verbatim: summary)
                    .font(.caption.monospaced())
                    .foregroundStyle(Palette.uiInk2.color)
            }
            Spacer(minLength: 8)
            // Дымка над землёй, у открытого — полоса кромки 1,8 pt.
            ZStack {
                Rectangle().fill(Palette.mapLand.color)
                Rectangle().fill(FogStyle.haze.color)
                Capsule()
                    .fill(Palette.mapLand.color)
                    .overlay { Capsule().stroke(FogStyle.edge.color, lineWidth: FogStyle.edgeBandWidth) }
                    .frame(width: 60, height: 12)
            }
            .frame(width: 110, height: 26)
            .clipShape(.rect(cornerRadius: 6))
        }
        .accessibilityElement(children: .combine)
    }

    /// «дымка #D0C1A6 · 0,76, кромка #9A6C35».
    private var summary: String {
        let haze = FogStyle.haze[theme]
        let alpha = NumberText.decimal(haze.alpha, fractionDigits: 2)
        return "дымка \(haze.hexString) · \(alpha), кромка \(FogStyle.edge[theme].hexString)"
    }
}

// MARK: - Отношения

private struct RelationSection: View {
    let player: PlayerColor

    var body: some View {
        Section {
            ForEach(TerritoryRelation.allCases, id: \.self) { relation in
                HStack(spacing: 14) {
                    RelationSwatch(relation: relation, owner: owner(of: relation))
                        .frame(width: 76, height: 50)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(relation.title)
                            .font(.headline)
                        Text(relation.detail)
                            .font(.subheadline)
                            .foregroundStyle(Palette.uiInk2.color)
                    }
                }
                .accessibilityElement(children: .combine)
            }
        } header: {
            Text("Отношение — узор, уровень — насыщенность")
        } footer: {
            Text(
                "Как в режиме «Отношения»: моё — твой цвет (выбери его у «Старта»), клан — Sky, соперник и спорное — "
                    + "Red. Уровень L2. Муравьи ползут только на спорной земле.")
        }
        .listRowBackground(Palette.uiCell.color)
    }

    private func owner(of relation: TerritoryRelation) -> PlayerColor {
        switch relation {
        case .mine: player
        case .clan: .sky
        case .rival, .contested, .lost, .noMansLand: .red
        }
    }
}

/// Образец земли с отношением: заливка, штриховка, подложка и кромка — те же числа, что возьмут рендереры карты.
private struct RelationSwatch: View {
    let relation: TerritoryRelation
    let owner: PlayerColor
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var antsPhase = 0.0

    var body: some View {
        let theme = Theme(colorScheme)
        let levelFill = owner.fill(.two, theme: theme)
        let edge = relation.edge
        let edgeColor = relation.edgeColor(ownerEdge: owner.edge[theme], theme: theme).color
        let glow = relation.glowRadius(theme: theme)
        let shape = RoundedRectangle(cornerRadius: 10)
        ZStack {
            shape.fill(Palette.mapLand.color)
            shape.fill(relation.fill(levelFill: levelFill, theme: theme).color)
            if let hatch = relation.hatch {
                HatchLines(spacing: hatch.spacing)
                    .stroke(hatch.color(levelFill: levelFill).color, lineWidth: hatch.lineWidth)
                    .clipShape(shape)
            }
            if let halo = edge.haloWidth {
                shape.stroke(Palette.antsHalo.color, lineWidth: halo)
            }
            shape
                .stroke(
                    edgeColor,
                    style: StrokeStyle(lineWidth: edge.width, dash: edge.dash.map { CGFloat($0) }, dashPhase: antsPhase)
                )
                .shadow(color: glow > 0 ? edgeColor : .clear, radius: glow)
        }
        .onAppear {
            guard relation.hasMarchingAnts, !reduceMotion else { return }
            withAnimation(Motion.marchingAnts) {
                antsPhase = MotionSpec.Ants.phaseShift
            }
        }
    }
}

/// Линии под 45° с шагом `spacing` pt (по перпендикуляру) — штриховка клана.
private struct HatchLines: Shape {
    let spacing: Double

    nonisolated func path(in rect: CGRect) -> Path {
        var path = Path()
        let step = CGFloat(spacing * 2.0.squareRoot())
        var x = rect.minX - rect.height
        while x < rect.maxX {
            path.move(to: CGPoint(x: x, y: rect.maxY))
            path.addLine(to: CGPoint(x: x + rect.height, y: rect.minY))
            x += step
        }
        return path
    }
}

// MARK: - Шрифт

private struct TypeSection: View {
    var body: some View {
        Section {
            VStack(alignment: .leading, spacing: 0) {
                Text("До замыкания")
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Palette.uiInk2.color)
                HStack(alignment: .firstTextBaseline, spacing: 4) {
                    Text(verbatim: NumberText.integer(140))
                        .scaledFont(.hudHero)
                    Text("м")
                        .scaledFont(.hudHeroUnit)
                }
                Caption("SF Pro Rounded Bold 78 pt — главная цифра HUD (§6.7)")
            }
            VStack(alignment: .leading, spacing: 4) {
                HStack(alignment: .firstTextBaseline, spacing: 14) {
                    Metric(value: NumberText.decimal(3.2, fractionDigits: 1), unit: "км")
                    Metric(value: "5:32", unit: "/км")
                    Metric(value: "17:42", unit: "")
                }
                Caption("Rounded Heavy 29 pt — метрики HUD, цифры одной ширины")
            }
            VStack(alignment: .leading, spacing: 6) {
                Text("Коллекция")
                    .font(.largeTitle.bold())
                Text("Короли участков")
                    .font(.headline)
                Text("Обеги участок — и он твой, ровно по контуру следа.")
                    .font(.body)
                Text("Новая находка")
                    .capsLabel()
            }
            VStack(alignment: .leading, spacing: 4) {
                Text(verbatim: areaSample)
                Caption("§6.10: запятая, неразрывный пробел, сотка = 100 м²")
            }
        } header: {
            Text("Шрифт")
        }
        .listRowBackground(Palette.uiCell.color)
    }

    /// «12 480 м² · 1,25 га · 125 соток».
    private var areaSample: String {
        let squareMeters = NumberText.squareMeters(12_480)
        let hectares = NumberText.hectares(fromSquareMeters: 12_480, fractionDigits: 2)
        return "\(squareMeters) · \(hectares) · \(CountText.sotki(125))"
    }
}

private struct Metric: View {
    let value: String
    let unit: String

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 2) {
            Text(verbatim: value)
                .scaledFont(.hudMetric)
            if !unit.isEmpty {
                Text(verbatim: unit)
                    .scaledFont(.hudMetricUnit)
                    .foregroundStyle(Palette.uiInk2.color)
            }
        }
    }
}

private struct Caption: View {
    let text: String

    init(_ text: String) {
        self.text = text
    }

    var body: some View {
        Text(verbatim: text)
            .font(.caption)
            .foregroundStyle(Palette.uiInk2.color)
    }
}

// MARK: - Стекло

private struct GlassSection: View {
    @Binding var player: PlayerColor
    @State private var layer = LayerChoice.capture
    @State private var starts = 0

    var body: some View {
        Section {
            ZStack {
                LabMap()
                VStack(spacing: 0) {
                    Picker("Слой карты", selection: $layer) {
                        ForEach(LayerChoice.allCases) { choice in
                            Text(choice.title).tag(choice)
                        }
                    }
                    .pickerStyle(.segmented)
                    .segmentedGlass()
                    .padding(.trailing, Metrics.mapButton + 8)
                    Spacer()
                    StartButton(player: player) {
                        starts += 1
                    } label: {
                        Label("Старт", systemImage: "figure.run")
                    }
                }
                .padding(Metrics.panelInset)
                .overlay(alignment: .topTrailing) {
                    GlassEffectContainer(spacing: 8) {
                        VStack(spacing: 8) {
                            MapGlassButton("Слои карты", systemImage: "map.fill") {}
                            MapGlassButton("Где я", systemImage: "location.fill") {}
                        }
                    }
                    .padding(Metrics.panelInset)
                }
            }
            .frame(height: 340)
            .listRowInsets(EdgeInsets())
            .sensoryFeedback(.start, trigger: starts)

            PlayerColorPicker(selection: $player)
            Text(verbatim: startInkSummary)
                .font(.footnote)
                .foregroundStyle(Palette.uiInk2.color)
        } header: {
            Text("Стекло — только слой управления")
        } footer: {
            Text(
                "«Старт» — единственная цветная кнопка: .glassProminent цвета игрока. Сегменты и кнопки карты — "
                    + "нейтральное стекло, соседние сливаются в GlassEffectContainer. На iOS 27 проверь оба края "
                    + "системного ползунка прозрачности (§6.2).")
        }
        .listRowBackground(Palette.uiCell.color)
    }

    /// «Текст на «Старте» — белый, контраст 3,4:1».
    private var startInkSummary: String {
        let ink = player.startInk == RGBA(0xFFFFFF) ? "белый" : "тёмный"
        let ratio = NumberText.decimal(player.base.contrastRatio(to: player.startInk), fractionDigits: 1)
        return "Текст на «Старте» — \(ink), контраст \(ratio):1"
    }
}

private enum LayerChoice: String, CaseIterable, Identifiable {
    case capture, explore

    var id: Self { self }

    var title: String {
        switch self {
        case .capture: "Захват"
        case .explore: "Исследование"
        }
    }
}

/// Кусок настоящей карты Apple под стеклом: кампус БрГТУ, стиль как у игры (PLAN.md, D4: `.muted`, без POI).
/// Карта не трогается — прокрутку получает список.
struct LabMap: View {
    var body: some View {
        let center = MapStressPreset.arena.center
        Map(
            initialPosition: .region(
                MKCoordinateRegion(
                    center: CLLocationCoordinate2D(latitude: center.latitude, longitude: center.longitude),
                    latitudinalMeters: 500, longitudinalMeters: 500)),
            interactionModes: []
        )
        .mapStyle(.standard(elevation: .flat, emphasis: .muted, pointsOfInterest: .excludingAll))
        .allowsHitTesting(false)
    }
}

private struct PlayerColorPicker: View {
    @Binding var selection: PlayerColor

    var body: some View {
        LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 10), count: 6), spacing: 10) {
            ForEach(PlayerColor.allCases) { color in
                Button {
                    selection = color
                } label: {
                    Circle()
                        .fill(color.color)
                        .frame(width: 36, height: 36)
                        .overlay {
                            if selection == color {
                                Image(systemName: "checkmark")
                                    .font(.headline.weight(.bold))
                                    .foregroundStyle(color.startInkColor)
                            }
                        }
                }
                .buttonStyle(.plain)
                .accessibilityLabel(color.title)
                .accessibilityAddTraits(selection == color ? .isSelected : [])
            }
        }
        .padding(.vertical, 6)
    }
}

// MARK: - Редкости

private struct RaritySection: View {
    @State private var showsFind = false

    var body: some View {
        Section {
            HStack(alignment: .top, spacing: 0) {
                ForEach(BadgeMetal.allCases, id: \.self) { metal in
                    VStack(spacing: 6) {
                        RarityBadge(metal, systemImage: metal.symbolName, size: 58)
                        Text(metal.rarityTitle)
                            .font(.caption.weight(.semibold))
                        Text(metal.shapeTitle)
                            .font(.caption2)
                            .foregroundStyle(Palette.uiInk2.color)
                    }
                    .frame(maxWidth: .infinity)
                    .accessibilityElement(children: .combine)
                }
            }
            .padding(.vertical, 6)
            HStack(spacing: 14) {
                RarityBadge(.silver, systemImage: "questionmark", found: false, size: 44)
                Text("Ненайденный — пунктирный силуэт с загадкой, она яснеет, когда открываешь туман рядом.")
                    .font(.subheadline)
                    .foregroundStyle(Palette.uiInk2.color)
            }
            ForEach(BadgeMetal.allCases, id: \.self) { metal in
                HStack(spacing: 12) {
                    Text(metal.rarityTitle)
                        .font(.subheadline)
                        .frame(width: 110, alignment: .leading)
                    MetalProgress(metal, fraction: Double(metal.sampleFound) / Double(metal.sampleTotal))
                    Text(verbatim: "\(metal.sampleFound) / \(metal.sampleTotal)")
                        .font(.subheadline.monospacedDigit())
                        .foregroundStyle(Palette.uiInk2.color)
                }
                .accessibilityElement(children: .combine)
            }
            Button("Показать находку", systemImage: "sparkles") {
                // Своего перехода у церемонии нет: она сама — анимация (docs/design/tokens.md, §7).
                var transaction = Transaction()
                transaction.disablesAnimations = true
                withTransaction(transaction) {
                    showsFind = true
                }
            }
            .fullScreenCover(isPresented: $showsFind) {
                FindCeremonyDemo(metal: .gold, systemImage: BadgeMetal.gold.symbolName, caption: "Эпический · золото")
            }
        } header: {
            Text("Редкости — форма и металл")
        } footer: {
            Text(
                "Отдельно от цветов игроков (§6.5). Прогресс — пример: 3 / 20, 2 / 12, 1 / 6, 0 / 2. Находка: значок "
                    + "падает с перелётом, сияние цвета металла, блик — 1,6 с.")
        }
        .listRowBackground(Palette.uiCell.color)
    }
}

// MARK: - Цифры катятся

private struct NumericSection: View {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var points = 2_340

    var body: some View {
        Section {
            Text(CountText.score(points))
                .scaledFont(.seasonPoints)
                .contentTransition(.numericText(value: Double(points)))
            HStack(spacing: 12) {
                Button {
                    withAnimation(Motion.numericRoll.unlessReduceMotion(reduceMotion)) {
                        points += 96
                    }
                } label: {
                    Text(verbatim: "+" + CountText.score(96))
                }
                Button("Сбросить") {
                    withAnimation(Motion.numericRoll.unlessReduceMotion(reduceMotion)) {
                        points = 2_340
                    }
                }
            }
            .buttonStyle(.bordered)
        } header: {
            Text("Цифры катятся")
        } footer: {
            Text("numericText: разряды прокручиваются за 0,35 с. «Уменьшить движение» — цифры меняются сразу.")
        }
        .listRowBackground(Palette.uiCell.color)
    }
}

// MARK: - Подписи

extension PlayerColor {
    /// Название цвета для VoiceOver и «Лаборатории».
    fileprivate var title: String {
        switch self {
        case .red: "Красный"
        case .orange: "Оранжевый"
        case .sun: "Солнечный"
        case .lime: "Лайм"
        case .forest: "Лесной"
        case .mint: "Мятный"
        case .teal: "Бирюзовый"
        case .sky: "Небесный"
        case .blue: "Синий"
        case .violet: "Фиолетовый"
        case .magenta: "Пурпурный"
        case .coral: "Коралловый"
        }
    }
}

extension TerritoryRelation {
    fileprivate var title: String {
        switch self {
        case .mine: "Моё"
        case .clan: "Клан"
        case .rival: "Соперник"
        case .contested: "Спорное, трещина"
        case .lost: "Потеряно"
        case .noMansLand: "«Ничейные земли»"
        }
    }

    fileprivate var detail: String {
        switch self {
        case .mine: "сплошная кромка 2,4 pt, ночью — свечение"
        case .clan: "бледнее, штриховка 45°"
        case .rival: "бледнее, кромка 1,1 pt"
        case .contested: "бегущие муравьи по кромке"
        case .lost: "призрак 3 дня, пунктир"
        case .noMansLand: "серая, кромка 1 pt"
        }
    }
}

extension BadgeMetal {
    fileprivate var rarityTitle: String {
        switch self {
        case .bronze: "Обычный"
        case .silver: "Редкий"
        case .gold: "Эпический"
        case .holo: "Легендарный"
        }
    }

    fileprivate var shapeTitle: String {
        switch self {
        case .bronze: "бронза · круг"
        case .silver: "серебро · шестигранник"
        case .gold: "золото · огранка"
        case .holo: "голографика · звезда"
        }
    }

    /// Глиф образца.
    fileprivate var symbolName: String {
        switch self {
        case .bronze: "leaf.fill"
        case .silver: "drop.fill"
        case .gold: "lightbulb.fill"
        case .holo: "eye.fill"
        }
    }

    /// Пример прогресса «Коллекции» из макета.
    fileprivate var sampleFound: Int {
        switch self {
        case .bronze: 3
        case .silver: 2
        case .gold: 1
        case .holo: 0
        }
    }

    fileprivate var sampleTotal: Int {
        switch self {
        case .bronze: 20
        case .silver: 12
        case .gold: 6
        case .holo: 2
        }
    }
}
