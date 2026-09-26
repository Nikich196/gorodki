import DesignSystem
import GameCore
import MapKit
import SwiftUI
import Sync

/// Итог забега и его детали (PLAN.md, §5, экраны 8 и 10; docs/architecture/run-hud.md, «Итог забега»): числа
/// накатываются, след рисуется, карточки въезжают при прокрутке. У каждого числа — источник: оценка телефона («≈»),
/// итог сервера или «позже». Контентный слой — без стекла (tokens.md, §6).
struct RunResultView: View {
    let model: RunResultModel
    /// «Готово» у итога сразу после «Финиша»; у деталей из истории — `nil` (назад — системной кнопкой).
    var done: (() -> Void)?
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.runScreens) private var run
    @State private var appeared = false

    /// След — кромкой цвета игрока (tokens.md, §3); вне оболочки (фикстуры деталей) — нейтральным.
    private var trailColor: Color { run?.player.edgeColor ?? Palette.uiInk.color }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                header
                if model.justFinished {
                    TrackCard(track: model.track, color: trailColor, appeared: appeared, reduceMotion: reduceMotion)
                        .resultCard()
                } else {
                    TrackMapCard(track: model.track, color: trailColor)
                        .resultCard()
                }
                if let readout = model.readout {
                    StatsCard(readout: readout, appeared: appeared)
                        .resultCard()
                    CaptureCard(readout: readout, appeared: appeared)
                        .resultCard()
                    if !readout.claims.isEmpty {
                        ClaimsCard(readout: readout)
                            .resultCard()
                    }
                    ServerCard(readout: readout)
                        .resultCard()
                    if !readout.breaks.isEmpty {
                        RowsCard(title: "Разрывы следа", rows: readout.breaks)
                            .resultCard()
                    }
                } else if let entry = model.entry {
                    Text(
                        "Забег длился \(NumberText.clock(seconds: Double(entry.endedAtMs - entry.startedAtMs) / 1_000)). "
                            + "Подробный итог хранится, пока забег в очереди на телефоне."
                    )
                    .font(.subheadline)
                    .foregroundStyle(Palette.uiInk2.color)
                    .contentCard()
                }
                if !model.justFinished {
                    gpxButton
                }
            }
            .padding(16)
        }
        .background(Palette.uiBackground.color)
        .navigationTitle(model.justFinished ? "Итог забега" : "Детали забега")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            if let done {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Готово", action: done)
                }
            }
        }
        .task {
            // Сначала сохранённое — числа накатываются уже по нему, потом сервер.
            await model.loadSaved()
            if reduceMotion {
                appeared = true
            } else {
                withAnimation(Motion.numericAppear) { appeared = true }
            }
            await model.refreshFromServer()
        }
        .task {
            if !model.justFinished { await model.prepareGPX() }
        }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                Text(model.readout?.leagueTitle ?? "Забег")
                    .capsLabel()
                if model.readout?.isReplay == true {
                    Text("Повтор").capsLabel()
                }
            }
            if let startedAt = model.startedAt {
                Text(startedAt.formatted(.dateTime.day().month(.wide).hour().minute().locale(NumberText.locale)))
                    .font(.largeTitle.bold())
                    .foregroundStyle(Palette.uiInk.color)
            }
            if let readout = model.readout {
                Label(readout.readinessText, systemImage: readinessSymbol(readout.readiness))
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Palette.uiInk2.color)
                if readout.endedAtLimit {
                    Text("Забег закончился сам: вышел предел длины.")
                        .font(.footnote)
                        .foregroundStyle(Palette.uiInk2.color)
                }
            }
        }
    }

    private func readinessSymbol(_ readiness: RunResult.Readiness) -> String {
        switch readiness {
        case .waitingForNetwork: "wifi.slash"
        case .computing: "hourglass"
        case .ready: "checkmark.seal.fill"
        }
    }

    @ViewBuilder
    private var gpxButton: some View {
        if let url = model.gpxURL {
            ShareLink(item: url) {
                Label("Экспорт GPX", systemImage: "square.and.arrow.up")
            }
            .buttonStyle(.neutral)
        } else if model.entry != nil || model.readout != nil {
            Label("След для GPX хранится в истории забегов", systemImage: "info.circle")
                .font(.footnote)
                .foregroundStyle(Palette.uiInk2.color)
        }
    }
}

