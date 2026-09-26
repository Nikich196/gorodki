import DesignSystem
import GameCore
import SwiftUI

/// «Входящие» (PLAN.md, §5, экран 11; §3.17): события сервера текстом — без координат; непрочитанные — с точкой.
/// «Прочитано» отмечает всё. Открывается колокольчиком на «Карте» (лист) и строкой в «Профиле».
struct InboxView: View {
    let model: InboxModel
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        content
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(Palette.uiBackground.color)
            .navigationTitle("Входящие")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .primaryAction) {
                    Button("Прочитано", systemImage: "checkmark.circle") {
                        Task { await model.markAllRead() }
                    }
                    .disabled(model.unread == 0 && !model.rows.contains { !$0.read })
                }
            }
            .task { await model.load() }
            .refreshable { await model.load() }
            .animation(Motion.numericRoll.unlessReduceMotion(reduceMotion), value: model.rows)
            .sensoryFeedback(.success, trigger: model.unread) { old, new in old > 0 && new == 0 }
    }

    @ViewBuilder
    private var content: some View {
        if let failure = model.failure, !model.loaded {
            SocialFailureView(failure: failure) { await model.load() }
        } else if !model.loaded {
            ProgressView()
        } else if model.rows.isEmpty {
            TabPlaceholder(
                systemImage: "bell",
                title: "Пока тихо",
                text: "Здесь будут нападения на твою землю, свержения, дуэли и конец сезона — не больше трёх в день."
            )
        } else {
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    NoticeBanner(text: Binding(get: { model.notice }, set: { model.notice = $0 }))
                    VStack(spacing: 0) {
                        ForEach(model.rows) { row in
                            InboxRowView(row: row, time: Self.timeText(row, now: model.now()))
                            if row.id != model.rows.last?.id {
                                Divider().padding(.leading, 64)
                            }
                        }
                    }
                    .background(Palette.uiCell.color, in: .rect(cornerRadius: Radius.card))
                }
                .padding(16)
            }
        }
    }

    /// «4 дня назад», «вчера» — от «сейчас» модели (в фикстурах оно зафиксировано).
    static func timeText(_ row: InboxRow, now: Date) -> String {
        let formatter = RelativeDateTimeFormatter()
        formatter.locale = NumberText.locale
        formatter.dateTimeStyle = .named
        formatter.unitsStyle = .full
        let date = Date(timeIntervalSince1970: Double(row.atMs) / 1_000)
        return formatter.localizedString(for: date, relativeTo: now)
    }
}

private struct InboxRowView: View {
    let row: InboxRow
    let time: String

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: row.symbolName)
                .font(.body.weight(.semibold))
                .foregroundStyle(row.read ? Palette.uiInk2.color : Palette.uiInk.color)
                .frame(width: 36, height: 36)
                .background(Palette.uiCell2.color, in: .circle)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 4) {
                Text(verbatim: row.text)
                    .font(row.read ? .body : .body.weight(.semibold))
                    .foregroundStyle(Palette.uiInk.color)
                    .fixedSize(horizontal: false, vertical: true)
                Text(verbatim: time)
                    .font(.caption)
                    .foregroundStyle(Palette.uiInk2.color)
            }
            Spacer(minLength: 0)
            if !row.read {
                Circle()
                    .fill(Palette.uiInk.color)
                    .frame(width: 8, height: 8)
                    .padding(.top, 8)
                    .accessibilityLabel("Новое")
            }
        }
        .padding(14)
        .accessibilityElement(children: .combine)
    }
}

/// Колокольчик «Входящих» на «Карте» — круглая стеклянная кнопка слоя управления с числом непрочитанных
/// (нейтральным: цветной у игры только «Старт»). Лист открывается zoom-переходом из кнопки.
struct InboxBell: View {
    @Environment(SocialScreens.self) private var social: SocialScreens?
    @Environment(\.runTransition) private var transition

    var body: some View {
        if let social {
            MapGlassButton("Входящие", systemImage: social.inbox.unread > 0 ? "bell.badge" : "bell") {
                social.inboxShown = true
            }
            .overlay(alignment: .topTrailing) {
                if let badge = social.inbox.badge {
                    Text(verbatim: badge)
                        .font(.caption2.weight(.bold).monospacedDigit())
                        .foregroundStyle(Palette.uiButtonInk.color)
                        .padding(.horizontal, 5)
                        .frame(minWidth: 18, minHeight: 18)
                        .background(Palette.uiButton.color, in: .capsule)
                        .contentTransition(.numericText())
                        .offset(x: 2, y: -2)
                        .accessibilityHidden(true)
                }
            }
            .accessibilityValue(Text(social.inbox.unread > 0 ? "есть новые" : ""))
            .modifier(TransitionSource(id: SocialTransitionID.inbox, namespace: transition))
            .task {
                if !social.inbox.loaded { await social.inbox.load() }
            }
        }
    }
}

/// Лист «Входящих» с «Карты».
struct InboxSheet: View {
    let model: InboxModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            InboxView(model: model)
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) {
                        Button("Закрыть", systemImage: "xmark") { dismiss() }
                    }
                }
        }
    }
}

/// Строка «Входящие» в «Профиле» — с числом непрочитанных.
struct InboxProfileRow: View {
    @Environment(SocialScreens.self) private var social: SocialScreens?

    var body: some View {
        if let social {
            NavigationLink {
                InboxView(model: social.inbox)
            } label: {
                HStack(spacing: 16) {
                    Image(systemName: "bell")
                        .font(.body.weight(.semibold))
                        .foregroundStyle(Palette.uiInk2.color)
                        .frame(width: 24)
                    Text("Входящие")
                        .font(.body)
                        .foregroundStyle(Palette.uiInk.color)
                    Spacer()
                    if let badge = social.inbox.badge {
                        Text(verbatim: badge)
                            .font(.subheadline.weight(.bold).monospacedDigit())
                            .foregroundStyle(Palette.uiButtonInk.color)
                            .padding(.horizontal, 8)
                            .frame(minHeight: 22)
                            .background(Palette.uiButton.color, in: .capsule)
                    }
                    Image(systemName: "chevron.right")
                        .font(.footnote.weight(.semibold))
                        .foregroundStyle(Palette.uiInk3.color)
                }
                .padding(.horizontal, 12)
                .frame(minHeight: 52)
                .contentShape(.rect)
            }
            .task {
                if !social.inbox.loaded { await social.inbox.load() }
            }
            Divider().padding(.leading, 52)
        }
    }
}

/// Идентификаторы zoom-переходов социальных экранов (пространство — то же, что у забега: `runTransition`).
enum SocialTransitionID {
    static let inbox = "social.inbox"
}
