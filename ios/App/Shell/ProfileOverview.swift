import DesignSystem
import GameCore
import Networking
import SwiftUI
import Sync

/// Верх «Профиля» (PLAN.md, §5, экран 12; docs/design/tokens.md, §8): шапка, сезон, четыре плитки и места того, что
/// сделает следующая волна на контрактах Егора, — «скоро». Только то, что отдаёт сервер: ник и цвет (`GET /me`), сезон
/// (`/seasons`), туман (`/fog/summary`), забеги и место (`/me/stats`). Контентный слой — без стекла.
struct ProfileOverview: View {
    let model: ProfileModel
    @Namespace private var zoom
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            header
            if let failure = model.loadFailure, model.signedIn {
                ContentStateView(failure, style: .card) {
                    Task { await model.refresh() }
                }
            }
            if model.signedIn {
                seasonCard
                tiles
            }
            soon
        }
        .animation(Motion.numericRoll.unlessReduceMotion(reduceMotion), value: model.exploredSquareMeters)
    }

    // MARK: - Шапка

    private var header: some View {
        HStack(spacing: 16) {
            Text(String((model.displayName ?? "?").prefix(1)))
                .font(.title.bold())
                .fontDesign(.rounded)
                .foregroundStyle(model.playerColor.startInkColor)
                .frame(width: 64, height: 64)
                .background(model.playerColor.color, in: .circle)
                .padding(4)
                .overlay { Circle().stroke(model.playerColor.edgeColor, lineWidth: 3) }
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 6) {
                Text(model.displayName ?? (model.signedIn ? "Игрок" : "Вход не выполнен"))
                    .font(.title2.bold())
                    .foregroundStyle(Palette.uiInk.color)
                Text(subtitle)
                    .font(.subheadline)
                    .foregroundStyle(Palette.uiInk2.color)
                if model.signedIn {
                    HStack(spacing: 6) {
                        tag("Лига «Бег»", systemImage: "figure.run")
                        if model.publicProfile == false {
                            tag("Ник скрыт", systemImage: "eye.slash")
                        }
                    }
                }
            }
        }
        .contentCard()
    }

    private var subtitle: String {
        guard model.signedIn else { return "Вкладки без входа — только для сборки команды" }
        switch model.role {
        case "admin": return "Администратор"
        case "demo": return "Демо-режим"
        default: return "Ник и цвет назначены автоматически"
        }
    }

    private func tag(_ title: String, systemImage: String) -> some View {
        Label(title, systemImage: systemImage)
            .font(.caption.weight(.semibold))
            .foregroundStyle(Palette.uiInk2.color)
            .padding(.horizontal, 8)
            .padding(.vertical, 4)
            .background(Palette.uiCell2.color, in: .capsule)
    }

    // MARK: - Сезон

    /// Сезон: день и полоса дней, открыто за сезон, место в «Кто открыл больше». Очки сезона и места в Арене —
    /// «скоро»: в ответах сервера их пока нет.
    private var seasonCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text(model.season?.name ?? model.seasonName ?? "Сезон")
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Palette.uiInk.color)
                Spacer(minLength: 8)
                if let season = model.season {
                    Text(season.text)
                        .font(.subheadline)
                        .foregroundStyle(Palette.uiInk2.color)
                }
            }
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(area(model.seasonExploredSquareMeters))
                        .font(.role(.seasonPoints))
                        .foregroundStyle(Palette.uiInk.color)
                        .contentTransition(.numericText(value: model.seasonExploredSquareMeters ?? 0))
                        .lineLimit(1)
                        .minimumScaleFactor(0.6)
                    Text("открыто за сезон")
                        .font(.footnote)
                        .foregroundStyle(Palette.uiInk2.color)
                }
                Spacer(minLength: 8)
                if let rank = model.explorationRank {
                    VStack(alignment: .trailing, spacing: 2) {
                        Text("№ \(rank)")
                            .font(.title2.bold().monospacedDigit())
                            .foregroundStyle(Palette.uiInk.color)
                        Text("в «Кто открыл больше»")
                            .font(.footnote)
                            .foregroundStyle(Palette.uiInk2.color)
                    }
                }
            }
            if let season = model.season, let days = season.days, days > 0 {
                ProgressView(value: Double(min(season.day, days)), total: Double(days))
                    .tint(Palette.uiInk.color)
                    .accessibilityLabel(Text(season.text))
            }
            Text("Очки сезона и места в Арене — скоро")
                .font(.caption)
                .foregroundStyle(Palette.uiInk3.color)
        }
        .contentCard()
    }

    // MARK: - Плитки

    private var tiles: some View {
        Grid(horizontalSpacing: 12, verticalSpacing: 12) {
            GridRow {
                NavigationLink {
                    ExplorationStatsView(model: statsModel())
                        .navigationTransition(.zoom(sourceID: "exploration", in: zoom))
                } label: {
                    StatTile(title: "Открыто тумана", value: area(model.exploredSquareMeters))
                        .overlay(alignment: .topTrailing) {
                            Image(systemName: "chevron.right")
                                .font(.caption.weight(.bold))
                                .foregroundStyle(Palette.uiInk3.color)
                                .padding(12)
                                .accessibilityHidden(true)
                        }
                }
                .buttonStyle(.plain)
                .matchedTransitionSource(id: "exploration", in: zoom)
                StatTile(title: "Открыто за сезон", value: area(model.seasonExploredSquareMeters))
            }
            GridRow {
                StatTile(title: "Забеги", value: model.runs.map { NumberText.integer($0) } ?? "—")
                StatTile(
                    title: "Засчитано",
                    value: model.distanceMeters.map { NumberText.kilometers(fromMeters: $0, fractionDigits: 1) } ?? "—")
            }
        }
    }

    /// Статистика — со сводкой, которую профиль уже загрузил; «потянуть вниз» на её экране загрузит свежую.
    private func statsModel() -> ExplorationStatsModel {
        let api = model.api
        return ExplorationStatsModel(
            api: api, summary: model.exploration,
            failure: api == nil && model.exploration == nil ? .notConfigured : nil)
    }

    // MARK: - Скоро

    /// Короны, коллекция, дуэли, рюкзак, карточка недели — экраны следующей волны (контракты C15 для задач Егора).
    private var soon: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Скоро в профиле")
                .font(.headline)
                .foregroundStyle(Palette.uiInk.color)
            VStack(spacing: 0) {
                ForEach(Self.upcoming, id: \.title) { item in
                    HStack(spacing: 14) {
                        Image(systemName: item.symbolName)
                            .font(.body.weight(.semibold))
                            .foregroundStyle(Palette.uiInk3.color)
                            .frame(width: 36, height: 36)
                            .background(Palette.uiCell2.color, in: .circle)
                            .accessibilityHidden(true)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(item.title)
                                .font(.body.weight(.semibold))
                                .foregroundStyle(Palette.uiInk.color)
                            Text(item.detail)
                                .font(.footnote)
                                .foregroundStyle(Palette.uiInk2.color)
                        }
                        Spacer(minLength: 8)
                        Text("скоро")
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(Palette.uiInk2.color)
                            .padding(.horizontal, 8)
                            .padding(.vertical, 3)
                            .background(Palette.uiCell2.color, in: .capsule)
                    }
                    .padding(.horizontal, 14)
                    .frame(minHeight: 60)
                    .accessibilityElement(children: .combine)
                    if item.title != Self.upcoming.last?.title {
                        Divider().padding(.leading, 64)
                    }
                }
            }
            .background(Palette.uiCell.color, in: .rect(cornerRadius: Radius.card))
        }
    }

    private struct Upcoming {
        let title: String
        let detail: String
        let symbolName: String
    }

    private static let upcoming = [
        Upcoming(title: "Короли участков", detail: "Короны за быстрые отрезки", symbolName: "crown"),
        Upcoming(title: "Коллекция", detail: "Тайники и значки по редкостям", symbolName: "rosette"),
        Upcoming(title: "Дуэли", detail: "Вызов 1×1 и живой счёт", symbolName: "person.2"),
        Upcoming(title: "Рюкзак", detail: "Фишки: «Радар», щиты", symbolName: "backpack"),
        Upcoming(title: "Карточка недели", detail: "Итог недели картинкой", symbolName: "photo.on.rectangle"),
    ]

    private func area(_ squareMeters: Double?) -> String {
        squareMeters.map { NumberText.hectares(fromSquareMeters: $0, fractionDigits: 2) } ?? "—"
    }
}
