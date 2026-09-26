import DesignSystem
import GorodkiAPI
import SwiftUI

/// Раздел «Друзья» (PLAN.md, §3.8: только взаимные, по QR или ссылке): свой код, заявки, друзья, отправленные.
/// «Добавить» — лист с полем кода (zoom из кнопки).
struct FriendsSectionView: View {
    let model: FriendsModel
    let social: SocialScreens
    @Namespace private var transition
    @State private var removing: FriendInfo?
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        @Bindable var social = social
        content
            .task { await model.load() }
            .refreshable { await model.load() }
            .sheet(isPresented: $social.addFriendShown) {
                AddFriendSheet(model: model)
                    .presentationDetents([.medium])
                    .navigationTransition(.zoom(sourceID: "friend.add", in: transition))
            }
            .confirmationDialog(
                removing.map { "Удалить «\($0.name)» из друзей?" } ?? "",
                isPresented: Binding(get: { removing != nil }, set: { if !$0 { removing = nil } }),
                titleVisibility: .visible, presenting: removing
            ) { friend in
                Button("Удалить", role: .destructive) { Task { await model.remove(friend) } }
                Button("Отмена", role: .cancel) {}
            }
    }

    @ViewBuilder
    private var content: some View {
        if let failure = model.failure, !model.loaded {
            SocialFailureView(failure: failure) { await model.load() }
        } else if !model.loaded {
            ProgressView()
        } else {
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    if let code = model.myCode {
                        ShareCodeCard(
                            title: "Твой код для друзей",
                            code: code,
                            shareText: "Добавь меня в друзья в «Городках»: «Клан» → «Друзья» → код \(code)",
                            footnote: "Дружба — только взаимная: друг вводит твой код, ты принимаешь заявку.")
                    }
                    Button {
                        social.addFriendShown = true
                    } label: {
                        Label("Добавить по коду друга", systemImage: "person.badge.plus")
                            .font(.headline)
                            .frame(maxWidth: .infinity, minHeight: 52)
                    }
                    .buttonStyle(.bordered)
                    .tint(Palette.uiInk.color)
                    .matchedTransitionSource(id: "friend.add", in: transition)
                    if let error = model.actionError {
                        Text(error)
                            .font(.footnote)
                            .foregroundStyle(Palette.uiInk2.color)
                            .padding(.horizontal, 16)
                    }
                    if !model.incoming.isEmpty {
                        section("Заявки", model.incoming) { friend in
                            HStack(spacing: 8) {
                                Button("Принять") { Task { await model.accept(friend) } }
                                    .buttonStyle(.borderedProminent)
                                    .tint(Palette.uiButton.color)
                                    .foregroundStyle(Palette.uiButtonInk.color)
                                Button("Отклонить", systemImage: "xmark") { Task { await model.remove(friend) } }
                                    .labelStyle(.iconOnly)
                                    .buttonStyle(.bordered)
                                    .tint(Palette.uiInk2.color)
                            }
                        }
                    }
                    section("Друзья", model.mutual) { friend in
                        Menu {
                            Button("Удалить из друзей", systemImage: "person.badge.minus", role: .destructive) {
                                removing = friend
                            }
                        } label: {
                            Image(systemName: "ellipsis")
                                .frame(width: 44, height: 44)
                                .foregroundStyle(Palette.uiInk2.color)
                        }
                        .accessibilityLabel("Ещё")
                    }
                    if !model.outgoing.isEmpty {
                        section("Отправлены", model.outgoing) { friend in
                            Button("Отозвать") { Task { await model.remove(friend) } }
                                .font(.subheadline)
                                .foregroundStyle(Palette.uiInk2.color)
                        }
                    }
                }
                .padding(16)
                .animation(Motion.numericRoll.unlessReduceMotion(reduceMotion), value: model.friends)
            }
        }
    }

    private func section<Trailing: View>(
        _ title: String, _ friends: [FriendInfo], @ViewBuilder trailing: @escaping (FriendInfo) -> Trailing
    ) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            SectionTitle(title)
            if friends.isEmpty {
                Text("Пока никого. Поделись своим кодом — и друг появится здесь, когда примешь заявку.")
                    .font(.subheadline)
                    .foregroundStyle(Palette.uiInk2.color)
                    .contentCard()
            } else {
                VStack(spacing: 0) {
                    ForEach(friends, id: \.playerId) { friend in
                        HStack(spacing: 12) {
                            PlayerAvatar(name: friend.name, color: PlayerColor(index: Int(friend.colorIndex)))
                            Text(verbatim: friend.name)
                                .font(.body)
                                .foregroundStyle(Palette.uiInk.color)
                                .lineLimit(1)
                            Spacer(minLength: 8)
                            trailing(friend)
                        }
                        .padding(.horizontal, 14)
                        .frame(minHeight: 60)
                        if friend.playerId != friends.last?.playerId {
                            Divider().padding(.leading, 66)
                        }
                    }
                }
                .background(Palette.uiCell.color, in: .rect(cornerRadius: Radius.card))
            }
        }
    }
}

/// Лист «Добавить друга»: код друга (заглавными). Сканер QR — точка подключения к «Сканеру» листика (lab2).
struct AddFriendSheet: View {
    @Bindable var model: FriendsModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            VStack(alignment: .leading, spacing: 14) {
                CodeField(prompt: "Код друга, например R4T8-KD2M", text: $model.addCode)
                    .onSubmit { Task { await add() } }
                if let error = model.actionError {
                    Text(error)
                        .font(.footnote)
                        .foregroundStyle(Palette.uiInk2.color)
                }
                Text("Друг найдёт свой код здесь же, в «Друзьях». Сканер QR появится вместе с камерой.")
                    .font(.footnote)
                    .foregroundStyle(Palette.uiInk2.color)
                Spacer(minLength: 0)
                Button {
                    Task { await add() }
                } label: {
                    if model.busy { ProgressView() } else { Text("Отправить заявку") }
                }
                .buttonStyle(.neutral)
                .disabled(!model.canAdd)
            }
            .padding(20)
            .background(Palette.uiBackground.color)
            .navigationTitle("Добавить друга")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Отмена", role: .cancel) { dismiss() }
                }
            }
        }
    }

    private func add() async {
        if await model.add() { dismiss() }
    }
}