extension View {
    /// Карточка итога въезжает при прокрутке.
    fileprivate func resultCard() -> some View {
        scrollTransition(.animated(Motion.numericRoll)) { content, phase in
            content
                .opacity(phase.isIdentity ? 1 : 0.4)
                .offset(y: phase.isIdentity ? 0 : 24)
                .scaleEffect(phase.isIdentity ? 1 : 0.97)
        }
    }
}

/// След без карты — рисуется `trim`-ом, как будто его пробегают заново.
private struct TrackCard: View {
    let track: [Coordinate]
    let color: Color
    let appeared: Bool
    let reduceMotion: Bool

    var body: some View {
        ZStack {
            if track.count >= 2 {
                TrackShape(coordinates: track)
                    .trim(from: 0, to: appeared ? 1 : 0)
                    .stroke(
                        Palette.trailCase.color,
                        style: StrokeStyle(lineWidth: TrailStyle.caseWidth, lineCap: .round, lineJoin: .round))
                TrackShape(coordinates: track)
                    .trim(from: 0, to: appeared ? 1 : 0)
                    .stroke(
                        color, style: StrokeStyle(lineWidth: TrailStyle.width - 1.5, lineCap: .round, lineJoin: .round))
            } else {
                Label(
                    "След появится, когда забег сохранится",
                    systemImage: "point.topleft.down.to.point.bottomright.curvepath"
                )
                .font(.subheadline)
                .foregroundStyle(Palette.uiInk2.color)
            }
        }
        .animation(reduceMotion ? nil : .easeInOut(duration: 1.4), value: appeared)
        .frame(maxWidth: .infinity)
        .frame(height: 200)
        .background(Palette.uiCell.color, in: .rect(cornerRadius: Radius.card))
        .accessibilityHidden(true)
    }
}

/// След на карте — детали забега.
private struct TrackMapCard: View {
    let track: [Coordinate]
    let color: Color

    var body: some View {
        Group {
            if let region = TrackGeometry.region(track, padding: 1.3) {
                Map(initialPosition: .region(region), interactionModes: [.pan, .zoom]) {
                    MapPolyline(coordinates: track.map(\.location))
                        .stroke(
                            Palette.trailCase.color,
                            style: StrokeStyle(lineWidth: TrailStyle.caseWidth, lineCap: .round, lineJoin: .round))
                    MapPolyline(coordinates: track.map(\.location))
                        .stroke(
                            color, style: StrokeStyle(lineWidth: TrailStyle.width, lineCap: .round, lineJoin: .round))
                }
                .mapStyle(.standard(elevation: .flat, emphasis: .muted, pointsOfInterest: .excludingAll))
            } else {
                Label("След не сохранился", systemImage: "map")
                    .font(.subheadline)
                    .foregroundStyle(Palette.uiInk2.color)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .background(Palette.uiCell2.color)
            }
        }
        .frame(height: 240)
        .clipShape(.rect(cornerRadius: Radius.card))
    }
}

/// Три метрики — накатываются при появлении.
private struct StatsCard: View {
    let readout: RunResultReadout
    let appeared: Bool

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 0) {
            BigNumber(value: appeared ? readout.distanceText : "0,00", unit: "км", title: "Дистанция")
            BigNumber(value: appeared ? readout.durationText : "0:00", unit: nil, title: "Время")
            BigNumber(value: appeared ? readout.paceText : "0:00", unit: NumberText.paceUnit, title: "Темп")
        }
        .contentCard()
    }
}

private struct BigNumber: View {
    let value: String
    let unit: String?
    let title: String

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(alignment: .firstTextBaseline, spacing: 2) {
                Text(value)
                    .font(.role(.statTile))
                    .contentTransition(.numericText())
                if let unit {
                    Text(unit)
                        .font(.role(.hudMetricUnit))
                        .foregroundStyle(Palette.uiInk2.color)
                }
            }
            .lineLimit(1)
            .minimumScaleFactor(0.6)
            Text(title)
                .font(.caption)
                .foregroundStyle(Palette.uiInk2.color)
        }
        .foregroundStyle(Palette.uiInk.color)
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
    }
}

