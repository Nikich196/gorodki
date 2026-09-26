import DesignSystem
import GorodkiAPI
import SwiftUI

/// Вкладка «Клан» (PLAN.md, §5, экраны 19 и 9; решено 26.09 по делегированию — ios-app.md): переключатель
/// «Клан | Друзья | Лента» в панели навигации (стекло панели рисует система), под ним — выбранный раздел.
struct ClanTab: View {
    let social: SocialScreens

    var body: some View {
        @Bindable var social = social
        NavigationStack {
            Group {
                switch social.clanSection {
                case .clan: ClanSectionView(model: social.clan, social: social)
                case .friends: FriendsSectionView(model: social.friends, social: social)
                case .feed: FeedSectionView(model: social.feed)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(Palette.uiBackground.color)
            .navigationTitle(social.clanSection.title)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .principal) {
                    Picker("Раздел", selection: $social.clanSection) {
                        ForEach(ClanSection.allCases) { section in
                            Text(section.title).tag(section)
                        }
                    }
                    .pickerStyle(.segmented)
                    .frame(maxWidth: 300)
                }
            }
        }
    }
}

/// Раздел «Клан»: свой клан или «не в клане».
struct ClanSectionView: View {
    let model: ClanModel
    let social: SocialScreens

    var body: some View {
        Group {
            switch model.state {
            case .loading:
                ProgressView()
            case .failed(let failure):
                SocialFailureView(failure: failure) { await model.load() }
            case .none:
                NoClanView(model: model, social: social)
            case .member(let clan):
                ClanDetailView(model: model, clan: clan)
            }
        }
        .task { await model.load() }
    }
}

/// Не в клане: вступить по коду от лидера или офицера (без заявок и модерации) или создать свой.
struct NoClanView: View {
    @Bindable var model: ClanModel
    let social: SocialScreens
    @Namespace private var transition

    var body: some View {
        @Bindable var social = social
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                VStack(alignment: .leading, spacing: 8) {
                    Image(systemName: "person.3.fill")
                        .font(.system(size: 34, weight: .semibold))
                        .foregroundStyle(Palette.uiInk2.color)
                        .accessibilityHidden(true)
                    Text("Ты пока без клана")
                        .font(.title2.bold())
                        .fontDesign(.rounded)
                        .foregroundStyle(Palette.uiInk.color)
                    Text(
                        "В клане от 3 до 12 человек. Земли соклановцев на карте — со штриховкой."
                    )
                    .font(.body)
                    .foregroundStyle(Palette.uiInk2.color)
                }
                .contentCard()
                if let until = model.joinBlockedUntil {
                    Label(
                        "После выхода вступить можно с \(ClanModel.momentText(until))", systemImage: "hourglass"
                    )
                    .font(.subheadline)
                    .foregroundStyle(Palette.uiInk.color)
                    .contentCard(padding: 14)
                }
                SectionTitle("Вступить по коду")
                VStack(alignment: .leading, spacing: 12) {
                    CodeField(prompt: "Код от лидера, например K7M2-9QXA", text: $model.joinCode)
                        .onSubmit { Task { await model.join() } }
                    Button {
                        Task { await model.join() }
                    } label: {
                        if model.busy { ProgressView() } else { Text("Вступить") }
                    }
                    .buttonStyle(.neutral)
                    .disabled(!model.canJoin)
                    if let error = model.actionError, !social.createClanShown {
                        Text(error)
                            .font(.footnote)
                            .foregroundStyle(Palette.uiInk2.color)
                    }
                    Text("Код даёт лидер или офицер клана. Сканер QR появится вместе с камерой.")
                        .font(.footnote)
                        .foregroundStyle(Palette.uiInk2.color)
                }
                .contentCard()
                Button {
                    social.createClanShown = true
                } label: {
                    Label("Создать свой клан", systemImage: "plus.circle.fill")
                        .font(.headline)
                        .frame(maxWidth: .infinity, minHeight: 52)
                }
                .buttonStyle(.bordered)
                .tint(Palette.uiInk.color)
                .disabled(model.joinBlockedUntil != nil)
                .matchedTransitionSource(id: "clan.create", in: transition)
            }
            .padding(16)
        }
        .scrollDismissesKeyboard(.interactively)
        .refreshable { await model.load() }
        .sheet(isPresented: $social.createClanShown) {
            CreateClanSheet(model: model)
                .navigationTransition(.zoom(sourceID: "clan.create", in: transition))
        }
    }
}

