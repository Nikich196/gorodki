import DesignSystem
import GameCore
import SwiftUI
import Sync
import UIKit

/// Вкладка «Карта» (PLAN.md, §5, экраны 4–5; docs/design/tokens.md, §8): слой «Захват | Исследование», лига
/// «Бег | Вело» и окраска земли на стекле сверху, «Где я» справа, «Старт» — единственная цветная кнопка внизу (`RunStartButton`: лист «Новый забег» с подсказками
/// к разрешениям, затем HUD), лист участка по касанию.
struct MapScreen: View {
    @Bindable var model: MapModel
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        GameMapView(model: model)
            .ignoresSafeArea()
            .overlay(alignment: .top) {
                MapControls(model: model)
                    .padding(.horizontal, Metrics.panelInset)
                    .padding(.top, 8)
            }
            .safeAreaInset(edge: .bottom) {
                // «Старт» — экраны забега: лист «Новый забег» с подсказками к разрешениям, HUD (`RunStartButton`).
                RunStartButton(player: model.player)
                    .padding(.bottom, 12)
            }
            .sheet(item: $model.selection) { _ in
                if let sheet = model.sheet, let selection = model.selection {
                    ParcelSheet(
                        content: sheet, color: PlayerColor(index: selection.parcel.colorIndex),
                        animation: Motion.numericRoll.unlessReduceMotion(reduceMotion)
                    )
                    .presentationDetents([.height(320), .large])
                    .presentationBackgroundInteraction(.enabled(upThrough: .height(320)))
                    .presentationBackground(Palette.uiBackground.color)
                }
            }
            .onChange(of: model.layer) { model.layerChanged() }
            .sensoryFeedback(.selection, trigger: model.selection?.id)
            .modifier(MapLocationPrompts(model: model))
            .task {
                // Раз в минуту: истёкшие зоны «спорная» уходят с карты, кэши получают шанс на запасной опрос.
                while !Task.isCancelled {
                    try? await Task.sleep(for: .seconds(MapModel.tickInterval))
                    guard !Task.isCancelled else { return }
                    model.tick()
                }
            }
    }
}

/// Стекло над картой: слой и под ним — окраска земли («Захват») или сводка тумана («Исследование»). Соседние
/// стеклянные элементы — в одном `GlassEffectContainer`.
private struct MapControls: View {
    @Bindable var model: MapModel
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    /// Строка окраски и карточка «Исследование» — одно стекло, которое перетекает при смене слоя.
    @Namespace private var glass

    var body: some View {
        GlassEffectContainer(spacing: 8) {
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 8) {
                    Picker("Слой карты", selection: $model.layer) {
                        ForEach(MapLayer.allCases) { layer in
                            Text(layer.title).tag(layer)
                        }
                    }
                    .pickerStyle(.segmented)
                    .segmentedGlass()
                    LeaguePicker(model: model)
                }
                HStack(alignment: .top, spacing: 8) {
                    switch model.layer {
                    case .capture:
                        Picker("Окраска земли", selection: $model.coloring) {
                            ForEach(LandColoring.allCases) { coloring in
                                Text(coloring.title).tag(coloring)
                            }
                        }
                        .pickerStyle(.segmented)
                        .padding(3)
                        .frame(height: Metrics.modeRowHeight)
                        .capsuleGlass()
                        .glassEffectID("layer-detail", in: glass)
                        .frame(maxWidth: 280)
                        Spacer(minLength: 0)
                    case .explore:
                        ExploreCard(
                            squareMeters: model.profile.exploredSquareMeters,
                            brestPercent: model.profile.exploration?.allTime.brestPercent, glass: glass)
                    }
                    MapGlassButton("Где я", systemImage: "location.fill") { model.locateTapped() }
                }
                if model.bikeHintShown {
                    Label("«Вело» — с Сезона 1: своя лига, своя земля и свой туман", systemImage: "lock.fill")
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(Palette.uiInk.color)
                        .padding(.horizontal, 16)
                        .padding(.vertical, 10)
                        .capsuleGlass()
                        .transition(.opacity)
                }
            }
        }
        .animation(Motion.layerSwitch.unlessReduceMotion(reduceMotion), value: model.layer)
        .animation(Motion.layerSwitch.unlessReduceMotion(reduceMotion), value: model.bikeHintShown)
        .task(id: model.bikeHintShown) {
            // Пояснение уходит само через 3 с.
            guard model.bikeHintShown else { return }
            try? await Task.sleep(for: .seconds(3))
            if !Task.isCancelled { model.bikeHintShown = false }
        }
    }
}

/// Лига карты «Бег | Вело» на стекле (tokens.md, §8, `LeaguePicker`): «Вело» — с замком до Сезона 1, нажатие —
/// пояснение. Выбранное — нейтральное, как выбранный сегмент (tokens.md, §6, п. 3).
private struct LeaguePicker: View {
    let model: MapModel

