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
            if model.signedIn {
                GameHubSection()
            }
        }
        .animation(Motion.numericRoll.unlessReduceMotion(reduceMotion), value: model.exploredSquareMeters)
    }

    // MARK: - Шапка

    /// Шапка-герой (бренд «Крепость»): свечение цвета игрока, силуэт Холмских ворот, аватар в вращающемся кольце огня.
    private var header: some View {
        HStack(spacing: 16) {
            ProfileAvatar(initial: String((model.displayName ?? "?").prefix(1)), color: model.playerColor)
            VStack(alignment: .leading, spacing: 6) {
                Text(model.displayName ?? (model.signedIn ? "Игрок" : "Вход не выполнен"))
                    .font(.system(size: 24, weight: .black).width(.expanded))
                    .italic()
                    .foregroundStyle(Palette.uiInk.color)
                    .lineLimit(1)
                    .minimumScaleFactor(0.6)
                Text(subtitle)
                    .font(.subheadline)
                    .foregroundStyle(Palette.uiInk2.color)
                if model.signedIn {
                    // Не влезают в строку — друг под другом: значок не переносится на две строки.
                    ViewThatFits(in: .horizontal) {
                        HStack(spacing: 6) { tags }
                        VStack(alignment: .leading, spacing: 6) { tags }
                    }
                }
            }
            Spacer(minLength: 0)
        }
        .padding(18)
        .padding(.bottom, 26)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(alignment: .bottom) {
            ZStack(alignment: .bottom) {
                RadialGradient(
                    colors: [model.playerColor.color.opacity(0.45), model.playerColor.color.opacity(0)],
                    center: .topLeading, startRadius: 0, endRadius: 320)
                FortressSkylineView(color: Palette.uiInk.color.opacity(0.10))
                    .frame(height: 54)
                    .padding(.horizontal, 8)
            }
            .clipShape(.rect(cornerRadius: Radius.card))
        }
        .brandCard()
    }

    private var subtitle: String {
        guard model.signedIn else { return "Вкладки без входа — только для сборки команды" }
        switch model.role {
        case "admin": return "Администратор"
        case "demo": return "Демо-режим"
        default: return "Ник и цвет назначены автоматически"
        }
    }

    @ViewBuilder
    private var tags: some View {
        tag("Лига «Бег»", systemImage: "figure.run")
        if model.publicProfile == false {
            tag("Ник скрыт", systemImage: "eye.slash")
        }
    }

    private func tag(_ title: String, systemImage: String) -> some View {
        Label(title, systemImage: systemImage)
            .lineLimit(1)
            .fixedSize()
            .font(.caption.weight(.semibold))
            .foregroundStyle(Palette.uiInk2.color)
            .padding(.horizontal, 8)
            .padding(.vertical, 4)
            .glassEffect(.regular, in: .capsule)
    }

    // MARK: - Сезон

    /// Сезон: день и полоса дней, открыто за сезон, место в «Кто открыл больше»; очки и места — в «Рейтингах».
    private var seasonCard: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Text((model.season?.name ?? model.seasonName ?? "Сезон").uppercased())
                    .font(.system(size: 13, weight: .heavy).width(.expanded))
                    .foregroundStyle(Palette.uiInk.color)
                Spacer(minLength: 8)
                if let season = model.season {
                    Text(season.text)
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(Palette.uiInk2.color)
                }
            }
            HStack(alignment: .center, spacing: 18) {
                if let season = model.season, let days = season.days, days > 0 {
                    ZStack {
                        BrandRing(progress: Double(min(season.day, days)) / Double(days), lineWidth: 9)
                        VStack(spacing: 0) {
                            Text(NumberText.integer(min(season.day, days)))
                                .font(.system(size: 26, weight: .black).width(.expanded))
                                .foregroundStyle(Palette.uiInk.color)
                            Text("из \(days)")
                                .font(.caption2.weight(.semibold))
                                .foregroundStyle(Palette.uiInk2.color)
                        }
                    }
                    .frame(width: 92, height: 92)
                    .accessibilityElement(children: .ignore)
                    .accessibilityLabel(Text(season.text))
                }
                VStack(alignment: .leading, spacing: 4) {
                    Text(area(model.seasonExploredSquareMeters))
                        .font(.system(size: 34, weight: .black).width(.expanded))
                        .italic()
                        .foregroundStyle(Palette.uiInk.color)
                        .contentTransition(.numericText(value: model.seasonExploredSquareMeters ?? 0))
                        .lineLimit(1)
                        .minimumScaleFactor(0.5)
                    Text("открыто за сезон")
                        .font(.footnote)
                        .foregroundStyle(Palette.uiInk2.color)
                    if let rank = model.explorationRank {
                        RankPill(rank: rank)
                    }
                }
            }
            Text("Очки и места сезона — во вкладке «Рейтинги», пересчёт каждые 5 минут")
                .font(.caption)
                .foregroundStyle(Palette.uiInk3.color)
        }
        .contentCard(padding: 18)
    }

    // MARK: - Плитки

    private var tiles: some View {
        Grid(horizontalSpacing: 12, verticalSpacing: 12) {
            GridRow {
                NavigationLink {
                    ExplorationStatsView(model: statsModel())
                        .navigationTransition(.zoom(sourceID: "exploration", in: zoom))
                } label: {
                    StatTile(
                        title: "Открыто тумана", value: area(model.exploredSquareMeters), systemImage: "cloud.sun.fill"
                    )
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
                StatTile(
                    title: "Открыто за сезон", value: area(model.seasonExploredSquareMeters),
                    systemImage: "flag.checkered")
            }
            GridRow {
                StatTile(
                    title: "Забеги", value: model.runs.map { NumberText.integer($0) } ?? "—", systemImage: "figure.run")
                StatTile(
                    title: "Засчитано",
                    value: model.distanceMeters.map { NumberText.kilometers(fromMeters: $0, fractionDigits: 1) } ?? "—",
                    systemImage: "point.topleft.down.to.point.bottomright.curvepath.fill")
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

    private func area(_ squareMeters: Double?) -> String {
        squareMeters.map { NumberText.hectares(fromSquareMeters: $0, fractionDigits: 2) } ?? "—"
    }
}

/// Аватар профиля: буква в цвете игрока и кольцо огня бренда, которое медленно вращается.
private struct ProfileAvatar: View {
    let initial: String
    let color: PlayerColor
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var spin = false

    var body: some View {
        let theme = Theme(colorScheme)
        Text(initial)
            .font(.system(size: 28, weight: .black).width(.expanded))
            .foregroundStyle(color.startInkColor)
            .frame(width: 66, height: 66)
            .background(color.color, in: .circle)
            .padding(5)
            .overlay {
                Circle()
                    .strokeBorder(
                        AngularGradient(
                            colors: [
                                Brand.amber[theme].color, Brand.ember[theme].color, Brand.dusk[theme].color,
                                Brand.amber[theme].color,
                            ],
                            center: .center),
                        lineWidth: 3.5
                    )
                    .rotationEffect(.degrees(spin ? 360 : 0))
            }
            .shadow(color: color.color.opacity(0.5), radius: 12)
            .accessibilityHidden(true)
            .onAppear {
                guard !reduceMotion else { return }
                withAnimation(.linear(duration: 8).repeatForever(autoreverses: false)) { spin = true }
            }
    }
}

/// Место в рейтинге: корона и номер на капсуле огня.
private struct RankPill: View {
    let rank: Int
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        Label {
            Text("№ \(rank) · Кто открыл больше")
        } icon: {
            Image(systemName: "crown.fill")
        }
        .font(.caption.weight(.heavy))
        .foregroundStyle(.white)
        .padding(.horizontal, 10)
        .padding(.vertical, 5)
        .background(Brand.fire(Theme(colorScheme)), in: .capsule)
        .padding(.top, 4)
    }
}