/// Лист «Новый клан»: название 3–24 символа и оттенок из свободных (`GET /clans/hues`).
struct CreateClanSheet: View {
    @Bindable var model: ClanModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    VStack(alignment: .leading, spacing: 8) {
                        TextField("Название клана", text: $model.newName)
                            .font(.title3.weight(.semibold))
                            .autocorrectionDisabled()
                            .padding(.horizontal, 16)
                            .frame(minHeight: 52)
                            .background(Palette.uiCell2.color, in: .rect(cornerRadius: Radius.plaque))
                        HStack {
                            Text(
                                model.newName.isEmpty
                                    ? "Буквы, цифры, пробел, дефис." : (model.nameProblem ?? "Подходит."))
                            Spacer()
                            Text(
                                verbatim:
                                    "\(ClanNameRule.normalized(model.newName).count)/\(ClanNameRule.length.upperBound)"
                            )
                            .monospacedDigit()
                            .contentTransition(.numericText())
                        }
                        .font(.footnote)
                        .foregroundStyle(Palette.uiInk2.color)
                    }
                    .contentCard()
                    SectionTitle("Оттенок")
                    HuePicker(hues: model.freeHues, selection: $model.newHue)
                        .contentCard()
                    if let error = model.actionError {
                        Text(error)
                            .font(.footnote)
                            .foregroundStyle(Palette.uiInk2.color)
                            .padding(.horizontal, 16)
                    }
                    Text(
                        "Название меняет лидер — раз в сезон. Когда кланов больше 12, оттенки повторяются."
                    )
                    .font(.footnote)
                    .foregroundStyle(Palette.uiInk2.color)
                    .padding(.horizontal, 16)
                }
                .padding(16)
            }
            .background(Palette.uiBackground.color)
            .navigationTitle("Новый клан")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Отмена", role: .cancel) { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Создать") {
                        Task {
                            if await model.create() { dismiss() }
                        }
                    }
                    .disabled(!model.canCreate)
                }
            }
            .task { await model.loadHues() }
        }
    }
}

/// Свободные оттенки палитры кланов — кружками; выбранный — с кольцом. Палитра кланов — 12 цветов палитры игроков
/// (PLAN.md, §3.6: 12 оттенков; своих токенов у кланов пока нет).
struct HuePicker: View {
    let hues: [Int]
    @Binding var selection: Int?

    var body: some View {
        if hues.isEmpty {
            HStack(spacing: 10) {
                ProgressView()
                Text("Загружаем свободные оттенки…")
                    .font(.subheadline)
                    .foregroundStyle(Palette.uiInk2.color)
            }
        } else {
            LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 12), count: 6), spacing: 12) {
                ForEach(hues, id: \.self) { hue in
                    let color = PlayerColor(index: hue)
                    Button {
                        selection = hue
                    } label: {
                        Circle()
                            .fill(color.color)
                            .overlay { Circle().stroke(color.edgeColor, lineWidth: 2) }
                            .padding(5)
                            .overlay {
                                if selection == hue {
                                    Circle().stroke(Palette.uiInk.color, lineWidth: 2.5)
                                }
                            }
                            .frame(height: 48)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(Text(verbatim: "Оттенок \(hue + 1)"))
                    .accessibilityAddTraits(selection == hue ? .isSelected : [])
                }
            }
            .sensoryFeedback(.selection, trigger: selection)
        }
    }
}