/// Захват: «≈» телефона и «взятое» сервера — рядом, не смешиваясь; забег отвергнут — об этом вместо площадей.
private struct CaptureCard: View {
    let readout: RunResultReadout
    let appeared: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Захват")
                .font(.headline)
            if let rejection = readout.rejection {
                Label(rejection, systemImage: "xmark.octagon.fill")
                    .font(.subheadline)
            } else if readout.loops == 0 {
                Text("Петель не было — туман открывался, земля не захватывалась.")
                    .font(.subheadline)
                    .foregroundStyle(Palette.uiInk2.color)
            } else {
                HStack(alignment: .firstTextBaseline, spacing: 16) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(appeared ? (readout.takenText ?? RunResultReadout.later) : "+0,00")
                            .font(.role(.seasonPoints))
                            .contentTransition(.numericText())
                            .lineLimit(1)
                            .minimumScaleFactor(0.5)
                        Text(readout.takenText == nil ? "итог сервера" : "взято · итог сервера")
                            .font(.caption)
                            .foregroundStyle(Palette.uiInk2.color)
                    }
                    Spacer(minLength: 0)
                    VStack(alignment: .trailing, spacing: 2) {
                        Text(readout.estimateText)
                            .font(.title3.weight(.bold).monospacedDigit())
                        Text("оценка телефона · \(CountText.loops(readout.loops))")
                            .font(.caption)
                            .foregroundStyle(Palette.uiInk2.color)
                    }
                }
            }
        }
        .foregroundStyle(Palette.uiInk.color)
        .contentCard()
    }
}

/// Заявки петель с решениями.
private struct ClaimsCard: View {
    let readout: RunResultReadout

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text("Петли")
                .font(.headline)
                .padding(.bottom, 6)
            ForEach(readout.claims) { claim in
                HStack(alignment: .top, spacing: 12) {
                    Image(systemName: Self.symbolName(claim.state))
                        .font(.headline)
                        .foregroundStyle(claim.state == .refused ? Palette.uiInk3.color : Palette.uiInk.color)
                        .frame(width: 24)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(claim.title + " · " + claim.estimate)
                            .font(.subheadline.weight(.semibold))
                        Text(
                            claim.state == .applied
                                ? CeremonyStatus.confirmed(claim.takenSquareMeters ?? 0) : claim.status
                        )
                        .font(.caption)
                        .foregroundStyle(Palette.uiInk2.color)
                    }
                    Spacer(minLength: 0)
                }
                .padding(.vertical, 8)
                .accessibilityElement(children: .combine)
                if claim.id != readout.claims.last?.id {
                    Divider()
                }
            }
        }
        .foregroundStyle(Palette.uiInk.color)
        .contentCard()
    }

    static func symbolName(_ state: RunResultReadout.ClaimRow.State) -> String {
        switch state {
        case .waiting: "hourglass"
        case .applied: "checkmark.seal.fill"
        case .refused: "xmark.octagon"
        }
    }
}

/// То, что сервер решает позже: туман, визиты, разбивка по видам (после границы публичности — «позже»).
private struct ServerCard: View {
    let readout: RunResultReadout

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Туман и участки")
                .font(.headline)
            Row(title: "Туман · оценка телефона", value: readout.fogEstimateText)
            Row(title: "Туман · итог сервера", value: readout.fogServerText ?? RunResultReadout.later)
            Row(
                title: "Освежено участков",
                value: readout.visitedParcels.map(CountText.parcels) ?? RunResultReadout.later)
            if readout.loops > 0, readout.rejection == nil {
                Divider()
                Text("Площадь по видам")
                    .font(.subheadline.weight(.semibold))
                if let breakdown = readout.breakdown {
                    ForEach(breakdown) { row in
                        Row(title: row.title, value: row.value)
                    }
                }
                if readout.breakdownPending {
                    Text(
                        "Разбивка — позже: сервер покажет её, когда захват станет виден всем (20–25 минут после "
                            + "петли)."
                    )
                    .font(.footnote)
                    .foregroundStyle(Palette.uiInk2.color)
                }
            }
        }
        .foregroundStyle(Palette.uiInk.color)
        .contentCard()
    }

    private struct Row: View {
        let title: String
        let value: String

        var body: some View {
            HStack(alignment: .firstTextBaseline) {
                Text(title)
                    .font(.subheadline)
                    .foregroundStyle(Palette.uiInk2.color)
                Spacer(minLength: 8)
                Text(value)
                    .font(.subheadline.weight(.semibold).monospacedDigit())
                    .foregroundStyle(
                        value == RunResultReadout.later ? Palette.uiInk3.color : Palette.uiInk.color)
            }
            .accessibilityElement(children: .combine)
        }
    }
}