    var body: some View {
        HStack(spacing: 2) {
            Button {
                model.select(.run)
            } label: {
                Image(systemName: "figure.run")
                    .font(.body.weight(.semibold))
                    .frame(width: 44, height: 38)
                    .background(Palette.uiCell.color.opacity(0.7), in: .capsule)
            }
            .accessibilityLabel(Text("Лига «Бег»"))
            .accessibilityValue(Text("выбрано"))
            Button {
                model.select(.bike)
            } label: {
                Image(systemName: "bicycle")
                    .font(.body.weight(.semibold))
                    .opacity(0.42)
                    .overlay(alignment: .bottomTrailing) {
                        Image(systemName: "lock.fill")
                            .font(.system(size: 9, weight: .bold))
                            .offset(x: 6, y: 2)
                    }
                    .frame(width: 44, height: 38)
            }
            .accessibilityLabel(Text("Лига «Вело»"))
            .accessibilityValue(Text("недоступно до Сезона 1"))
        }
        .buttonStyle(.plain)
        .foregroundStyle(Palette.uiInk.color)
        .padding(4)
        .frame(height: Metrics.segmentHeight)
        .capsuleGlass(interactive: true)
        .sensoryFeedback(.selection, trigger: model.bikeHintShown)
    }
}

/// «Где я» без разрешения: подсказка перед системным запросом (одна кнопка «Продолжить», PLAN.md, §6.6) или путь
/// в Настройки, если геопозиция запрещена.
private struct MapLocationPrompts: ViewModifier {
    @Bindable var model: MapModel
    @Environment(\.openURL) private var openURL

    func body(content: Content) -> some View {
        content
            .confirmationDialog("Показать, где ты?", isPresented: $model.locationPrimerShown, titleVisibility: .visible)
        {
            Button("Продолжить") {
                Task { await model.locationPrimerAccepted() }
            }
            Button("Не сейчас", role: .cancel) {}
        } message: {
            Text(
                "Карта покажет твоё место. Та же геопозиция нужна забегу — выбери «При использовании», "
                    + "разрешение «Всегда» игре не нужно.")
        }
            .alert("Геопозиция выключена", isPresented: $model.locationDeniedShown) {
                Button("Открыть Настройки") {
                    if let url = URL(string: UIApplication.openSettingsURLString) {
                        openURL(url)
                    }
                }
                Button("Отмена", role: .cancel) {}
            } message: {
                Text("Включи её для Городков: «Конфиденциальность → Службы геолокации → Городки → При использовании».")
            }
    }
}

/// Карточка «Исследование» на стекле: сколько тумана открыто за всё время (`GET /fog/summary`, слой «Пешком»).
/// Процент Бреста и «+N га сегодня» — после маски «достижимого» (fog.md, «Что дальше»).
private struct ExploreCard: View {
    let squareMeters: Double?
    /// «% Бреста» из той же сводки (поля E9); `nil` — сервер ещё не считает.
    let brestPercent: Double?
    let glass: Namespace.ID

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: "cloud.fog.fill")
                .font(.title3)
                .foregroundStyle(FogStyle.exploreFill.color)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text(verbatim: summary)
                    .font(.headline.monospacedDigit())
                    .foregroundStyle(Palette.uiInk.color)
                Text("Слой «Пешком» · открывается на забеге")
                    .font(.caption)
                    .foregroundStyle(Palette.uiInk2.color)
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
        .glassEffect(.regular, in: .rect(cornerRadius: Radius.plaque))
        .glassEffectID("layer-detail", in: glass)
    }

    /// «Открыто 4,26 га»; сводка ещё не пришла — так и сказать.
    private var summary: String {
        guard let squareMeters else { return "Открытое загрузится с профилем" }
        let area = NumberText.hectares(fromSquareMeters: squareMeters, fractionDigits: 2)
        guard let brestPercent else { return "Открыто " + area }
        return "Открыто " + ExplorationText.percent(brestPercent) + " Бреста · " + area
    }
}

/// Лист участка — контентный слой, без стекла (docs/design/tokens.md, §6, п. 2): владелец, отношение и строки
/// из ответа `/territory`.
struct ParcelSheet: View {
    let content: ParcelSheetContent
    let color: PlayerColor
    /// Цифры строк перекатываются при выборе другого участка (`Motion.numericRoll`); `nil` — «Уменьшить движение».
    let animation: Animation?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                HStack(spacing: 14) {
                    Circle()
                        .fill(color.color)
                        .overlay { Circle().stroke(color.edgeColor, lineWidth: 3) }
                        .frame(width: 40, height: 40)
                        .accessibilityHidden(true)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(verbatim: content.title)
                            .font(.title2.bold())
                            .foregroundStyle(Palette.uiInk.color)
                        Text(verbatim: content.subtitle)
                            .font(.subheadline)
                            .foregroundStyle(Palette.uiInk2.color)
                    }
                }
                VStack(spacing: 0) {
                    ForEach(content.rows) { row in
                        HStack(alignment: .firstTextBaseline, spacing: 12) {
                            Text(verbatim: row.title)
                                .font(.body)
                                .foregroundStyle(Palette.uiInk2.color)
                            Spacer(minLength: 8)
                            Text(verbatim: row.value)
                                .font(.body.weight(.semibold).monospacedDigit())
                                .foregroundStyle(Palette.uiInk.color)
                                .multilineTextAlignment(.trailing)
                                .contentTransition(.numericText())
                        }
                        .padding(.horizontal, 16)
                        .frame(minHeight: 48)
                        .accessibilityElement(children: .combine)
                        if row != content.rows.last {
                            Divider().padding(.leading, 16)
                        }
                    }
                }
                .background(Palette.uiCell.color, in: .rect(cornerRadius: Radius.card))
            }
            .padding(20)
            .animation(animation, value: content)
        }
    }
}