/// Свой клан: шапка оттенка клана, код-приглашение (лидеру и офицерам), состав с ролями, выход.
struct ClanDetailView: View {
    let model: ClanModel
    let clan: ClanInfo
    @State private var leaveAsked = false
    @State private var renewAsked = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                header
                if model.showsInvite, let code = clan.inviteCode {
                    ShareCodeCard(
                        title: "Код-приглашение",
                        code: code,
                        shareText: "Вступай в клан «\(clan.name)» в «Городках»: вкладка «Клан» → код \(code)",
                        footnote: "По коду вступают сразу, без заявок. В клане не больше 12 человек.")
                    Button("Выдать новый код") { renewAsked = true }
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(Palette.uiInk.color)
                        .padding(.horizontal, 16)
                        .disabled(model.busy)
                }
                SectionTitle("Состав")
                VStack(spacing: 0) {
                    ForEach(model.members, id: \.playerId) { member in
                        MemberRow(member: member)
                        if member.playerId != model.members.last?.playerId {
                            Divider().padding(.leading, 68)
                        }
                    }
                }
                .background(Palette.uiCell.color, in: .rect(cornerRadius: Radius.card))
                if let error = model.actionError {
                    Text(error)
                        .font(.footnote)
                        .foregroundStyle(Palette.uiInk2.color)
                        .padding(.horizontal, 16)
                }
                Button("Выйти из клана", role: .destructive) { leaveAsked = true }
                    .font(.body.weight(.semibold))
                    .frame(maxWidth: .infinity, minHeight: 52)
                    .background(Palette.uiCell.color, in: .rect(cornerRadius: Radius.card))
                    .disabled(model.busy)
            }
            .padding(16)
        }
        .refreshable { await model.load() }
        .confirmationDialog("Выйти из клана?", isPresented: $leaveAsked, titleVisibility: .visible) {
            Button("Выйти", role: .destructive) { Task { await model.leave() } }
            Button("Остаться", role: .cancel) {}
        } message: {
            Text("Земля останется твоей. Вступить в другой клан можно будет через 72 часа.")
        }
        .confirmationDialog("Выдать новый код?", isPresented: $renewAsked, titleVisibility: .visible) {
            Button("Новый код") { Task { await model.renewInviteCode() } }
            Button("Отмена", role: .cancel) {}
        } message: {
            Text("Старый код перестанет действовать.")
        }
    }

    private var header: some View {
        let hue = PlayerColor(index: Int(clan.hue))
        return HStack(spacing: 16) {
            Text(String(clan.name.first.map(String.init) ?? "?"))
                .font(.system(size: 30, weight: .heavy, design: .rounded))
                .foregroundStyle(hue.startInkColor)
                .frame(width: 64, height: 64)
                .background(hue.color, in: .rect(cornerRadius: 18))
                .overlay { RoundedRectangle(cornerRadius: 18).stroke(hue.edgeColor, lineWidth: 3) }
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 4) {
                Text(verbatim: clan.name)
                    .font(.title2.bold())
                    .fontDesign(.rounded)
                    .foregroundStyle(Palette.uiInk.color)
                Text(verbatim: subtitle)
                    .font(.subheadline)
                    .foregroundStyle(Palette.uiInk2.color)
                    .contentTransition(.numericText())
                if !clan.full {
                    Label("Неполный: рейды и кварталы — с 3 человек", systemImage: "exclamationmark.circle")
                        .font(.caption)
                        .foregroundStyle(Palette.uiInk2.color)
                }
            }
        }
        .contentCard()
    }

    private var subtitle: String {
        var parts = [CountText.members(clan.members.count)]
        if let role = model.myRole {
            parts.append("ты — " + role.title.lowercased())
        }
        return parts.joined(separator: " · ")
    }
}

/// Участник: кружок цвета игрока, ник, «ты», роль.
private struct MemberRow: View {
    let member: Components.Schemas.ClanMemberResponse

    var body: some View {
        HStack(spacing: 12) {
            PlayerAvatar(name: member.name, color: PlayerColor(index: Int(member.colorIndex)))
            VStack(alignment: .leading, spacing: 2) {
                Text(verbatim: member.name)
                    .font(member.me ? .body.weight(.bold) : .body)
                    .foregroundStyle(Palette.uiInk.color)
                Text(verbatim: member.me ? member.role.title + " · ты" : member.role.title)
                    .font(.caption)
                    .foregroundStyle(Palette.uiInk2.color)
            }
            Spacer()
            if member.role == .leader {
                Image(systemName: "crown.fill")
                    .foregroundStyle(Palette.gold.color)
                    .accessibilityHidden(true)
            } else if member.role == .officer {
                Image(systemName: "star.fill")
                    .foregroundStyle(Palette.uiInk3.color)
                    .accessibilityHidden(true)
            }
        }
        .padding(.horizontal, 14)
        .frame(minHeight: 60)
        .accessibilityElement(children: .combine)
    }
}