/// Строки «название — значение».
private struct RowsCard: View {
    let title: String
    let rows: [RunResultReadout.Row]

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title)
                .font(.headline)
            ForEach(rows) { row in
                HStack {
                    Text(row.title)
                        .font(.subheadline)
                        .foregroundStyle(Palette.uiInk2.color)
                    Spacer()
                    Text(row.value)
                        .font(.subheadline.weight(.semibold).monospacedDigit())
                }
            }
        }
        .foregroundStyle(Palette.uiInk.color)
        .contentCard()
    }
}

// MARK: - История

/// История забегов (PLAN.md, §5: детали забега — отсюда): новые первыми; строка раскрывается в детали zoom-переходом.
/// Модель — во владении экрана: перерисовка родителя не подменяет её пустой.
struct RunHistoryView: View {
    @State private var model: RunHistoryModel
    @Namespace private var zoom

    init(model: RunHistoryModel) {
        _model = State(initialValue: model)
    }

    var body: some View {
        ScrollView {
            LazyVStack(spacing: 10) {
                if model.loaded, model.rows.isEmpty {
                    ContentUnavailableView(
                        "Забегов пока нет", systemImage: "figure.run",
                        description: Text("Первый забег — «Старт» на карте."))
                }
                ForEach(model.rows) { row in
                    NavigationLink(value: row.id) {
                        HistoryRowView(row: row)
                    }
                    .buttonStyle(.plain)
                    .matchedTransitionSource(id: row.id, in: zoom)
                    .scrollTransition(.animated(Motion.numericRoll)) { content, phase in
                        content
                            .opacity(phase.isIdentity ? 1 : 0.4)
                            .offset(y: phase.isIdentity ? 0 : 16)
                    }
                }
            }
            .padding(16)
        }
        .background(Palette.uiBackground.color)
        .navigationTitle("Забеги")
        .navigationDestination(for: UUID.self) { id in
            RunDetailsScreen(model: model.details(id))
                .navigationTransition(.zoom(sourceID: id, in: zoom))
        }
        .task { await model.load() }
        .refreshable { await model.load() }
    }
}

/// Детали забега: модель — во владении экрана.
private struct RunDetailsScreen: View {
    @State private var model: RunResultModel

    init(model: RunResultModel) {
        _model = State(initialValue: model)
    }

    var body: some View {
        RunResultView(model: model)
    }
}

private struct HistoryRowView: View {
    let row: RunHistoryRow

    var body: some View {
        HStack(spacing: 14) {
            Image(systemName: row.league == .run ? "figure.run" : "bicycle")
                .font(.title3.weight(.semibold))
                .frame(width: 44, height: 44)
                .background(Palette.uiCell2.color, in: .circle)
            VStack(alignment: .leading, spacing: 3) {
                Text(row.startedAt.formatted(.dateTime.day().month(.wide).hour().minute().locale(NumberText.locale)))
                    .font(.headline)
                Text(row.summary + (row.isReplay ? " · повтор" : ""))
                    .font(.subheadline.monospacedDigit())
                    .foregroundStyle(Palette.uiInk2.color)
            }
            Spacer(minLength: 8)
            if let area = row.area {
                Text(area)
                    .font(.headline.monospacedDigit())
            }
            Image(systemName: "chevron.right")
                .font(.footnote.weight(.semibold))
                .foregroundStyle(Palette.uiInk3.color)
        }
        .foregroundStyle(Palette.uiInk.color)
        .contentCard(padding: 14)
        .accessibilityElement(children: .combine)
    }
}
