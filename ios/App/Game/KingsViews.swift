import DesignSystem
import GameCore
import GorodkiAPI
import MapKit
import SwiftUI

/// «Короли участков» (PLAN.md, §3.13): короткие отрезки города, корона — лучшее время в сезоне, раздельно «Бег» и
/// «Вело». Экран — вкладка «Короли» в «Рейтингах» и строка раздела «Игра» в профиле.
struct KingsList: View {
    @Environment(SocialScreens.self) private var social

    var body: some View {
        @Bindable var game = social.game
        LoadableContent(state: game.segments, load: { await game.loadSegments() }) { list in
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    Picker("Лига", selection: $game.kingsLeague) {
                        Text("Бег").tag(Components.Schemas.League.run)
                        Text("Вело").tag(Components.Schemas.League.bike)
                    }
                    .pickerStyle(.segmented)
                    Text(
                        "Пробеги отрезок быстрее всех за сезон — и корона твоя. Больше всех проходов за 30 дней — «местная легенда»."
                    )
                    .font(.footnote)
                    .foregroundStyle(Palette.uiInk2.color)
                    if list.segments.isEmpty {
                        Text("Отрезков этой лиги пока нет.")
                            .font(.body)
                            .foregroundStyle(Palette.uiInk2.color)
                            .contentCard()
                    }
                    ForEach(list.segments, id: \.id) { segment in
                        NavigationLink {
                            SegmentScreen(segmentId: segment.id, league: list.league, title: segment.name)
                        } label: {
                            SegmentRow(segment: segment)
                        }
                        .buttonStyle(.plain)
                    }
                }
                .padding(16)
            }
            .refreshable { await game.loadSegments() }
            .atmosphereBackground()
        }
        .onChange(of: game.kingsLeague) {
            Task { await game.loadSegments() }
        }
    }
}

/// «Короли участков» отдельным экраном — из профиля.
struct KingsScreen: View {
    var body: some View {
        KingsList()
            .navigationTitle("Короли участков")
    }
}

private struct SegmentRow: View {
    let segment: Components.Schemas.SegmentSummary

    var body: some View {
        HStack(spacing: 14) {
            GameBadge(
                systemImage: segment.seasonCrown == nil ? "flag.checkered" : "crown.fill",
                color: segment.seasonCrown?.me == true ? Brand.ember.day.color : Brand.amber.day.color, size: 44)
            VStack(alignment: .leading, spacing: 3) {
                Text(segment.name)
                    .font(.headline)
                    .foregroundStyle(Palette.uiInk.color)
                Text(NumberText.meters(Int(segment.lengthMeters.rounded())))
                    .font(.subheadline.monospacedDigit())
                    .foregroundStyle(Palette.uiInk2.color)
            }
            Spacer(minLength: 8)
            if let crown = segment.seasonCrown {
                VStack(alignment: .trailing, spacing: 3) {
                    Text(crown.me ? "Ты король" : crown.name)
                        .font(.subheadline.weight(.bold))
                        .foregroundStyle(crown.me ? Brand.ember.day.color : Palette.uiInk.color)
                        .lineLimit(1)
                    Text(SegmentTime.text(milliseconds: crown.timeMs))
                        .font(.subheadline.monospacedDigit())
                        .foregroundStyle(Palette.uiInk2.color)
                }
            } else {
                Text("Короны нет —\nзабери первым")
                    .font(.caption.weight(.semibold))
                    .multilineTextAlignment(.trailing)
                    .foregroundStyle(Brand.ember.day.color)
            }
            Image(systemName: "chevron.right")
                .font(.footnote.weight(.semibold))
                .foregroundStyle(Palette.uiInk3.color)
        }
        .contentCard(padding: 14)
        .accessibilityElement(children: .combine)
    }
}

/// Отрезок: линия на карте, корона сезона и за всё время, «местная легенда», своё лучшее и таблица сезона.
struct SegmentScreen: View {
    let segmentId: String
    let league: Components.Schemas.League
    let title: String
    @Environment(SocialScreens.self) private var social
    @State private var info: Loadable<(SegmentInfo, SegmentBoard)> = .idle

