import DesignSystem
import GameCore
import SwiftUI

/// Вкладка «Карта» (PLAN.md, §5, экраны 3–5; docs/design/tokens.md, §8): слой «Захват | Исследование» и окраска земли
/// на стекле сверху, «Старт» — единственная цветная кнопка внизу, лист участка по касанию, подсказки перед системными
/// разрешениями. Экрана забега (HUD) ещё нет — «Старт» после подсказок говорит «Забег — скоро».
struct MapScreen: View {
    @Bindable var model: MapModel

    var body: some View {
        GameMapView(model: model)
            .ignoresSafeArea()
            .overlay(alignment: .top) {
                MapControls(model: model)
                    .padding(.horizontal, Metrics.panelInset)
                    .padding(.top, 8)
            }
            .safeAreaInset(edge: .bottom) {
                StartButton(player: model.player) {
                    model.selection = nil
                    model.start()
                } label: {
                    Label("Старт", systemImage: "figure.run")
                }
                .frame(width: 200)
                .padding(.bottom, 12)
                .sheet(item: $model.primer) { step in
                    PermissionPrimerView(step: step) {
                        Task { await model.primerContinue() }
                    }
                    .presentationDetents([.medium])
                }
            }
            .sheet(item: $model.selection) { _ in
                if let sheet = model.sheet, let selection = model.selection {
                    ParcelSheet(content: sheet, color: PlayerColor(index: selection.parcel.colorIndex))
                        .presentationDetents([.height(320), .large])
                        .presentationBackgroundInteraction(.enabled(upThrough: .height(320)))
                        .presentationBackground(Palette.uiBackground.color)
                }
            }
            .alert("Забег — скоро", isPresented: $model.startNoticeShown) {
                Button("Понятно", role: .cancel) {}
            } message: {
                Text(startNotice)
            }
            .onChange(of: model.layer) { model.layerChanged() }
            .task {
                // Раз в минуту: истёкшие зоны «спорная» уходят с карты, кэши получают шанс на запасной опрос.
                while !Task.isCancelled {
                    try? await Task.sleep(for: .seconds(MapModel.tickInterval))
                    guard !Task.isCancelled else { return }
                    model.tick()
                }
            }
    }

    private var startNotice: String {
        let hud = "Экран забега появится следующим шагом."
        return DebugAccess.buildAllows ? hud + " Пробный забег без сервера — в «Профиль → Отладка → Лаборатория»." : hud
    }
}

/// Стекло над картой: слой и под ним — окраска земли («Захват») или сводка тумана («Исследование»). Соседние
/// стеклянные элементы — в одном `GlassEffectContainer`.
private struct MapControls: View {
    @Bindable var model: MapModel

    var body: some View {
        GlassEffectContainer(spacing: 8) {
            VStack(spacing: 8) {
                Picker("Слой карты", selection: $model.layer) {
                    ForEach(MapLayer.allCases) { layer in
                        Text(layer.title).tag(layer)
                    }
                }
                .pickerStyle(.segmented)
                .segmentedGlass()
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
                    .frame(maxWidth: 280)
                case .explore:
                    ExploreCard(squareMeters: model.profile.exploredSquareMeters)
                }
            }
        }
    }
}

/// Карточка «Исследование» на стекле: сколько тумана открыто за всё время (`GET /fog/summary`, слой «Пешком»).
/// Процент Бреста и «+N га сегодня» — после маски «достижимого» (fog.md, «Что дальше»).
private struct ExploreCard: View {
    let squareMeters: Double?

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
                Text("Слой «Пешком» · туман открывается только на забеге")
                    .font(.caption)
                    .foregroundStyle(Palette.uiInk2.color)
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
        .glassEffect(.regular, in: .rect(cornerRadius: Radius.plaque))
    }

    /// «Открыто 4,26 га»; сводка ещё не пришла — так и сказать.
    private var summary: String {
        guard let squareMeters else { return "Открытое загрузится с профилем" }
        return "Открыто " + NumberText.hectares(fromSquareMeters: squareMeters, fractionDigits: 2)
    }
}

/// Лист участка — контентный слой, без стекла (docs/design/tokens.md, §6, п. 2): владелец, отношение и строки
/// из ответа `/territory`.
struct ParcelSheet: View {
    let content: ParcelSheetContent
    let color: PlayerColor

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
        }
    }
}

/// Подсказка перед системным запросом (PLAN.md, §5, экран 3; §6.6): зачем разрешение и одна кнопка «Продолжить».
/// Тексты — по смыслу строк Info.plist, которые покажет сама система.
struct PermissionPrimerView: View {
    let step: PermissionPrimer
    let onContinue: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Image(systemName: symbol)
                .font(.system(size: 40, weight: .semibold))
                .foregroundStyle(Palette.uiInk.color)
                .accessibilityHidden(true)
            Text(verbatim: title)
                .font(.title2.bold())
                .foregroundStyle(Palette.uiInk.color)
            Text(verbatim: text)
                .font(.body)
                .foregroundStyle(Palette.uiInk2.color)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
            Button("Продолжить", action: onContinue)
                .buttonStyle(.neutral)
        }
        .padding(24)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Palette.uiBackground.color)
    }

    private var symbol: String {
        switch step {
        case .location: "location.fill"
        case .motion: "figure.walk.motion"
        }
    }

    private var title: String {
        switch step {
        case .location: "Геопозиция — на время забега"
        case .motion: "Движение и фитнес"
        }
    }

    private var text: String {
        switch step {
        case .location:
            "Городки записывают твой след во время забега: так засчитывается захваченная земля и открывается туман. "
                + "Выбери «При использовании» — разрешение «Всегда» игре не нужно."
        case .motion:
            "Датчики движения отличают бег от поездки на транспорте — так игра остаётся честной. Без них забег "
                + "запишется и туман откроется, но захваты сервер не засчитает."
        }
    }
}
