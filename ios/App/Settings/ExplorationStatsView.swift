import DesignSystem
import GameCore
import GorodkiAPI
import Networking
import Observation
import SwiftUI
import Sync

/// «Исследование — статистика» (PLAN.md, §5, экран 15; §3.10): гектары и клетки за всё время и за сезон из
/// `GET /fog/summary` и `/seasons`; «% Бреста» и районы — когда сервер их считает (контракт E9), иначе «появится позже».
/// Числа собирает `ExplorationSummary` (пакет Sync, тесты на Linux).
@MainActor
@Observable
final class ExplorationStatsModel {
    private(set) var phase = LoadPhase.loading
    private(set) var summary: ExplorationSummary?
    private(set) var attempt = 0
    @ObservationIgnored private let api: (any APIProtocol)?

    /// - Parameters:
    ///   - api: клиент вошедшего игрока; `nil` — сервера нет (или режим фикстур с готовой `summary`).
    init(api: (any APIProtocol)?, summary: ExplorationSummary? = nil, failure: RequestFailure? = nil) {
        self.api = api
        self.summary = summary
        if let failure {
            phase = .failed(failure)
        } else if summary != nil {
            phase = .loaded
        } else if api == nil {
            phase = .failed(.notConfigured)
        }
    }

    func load() async {
        guard let api else { return }
        if case .failed = phase {
            attempt += 1
            phase = .loading
        }
        do {
            let fog: Components.Schemas.FogSummaryResponse
            switch try await api.getFogSummary() {
            case .ok(let ok): fog = try ok.body.json
            case .undocumented(let status, _): throw AccountServiceError.unexpectedStatus(status)
            }
            // Сезоны — только имена: не пришли — «Сезон N».
            let seasons = try? await api.getSeasons().ok.body.json
            summary = ExplorationSummary(summary: fog, seasons: seasons)
            phase = .loaded
        } catch {
            guard !Task.isCancelled else { return }
            phase = .failed(RequestFailure(error))
        }
    }
}