    var body: some View {
        LoadableContent(state: info, load: load) { pair in
            let (segment, board) = pair
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    SegmentMap(line: segment.line)
                    HStack(spacing: 10) {
                        CrownTile(title: "Корона сезона", entry: segment.seasonCrown, symbol: "crown.fill")
                        CrownTile(title: "За всё время", entry: segment.allTimeCrown, symbol: "trophy.fill")
                    }
                    if let legend = segment.legend {
                        HStack(spacing: 12) {
                            GameBadge(systemImage: "figure.walk.motion", color: Brand.river.day.color, size: 40)
                            VStack(alignment: .leading, spacing: 2) {
                                Text("Местная легенда")
                                    .font(.caption.weight(.semibold))
                                    .foregroundStyle(Palette.uiInk2.color)
                                Text((legend.me ? "Ты" : legend.name) + " · " + CountText.efforts(Int(legend.efforts)))
                                    .font(.headline)
                                    .foregroundStyle(Palette.uiInk.color)
                            }
                            Spacer(minLength: 0)
                        }
                        .contentCard(padding: 14)
                    }
                    if let best = segment.myBestTimeMs {
                        Label("Твоё лучшее: " + SegmentTime.text(milliseconds: best), systemImage: "stopwatch.fill")
                            .font(.headline.monospacedDigit())
                            .foregroundStyle(Palette.uiInk.color)
                            .padding(.horizontal, 4)
                    }
                    SectionTitle("Сезон")
                    VStack(spacing: 0) {
                        ForEach(board.entries, id: \.rank) { entry in
                            SegmentEntryRow(entry: entry)
                            if entry.rank != board.entries.last?.rank {
                                Divider().padding(.leading, 52)
                            }
                        }
                        if let mine = board.mine, !board.entries.contains(where: { $0.me }) {
                            Divider()
                            SegmentEntryRow(entry: mine)
                        }
                    }
                    .brandCard(padding: 0)
                    Text(
                        "Проход засчитывается на сервере: ворота старта и финиша по порядку, коридор 30 м. Чужие времена видны через 20 минут."
                    )
                    .font(.footnote)
                    .foregroundStyle(Palette.uiInk2.color)
                }
                .padding(16)
            }
            .refreshable { await load() }
            .atmosphereBackground()
        }
        .navigationTitle(title)
        .navigationBarTitleDisplayMode(.inline)
    }

    private func load() async {
        guard let source = social.game.source else {
            info = .failed(.offline)
            return
        }
        do {
            async let segment = source.segment(id: segmentId, league: league)
            async let board = source.board(id: segmentId, league: league, season: nil)
            info = .loaded(try await (segment, board))
        } catch {
            info = .failed(SocialFailure.from(error))
        }
    }
}

private struct CrownTile: View {
    let title: String
    let entry: Components.Schemas.SegmentEntry?
    let symbol: String

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Label(title, systemImage: symbol)
                .font(.caption.weight(.semibold))
                .foregroundStyle(Brand.amber.day.color)
            Text(entry.map { $0.me ? "Ты" : $0.name } ?? "Свободна")
                .font(.headline)
                .foregroundStyle(Palette.uiInk.color)
                .lineLimit(1)
            Text(entry.map { SegmentTime.text(milliseconds: $0.timeMs) } ?? "—")
                .font(.system(size: 22, weight: .black).width(.expanded).monospacedDigit())
                .foregroundStyle(Palette.uiInk.color)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .contentCard(padding: 14)
        .accessibilityElement(children: .combine)
    }
}

private struct SegmentEntryRow: View {
    let entry: Components.Schemas.SegmentEntry

    var body: some View {
        HStack(spacing: 12) {
            Text(NumberText.integer(Int(entry.rank)))
                .font(.subheadline.weight(.bold).monospacedDigit())
                .foregroundStyle(entry.rank == 1 ? Brand.amber.day.color : Palette.uiInk2.color)
                .frame(width: 28)
            Text(entry.name + (entry.me ? " · ты" : ""))
                .font(entry.me ? .body.weight(.bold) : .body)
                .foregroundStyle(Palette.uiInk.color)
                .lineLimit(1)
            Spacer(minLength: 8)
            Text(SegmentTime.text(milliseconds: entry.timeMs))
                .font(.body.weight(.semibold).monospacedDigit())
                .foregroundStyle(Palette.uiInk.color)
        }
        .padding(.horizontal, 14)
        .frame(minHeight: 46)
        .background(entry.me ? Brand.ember.day.color.opacity(0.10) : .clear)
        .accessibilityElement(children: .combine)
    }
}

/// Линия отрезка на карте: старт и финиш — флажки, сама линия — огонь бренда.
private struct SegmentMap: View {
    let line: [Double]

    var body: some View {
        let points = stride(from: 0, to: line.count - 1, by: 2).map {
            CLLocationCoordinate2D(latitude: line[$0], longitude: line[$0 + 1])
        }
        Group {
            if let first = points.first, let last = points.last {
                Map(initialPosition: .rect(Self.rect(points)), interactionModes: [.pan, .zoom]) {
                    MapPolyline(coordinates: points)
                        .stroke(
                            Gradient(colors: [Brand.amber.day.color, Brand.ember.day.color]),
                            style: StrokeStyle(lineWidth: 6, lineCap: .round, lineJoin: .round))
                    Annotation("Старт", coordinate: first) {
                        GameBadge(systemImage: "flag.fill", color: PlayerColor.forest.edge.day.color, size: 26)
                    }
                    Annotation("Финиш", coordinate: last) {
                        GameBadge(systemImage: "flag.checkered", color: Brand.ember.day.color, size: 26)
                    }
                }
                .mapStyle(.standard(elevation: .flat, emphasis: .muted, pointsOfInterest: .excludingAll))
            } else {
                Color.clear
            }
        }
        .frame(height: 220)
        .clipShape(.rect(cornerRadius: Radius.card))
    }

    private static func rect(_ points: [CLLocationCoordinate2D]) -> MKMapRect {
        let mapPoints = points.map(MKMapPoint.init)
        let xs = mapPoints.map(\.x)
        let ys = mapPoints.map(\.y)
        guard let minX = xs.min(), let maxX = xs.max(), let minY = ys.min(), let maxY = ys.max() else {
            return .world
        }
        let width = max(maxX - minX, 400)
        let height = max(maxY - minY, 400)
        return MKMapRect(x: minX - width * 0.25, y: minY - height * 0.35, width: width * 1.5, height: height * 1.7)
    }
}
