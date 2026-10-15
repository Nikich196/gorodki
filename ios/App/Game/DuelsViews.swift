import DesignSystem
import GameCore
import GorodkiAPI
import SwiftUI

/// Дуэли 1×1 (PLAN.md, §3.14): только взаимные друзья одной лиги, срок 1, 3 или 7 дней, метрика — захват,
/// исследование, дистанция или время на отрезке; ставка — по желанию, фишка из рюкзака. Счёт соперника — после
/// границы публичности (20 минут).
struct DuelsScreen: View {
    @Environment(SocialScreens.self) private var social
    @State private var challengeShown = false

    var body: some View {
        let game = social.game
        LoadableContent(state: game.duels, load: { await game.loadDuels() }) { duels in
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    Button {
                        challengeShown = true
                    } label: {
                        Label("Вызвать друга", systemImage: "figure.fencing")
                    }
                    .buttonStyle(.brand)
                    if let error = game.actionError {
                        Text(error)
                            .font(.footnote)
                            .foregroundStyle(Palette.uiInk2.color)
                    }
                    if duels.isEmpty {
                        Text("Дуэлей пока нет. Вызови друга: кто за 3 дня пробежит больше или откроет больше Бреста?")
                            .font(.body)
                            .foregroundStyle(Palette.uiInk2.color)
                            .contentCard()
                    }
                    ForEach(duels, id: \.id) { duel in
                        DuelCard(duel: duel)
                    }
                    Text(
                        "Дуэль — только со взаимным другом твоей лиги. Не больше двух одновременно, с одним другом — раз в неделю."
                    )
                    .font(.footnote)
                    .foregroundStyle(Palette.uiInk2.color)
                }
                .padding(16)
            }
            .refreshable { await game.loadDuels() }
            .atmosphereBackground()
        }
        .navigationTitle("Дуэли")
        .sheet(isPresented: $challengeShown) {
            NewDuelSheet()
                .presentationDetents([.large])
        }
    }
}

/// Карточка дуэли: соперники, метрика, срок, счёт полосой «ты — соперник», ответ на вызов.
private struct DuelCard: View {
    let duel: DuelInfo
    @Environment(SocialScreens.self) private var social

    var body: some View {
        let game = social.game
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 8) {
                Label(duel.metric.title, systemImage: duel.metric.symbol)
                    .font(.caption.weight(.bold))
                    .foregroundStyle(Brand.ember.day.color)
                Text("·")
                    .foregroundStyle(Palette.uiInk3.color)
                Text(CountText.days(Int(duel.days)))
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(Palette.uiInk2.color)
                Spacer(minLength: 0)
                status
            }
            HStack(alignment: .center, spacing: 10) {
                side(duel.me, title: "Ты", alignment: .leading)
                Text("VS")
                    .font(.system(size: 18, weight: .black).width(.expanded))
                    .italic()
                    .foregroundStyle(Brand.ember.day.color)
                side(duel.opponent, title: duel.opponent.name, alignment: .trailing)
            }
            if duel.status == .active {
                ScoreBar(me: duel.me.score, opponent: duel.opponent.score, lowerIsBetter: duel.metric.lowerIsBetter)
            }
            if duel.status == .pending && duel.isIncoming {
                HStack(spacing: 10) {
                    Button("Принять") { Task { await game.accept(duel) } }
                        .fontWeight(.semibold)
                        .buttonStyle(.glassProminent)
                        .tint(Brand.ember.color)
                        .foregroundStyle(.white)
                    Button("Отклонить") { Task { await game.decline(duel) } }
                        .buttonStyle(.glass)
                        .tint(Palette.uiInk2.color)
                }
                .disabled(game.busy)
            }
        }
        .brandCard(padding: 16)
        .sensoryFeedback(.success, trigger: duel.status)
    }

    @ViewBuilder
    private var status: some View {
        switch duel.status {
        case .pending:
            Text(duel.isIncoming ? "вызов тебе" : "ждёт ответа")
                .font(.caption.weight(.bold))
                .foregroundStyle(Brand.amber.day.color)
        case .active:
            if let ends = duel.endsAtMs {
                Text(
                    "до "
                        + Date(timeIntervalSince1970: Double(ends) / 1_000)
                        .formatted(.dateTime.day().month(.abbreviated).hour().minute().locale(NumberText.locale))
                )
                .font(.caption.weight(.semibold))
                .foregroundStyle(Palette.uiInk2.color)
            }
        case .finished:
            Text(duel.winnerId == duel.me.playerId ? "победа" : duel.winnerId == nil ? "ничья" : "поражение")
                .font(.caption.weight(.heavy))
                .foregroundStyle(
                    duel.winnerId == duel.me.playerId ? PlayerColor.forest.edge.day.color : Palette.uiInk2.color)
        case .declined:
            Text("отклонена")
                .font(.caption.weight(.semibold))
                .foregroundStyle(Palette.uiInk3.color)
        }
    }

    private func side(_ side: Components.Schemas.DuelSide, title: String, alignment: HorizontalAlignment) -> some View {
        let color = PlayerColor(index: side.colorIndex)
        return VStack(alignment: alignment, spacing: 4) {
            HStack(spacing: 6) {
                if alignment == .trailing { Spacer(minLength: 0) }
                Text(String(side.name.prefix(1)))
                    .font(.system(size: 15, weight: .black).width(.expanded))
                    .foregroundStyle(color.startInkColor)
                    .frame(width: 30, height: 30)
                    .background(color.color, in: .circle)
                    .overlay { Circle().strokeBorder(color.edgeColor, lineWidth: 2) }
                Text(title)
                    .font(.subheadline.weight(.bold))
                    .foregroundStyle(Palette.uiInk.color)
                    .lineLimit(1)
                if alignment == .leading { Spacer(minLength: 0) }
            }
            Text(duel.metric.format(side.score))
                .font(.system(size: 20, weight: .black).width(.expanded).monospacedDigit())
                .foregroundStyle(Palette.uiInk.color)
                .contentTransition(.numericText())
        }
        .frame(maxWidth: .infinity, alignment: alignment == .leading ? .leading : .trailing)
    }
}

