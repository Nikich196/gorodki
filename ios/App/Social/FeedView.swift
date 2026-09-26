import DesignSystem
import SwiftUI

/// Раздел «Лента» (PLAN.md, §3.8, экран 9): фильтр «Друзья и клан | Все», карточки захватов и забегов — только то,
/// что есть в посте; респект (значок подпрыгивает, лёгкая вибрация), жалоба и блокировка автора — с подтверждением.
struct FeedSectionView: View {
    @Bindable var model: FeedModel
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var blocking: FeedCard?
    @State private var reporting: FeedCard?

    var body: some View {
        content
            .task(id: model.scope) { await model.load() }
            .refreshable { await model.load() }
            .confirmationDialog(
                blocking.map { "Заблокировать «\($0.authorName)»?" } ?? "",
                isPresented: Binding(get: { blocking != nil }, set: { if !$0 { blocking = nil } }),
                titleVisibility: .visible, presenting: blocking
            ) { card in
                Button("Заблокировать", role: .destructive) { Task { await model.block(card) } }
                Button("Отмена", role: .cancel) {}
            } message: { _ in
                Text("Посты игрока исчезнут из ленты, а дружба и заявки с ним снимутся. Снять блокировку можно позже.")
            }
            .confirmationDialog(
                "Пожаловаться на пост?",
                isPresented: Binding(get: { reporting != nil }, set: { if !$0 { reporting = nil } }),
                titleVisibility: .visible, presenting: reporting
            ) { card in
                Button("Пожаловаться", role: .destructive) { Task { await model.report(card) } }
                Button("Отмена", role: .cancel) {}
            } message: { _ in
                Text("Пост посмотрит администратор.")
            }
    }

    @ViewBuilder
    private var content: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                Picker("Чья лента", selection: $model.scope) {
                    ForEach(FeedScope.allCases) { scope in
                        Text(scope.title).tag(scope)
                    }
                }
                .pickerStyle(.segmented)
                NoticeBanner(text: $model.notice)
                if let failure = model.failure, !model.loaded {
                    SocialFailureView(failure: failure) { await model.load() }
                        .frame(minHeight: 420)
                } else if !model.loaded {
                    ProgressView()
                        .frame(maxWidth: .infinity, minHeight: 240)
                } else if model.visibleCards.isEmpty {
                    Text("Здесь появятся захваты и забеги друзей и клана — после того, как их покажет карта.")
                        .font(.body)
                        .foregroundStyle(Palette.uiInk2.color)
                        .contentCard()
                } else {
                    ForEach(model.visibleCards) { card in
                        FeedCardView(
                            card: card, reported: model.reported.contains(card.id),
                            respect: { Task { await model.respect(card) } },
                            report: { reporting = card },
                            block: { blocking = card }
                        )
                        .transition(.opacity.combined(with: .scale(scale: 0.96)))
                    }
                }
            }
            .padding(16)
            .animation(Motion.numericRoll.unlessReduceMotion(reduceMotion), value: model.visibleCards)
            .animation(Motion.numericRoll.unlessReduceMotion(reduceMotion), value: model.notice)
        }
    }
}

/// Карточка поста: автор, дата, что сделал и сколько, респекты. Свой пост — без респекта, жалобы и блокировки.
struct FeedCardView: View {
    let card: FeedCard
    let reported: Bool
    let respect: () -> Void
    let report: () -> Void
    let block: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 12) {
                PlayerAvatar(name: card.authorName, color: PlayerColor(index: card.colorIndex), size: 36)
                VStack(alignment: .leading, spacing: 1) {
                    Text(verbatim: card.mine ? card.authorName + " · ты" : card.authorName)
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(Palette.uiInk.color)
                    Text(verbatim: card.day)
                        .font(.caption)
                        .foregroundStyle(Palette.uiInk2.color)
                }
                Spacer()
                if !card.mine {
                    Menu {
                        Button(
                            reported ? "Жалоба отправлена" : "Пожаловаться", systemImage: "exclamationmark.bubble",
                            action: report
                        )
                        .disabled(reported)
                        Button("Заблокировать автора", systemImage: "hand.raised", role: .destructive, action: block)
                    } label: {
                        Image(systemName: "ellipsis")
                            .frame(width: 44, height: 44)
                            .foregroundStyle(Palette.uiInk2.color)
                    }
                    .accessibilityLabel("Ещё")
                }
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(verbatim: card.title)
                    .font(.footnote.weight(.semibold))
                    .textCase(.uppercase)
                    .foregroundStyle(Palette.uiInk2.color)
                Text(verbatim: card.value)
                    .font(.system(size: 28, weight: .heavy, design: .rounded).monospacedDigit())
                    .foregroundStyle(Palette.uiInk.color)
                    .minimumScaleFactor(0.7)
                    .lineLimit(1)
            }
            RespectButton(
                count: card.respects, respected: card.respectedByMe, enabled: !card.mine, action: respect)
        }
        .contentCard()
    }
}

/// Респект: хлопок подпрыгивает (`symbolEffect(.bounce)`), число перекатывается, лёгкая вибрация. Поставить можно
/// один раз (сервер: повтор ничего не меняет); свой пост — только число.
private struct RespectButton: View {
    let count: Int
    let respected: Bool
    let enabled: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 6) {
                Image(systemName: respected ? "hands.clap.fill" : "hands.clap")
                    .symbolEffect(.bounce, value: respected)
                Text(verbatim: "\(count)")
                    .monospacedDigit()
                    .contentTransition(.numericText(value: Double(count)))
            }
            .font(.subheadline.weight(.semibold))
            .foregroundStyle(respected ? Palette.uiInk.color : Palette.uiInk2.color)
            .padding(.horizontal, 14)
            .frame(minHeight: 36)
            .background(Palette.uiFill.color, in: .capsule)
        }
        .buttonStyle(.plain)
        // Не `disabled`: поставленный респект должен выглядеть ярко, а не погасшим.
        .allowsHitTesting(enabled && !respected)
        .sensoryFeedback(.impact(weight: .light), trigger: respected) { _, new in new }
        .accessibilityLabel(Text(respected ? "Респект поставлен" : "Респект"))
        .accessibilityValue(Text(CountText.respects(count)))
    }
}