/// Экран статистики: герой — «% Бреста» или гектары, периоды, районы, достижения «скоро».
struct ExplorationStatsView: View {
    @State var model: ExplorationStatsModel
    /// Цифры накатываются при появлении (tokens.md, §7, «Цифры катятся»); «Уменьшить движение» — сразу итог.
    @State private var shown = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        content
            .background(Palette.uiBackground.color)
            .navigationTitle("Исследование")
            .task {
                if model.phase == .loading { await model.load() }
                withAnimation(Motion.numericAppear.unlessReduceMotion(reduceMotion)) { shown = true }
            }
            .refreshable { await model.load() }
    }

    @ViewBuilder
    private var content: some View {
        switch model.phase {
        case .loading:
            ContentLoadingView("Считаю открытое…")
        case .failed(let failure):
            ContentStateView(failure, attempt: model.attempt) {
                Task { await model.load() }
            }
        case .loaded:
            if let summary = model.summary {
                ScrollView {
                    VStack(alignment: .leading, spacing: 16) {
                        hero(summary)
                        if summary.allTime.cells == 0 {
                            ContentStateView(
                                "Туман ещё не открыт", systemImage: "cloud.fog",
                                message: "Туман открывается на забеге: 25 м вокруг пути. Нажми «Старт» на карте.",
                                style: .card)
                        }
                        periods(summary)
                        districts(summary.allTime)
                        soon
                    }
                    .padding(20)
                }
            }
        }
    }

    // MARK: - Блоки

    /// Главное число: «% Бреста», пока его нет — гектары за всё время.
    private func hero(_ summary: ExplorationSummary) -> some View {
        let period = summary.allTime
        return VStack(alignment: .leading, spacing: 8) {
            Text("За всё время · слой «\(summary.layer.title)»")
                .textCase(.uppercase)
                .font(.caption.weight(.semibold))
                .tracking(0.8)
                .foregroundStyle(Palette.uiInk2.color)
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(heroValue(period))
                    .font(.role(.seasonPoints))
                    .foregroundStyle(Palette.uiInk.color)
                    .contentTransition(.numericText(value: shown ? period.squareMeters : 0))
                Text(period.brestPercent == nil ? "открыто" : "Бреста открыто")
                    .font(.headline)
                    .foregroundStyle(Palette.uiInk2.color)
            }
            Text(heroDetail(period))
                .font(.subheadline)
                .foregroundStyle(Palette.uiInk2.color)
            ProgressView(value: shown ? min((period.brestPercent ?? 0) / 100, 1) : 0)
                .tint(FogStyle.exploreFill.color)
                .opacity(period.brestPercent == nil ? 0.35 : 1)
                .accessibilityHidden(true)
        }
        .contentCard(padding: 20)
        .accessibilityElement(children: .combine)
    }

    private func heroValue(_ period: ExplorationSummary.Period) -> String {
        guard shown else { return period.brestPercent == nil ? ExplorationText.area(0) : ExplorationText.percent(0) }
        return period.brestPercent.map(ExplorationText.percent) ?? ExplorationText.area(period.squareMeters)
    }

    private func heroDetail(_ period: ExplorationSummary.Period) -> String {
        let cells = CountText.fogCells(period.cells)
        guard period.brestPercent != nil else {
            return "\(cells) · «% Бреста» \(ExplorationText.later)"
        }
        return "\(ExplorationText.area(period.squareMeters)) · \(cells)"
    }

    /// За всё время и по сезонам: гектары, клетки, процент (или «появится позже»).
    private func periods(_ summary: ExplorationSummary) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("По сезонам")
                .font(.headline)
                .foregroundStyle(Palette.uiInk.color)
            VStack(spacing: 0) {
                ForEach([summary.allTime] + summary.seasons) { period in
                    HStack(alignment: .firstTextBaseline, spacing: 12) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(period.title + (period.isCurrent ? " · идёт" : ""))
                                .font(.body)
                                .foregroundStyle(Palette.uiInk.color)
                            Text(CountText.fogCells(period.cells))
                                .font(.footnote)
                                .foregroundStyle(Palette.uiInk2.color)
                        }
                        Spacer(minLength: 8)
                        VStack(alignment: .trailing, spacing: 2) {
                            Text(shown ? ExplorationText.area(period.squareMeters) : ExplorationText.area(0))
                                .font(.body.weight(.semibold).monospacedDigit())
                                .foregroundStyle(Palette.uiInk.color)
                                .contentTransition(.numericText(value: shown ? period.squareMeters : 0))
                            Text(period.brestPercent.map(ExplorationText.percent) ?? "% — \(ExplorationText.later)")
                                .font(.footnote)
                                .foregroundStyle(Palette.uiInk2.color)
                        }
                    }
                    .padding(.horizontal, 16)
                    .frame(minHeight: 60)
                    .accessibilityElement(children: .combine)
                    if period.id != (summary.seasons.last ?? summary.allTime).id {
                        Divider().padding(.leading, 16)
                    }
                }
            }
            .background(Palette.uiCell.color, in: .rect(cornerRadius: Radius.card))
        }
    }

    /// Районы и Арена — доли открытого; нет у сервера — одна строка «появится позже».
    private func districts(_ period: ExplorationSummary.Period) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Районы")
                .font(.headline)
                .foregroundStyle(Palette.uiInk.color)
            if let shares = period.districts, !shares.isEmpty {
                VStack(spacing: 14) {
                    ForEach(shares) { share in
                        VStack(alignment: .leading, spacing: 6) {
                            HStack {
                                Text(share.name + (share.proposal ? " · граница — предложение" : ""))
                                    .font(.subheadline)
                                    .foregroundStyle(Palette.uiInk.color)
                                Spacer(minLength: 8)
                                Text(ExplorationText.percent(share.percent))
                                    .font(.subheadline.weight(.semibold).monospacedDigit())
                                    .foregroundStyle(Palette.uiInk.color)
                            }
                            ProgressView(value: shown ? min(share.percent / 100, 1) : 0)
                                .tint(FogStyle.exploreFill.color)
                                .accessibilityHidden(true)
                        }
                        .accessibilityElement(children: .combine)
                    }
                }
                .contentCard()
            } else {
                Label(
                    "Ленинский и Московский районы, Арена БрГТУ — \(ExplorationText.later): сервер начнёт считать "
                        + "доли, когда подключит карту OSM.",
                    systemImage: "map"
                )
                .font(.subheadline)
                .foregroundStyle(Palette.uiInk2.color)
                .contentCard()
            }
        }
    }

    /// Достижения «Квадрат», «Все парки», «100 % Арены» — после полевого теста (§3.10).
    private var soon: some View {
        ContentStateView(
            "Достижения — скоро", systemImage: "rosette",
            message: "«Квадрат», «Открыл все парки», «100 % Арены» — пороги назначат после полевого теста.",
            style: .card)
    }
}