/// Полоса счёта: доля «моего» — огонь, соперника — сумерки. На отрезке меньше — лучше.
private struct ScoreBar: View {
    let me: Double?
    let opponent: Double?
    let lowerIsBetter: Bool

    var body: some View {
        let mine = max(0, me ?? 0)
        let theirs = max(0, opponent ?? 0)
        let share: Double =
            mine + theirs <= 0 ? 0.5 : lowerIsBetter ? theirs / (mine + theirs) : mine / (mine + theirs)
        GeometryReader { proxy in
            HStack(spacing: 3) {
                Capsule().fill(Brand.fire(.day))
                    .frame(width: max(8, (proxy.size.width - 3) * share))
                Capsule().fill(Brand.dusk.day.color.opacity(0.7))
            }
        }
        .frame(height: 10)
        .animation(.spring(duration: 0.6), value: share)
        .accessibilityLabel(share >= 0.5 ? "Ты впереди" : "Соперник впереди")
    }
}

/// «Вызвать друга»: друг (взаимный), метрика, срок; на отрезке — какой отрезок.
private struct NewDuelSheet: View {
    @Environment(SocialScreens.self) private var social
    @Environment(\.dismiss) private var dismiss
    @State private var friendId: String?
    @State private var metric: Components.Schemas.DuelMetric = .distance
    @State private var days: Int32 = 3
    @State private var segmentId: String?

    var body: some View {
        let game = social.game
        let friends = social.friends.mutual
        NavigationStack {
            Form {
                Section("Кого вызвать") {
                    if friends.isEmpty {
                        Text("Нет взаимных друзей. Добавь друга по коду во вкладке «Клан → Друзья».")
                            .foregroundStyle(Palette.uiInk2.color)
                    }
                    ForEach(friends, id: \.playerId) { friend in
                        Button {
                            friendId = friend.playerId
                        } label: {
                            HStack {
                                Text(friend.name)
                                    .foregroundStyle(Palette.uiInk.color)
                                Spacer()
                                if friendId == friend.playerId {
                                    Image(systemName: "checkmark.circle.fill")
                                        .foregroundStyle(Brand.ember.color)
                                }
                            }
                        }
                    }
                }
                Section("За что") {
                    Picker("Метрика", selection: $metric) {
                        ForEach(Components.Schemas.DuelMetric.allCases, id: \.self) { metric in
                            Label(metric.title, systemImage: metric.symbol).tag(metric)
                        }
                    }
                    .pickerStyle(.inline)
                    .labelsHidden()
                    if metric == .segmentTime {
                        Picker("Отрезок", selection: $segmentId) {
                            Text("Выбери").tag(String?.none)
                            ForEach(game.segments.value?.segments ?? [], id: \.id) { segment in
                                Text(segment.name).tag(Optional(segment.id))
                            }
                        }
                    }
                }
                Section("Срок") {
                    Picker("Дней", selection: $days) {
                        Text("1 день").tag(Int32(1))
                        Text("3 дня").tag(Int32(3))
                        Text("7 дней").tag(Int32(7))
                    }
                    .pickerStyle(.segmented)
                }
                if let error = game.actionError {
                    Text(error)
                        .foregroundStyle(Palette.uiInk2.color)
                }
            }
            .scrollContentBackground(.hidden)
            .atmosphereBackground()
            .navigationTitle("Новая дуэль")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Отмена") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Вызвать") {
                        Task {
                            guard let friendId else { return }
                            let sent = await game.challenge(
                                .init(
                                    opponentId: friendId, league: game.kingsLeague, metric: metric, days: days,
                                    segmentId: metric == .segmentTime ? segmentId : nil, stakeItemId: nil))
                            if sent { dismiss() }
                        }
                    }
                    .disabled(friendId == nil || (metric == .segmentTime && segmentId == nil) || game.busy)
                }
            }
            .task {
                await social.friends.load()
                if game.segments.value == nil { await game.loadSegments() }
            }
        }
    }
}

extension DuelInfo {
    /// Вызов пришёл мне и ждёт ответа. ЗАДАЧА #147: сервер добавляет поле `incoming` — до него ждущий ответа вызов
    /// считается входящим (принять свой же вызов сервер не даст — ответит 409).
    var isIncoming: Bool { status == .pending }
}
