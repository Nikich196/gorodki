import DesignSystem
import GameCore
import GorodkiAPI
import SwiftUI
import Sync

/// Профиль до этапа 6: кто ты (ник, цвет, роль), сколько тумана открыто, отладочное меню и выход. Простые значения —
/// их задают ответы сервера (`load`) или образцы `contracts/samples` в режиме фикстур.
@MainActor
@Observable
final class ProfileModel {
    /// Номер игрока (`GET /me`, UUID) — по нему карта отличает свою землю.
    var playerId: String? = nil
    var displayName: String? = nil
    /// Номер цвета с сервера (`colorIndex`); `nil` — ещё не знаем.
    var colorIndex: Int? = nil
    var role: String? = nil
    /// Открыто тумана «Пешком» за всё время и за текущий сезон, м².
    var exploredSquareMeters: Double? = nil
    var seasonExploredSquareMeters: Double? = nil
    var seasonName: String? = nil
    var signedIn: Bool
    /// Клиент API вошедшего игрока (`LiveRoot`); `nil` — не вошёл, нет адреса сервера или режим фикстур.
    @ObservationIgnored var api: (any APIProtocol)?

    init(signedIn: Bool = false, role: String? = nil) {
        self.signedIn = signedIn
        self.role = role
    }

    var playerColor: PlayerColor {
        colorIndex.map(PlayerColor.init(index:)) ?? .blue
    }

    var debugMenuAvailable: Bool {
        DebugAccess.isAvailable(role: role)
    }

    func apply(_ me: Components.Schemas.MeResponse) {
        playerId = me.id
        displayName = me.displayName
        colorIndex = Int(me.colorIndex)
        role = me.role
    }

    /// Сводка тумана (`GET /fog/summary`): слой «Пешком» за всё время (`season == nil`) и за сезон `current`.
    func apply(_ summary: Components.Schemas.FogSummaryResponse, currentSeason: Int?) {
        let foot = summary.layers.filter { $0.layer == .foot }
        exploredSquareMeters = foot.first { $0.season == nil }?.areaSquareMeters ?? 0
        if let currentSeason {
            seasonExploredSquareMeters = foot.first { $0.season.map(Int.init) == currentSeason }?.areaSquareMeters ?? 0
        }
    }

    func apply(_ seasons: Components.Schemas.SeasonsResponse) {
        seasonName = seasons.seasons.first { seasons.current.map(Int.init) == Int($0.number) }?.name
    }

    /// Профиль ещё не загрузился (например, приложение запустилось без сети).
    var needsLoad: Bool {
        displayName == nil || exploredSquareMeters == nil
    }

    /// Загрузить заново через `api` — при входе (`LiveRoot`), по жесту «потянуть вниз» и при заходе на вкладку, если
    /// в прошлый раз не загрузилось (`ProfileTab`).
    func refresh() async {
        guard signedIn, let api else { return }
        await load(api: api)
    }

    /// Данные вошедшего игрока с сервера. Ошибки не показываются: профиль останется с тем, что уже знает, а
    /// следующий заход на вкладку или «потянуть вниз» попробуют снова (`refresh`).
    func load(api: any APIProtocol) async {
        if let me = try? await api.getMe().ok.body.json {
            apply(me)
        }
        let seasons = try? await api.getSeasons().ok.body.json
        if let seasons {
            apply(seasons)
        }
        if let summary = try? await api.getFogSummary().ok.body.json {
            apply(summary, currentSeason: seasons?.current.map(Int.init))
        }
    }
}

struct ProfileTab: View {
    let model: ProfileModel
    @State private var signOutError: String?
    @State private var signOutAsked = false
    /// Сколько забегов сотрёт выход, не дойдя до сервера (`AppDependencies.unsentRunCount`) — для вопроса перед выходом.
    @State private var unsentRuns = 0

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    header
                    HStack(spacing: 12) {
                        StatTile(title: "Открыто тумана", value: area(model.exploredSquareMeters))
                        StatTile(title: model.seasonName ?? "Сезон", value: area(model.seasonExploredSquareMeters))
                    }
                    ProfileFeatures(model: model)
                    actions
                }
                .padding(20)
            }
            .background(Palette.uiBackground.color)
            .navigationTitle("Профиль")
            .refreshable { await model.refresh() }
            .task {
                // Не загрузилось при входе (не было сети) — ещё раз при заходе на вкладку.
                if model.needsLoad { await model.refresh() }
            }
            .confirmationDialog("Выйти из аккаунта?", isPresented: $signOutAsked, titleVisibility: .visible) {
                Button("Выйти", role: .destructive) {
                    Task { await signOut() }
                }
                Button("Отмена", role: .cancel) {}
            } message: {
                Text(signOutWarning)
            }
        }
    }

    /// Что потеряется при выходе: `RunController.signOut` заканчивает забег и стирает очередь синхронизации.
    private var signOutWarning: String {
        let base = "Идущий забег закончится, а всё об игроке на этом телефоне сотрётся."
        guard unsentRuns > 0 else { return base }
        return base + " Ещё не дошли до сервера \(CountText.runs(unsentRuns)) — они пропадут."
    }

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
            VStack(alignment: .leading, spacing: 4) {
                Text(model.displayName ?? (model.signedIn ? "Игрок" : "Вход не выполнен"))
                    .font(.title2.bold())
                    .foregroundStyle(Palette.uiInk.color)
                Text(subtitle)
                    .font(.subheadline)
                    .foregroundStyle(Palette.uiInk2.color)
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

    private var actions: some View {
        VStack(spacing: 0) {
            NavigationLink {
                RunHistoryView(model: RunHistoryModel.live())
            } label: {
                ProfileRow(title: "Забеги", systemImage: "figure.run")
            }
            Divider().padding(.leading, 52)
            if model.debugMenuAvailable {
                NavigationLink {
                    DebugMenuView(profile: model)
                } label: {
                    ProfileRow(title: "Отладка", systemImage: "ladybug")
                }
                Divider().padding(.leading, 52)
            }
            if model.signedIn {
                Button {
                    Task {
                        unsentRuns = await AppDependencies.shared.unsentRunCount()
                        signOutAsked = true
                    }
                } label: {
                    ProfileRow(title: "Выйти", systemImage: "rectangle.portrait.and.arrow.right")
                }
            } else {
                Button {
                    AppSession.shared.browsingWithoutSignIn = false
                } label: {
                    ProfileRow(title: "Войти", systemImage: "person.crop.circle.badge.checkmark")
                }
            }
            if let signOutError {
                Text(signOutError)
                    .font(.footnote)
                    .foregroundStyle(Palette.uiInk2.color)
                    .padding(12)
            }
        }
        .buttonStyle(.plain)
        .background(Palette.uiCell.color, in: .rect(cornerRadius: Radius.card))
    }

    private func area(_ squareMeters: Double?) -> String {
        squareMeters.map { NumberText.hectares(fromSquareMeters: $0, fractionDigits: 2) } ?? "—"
    }

    /// Выход — как в `RunController.signOut`: идущий забег заканчивается, всё об игроке на телефоне стирается.
    private func signOut() async {
        do {
            try await RunController.shared.signOut()
            signOutError = nil
        } catch {
            let signedOut = await AppDependencies.shared.tokens.current() == nil
            let message = Self.signOutFailure(error, signedOut: signedOut)
            if message.stayedSignedIn {
                signOutError = message.text
            } else {
                // Вход уже стёрт: корень переключится на онбординг, и эта вкладка ошибку не покажет.
                AppSession.shared.notice = message.text
            }
        }
    }

    /// Текст ошибки выхода для игрока. `signedOut` — вход к этому времени уже стёрт (упало стирание данных).
    static func signOutFailure(_ error: any Error, signedOut: Bool) -> (text: String, stayedSignedIn: Bool) {
        if signedOut {
            return (
                "Ты вышел, но часть данных на телефоне стереть не удалось. Удали и поставь приложение заново — "
                    + "так сотрётся всё.", false
            )
        }
        if error as? TrackerError == .alreadyRunning {
            return ("Сначала закончи пробный забег в «Лаборатории».", true)
        }
        return ("Не получилось закончить забег, поэтому выход отменён. Попробуй ещё раз.", true)
    }
}

private struct StatTile: View {
    let title: String
    let value: String

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(value)
                .font(.role(.statTile))
                .foregroundStyle(Palette.uiInk.color)
                .lineLimit(1)
                .minimumScaleFactor(0.6)
            Text(title)
                .font(.caption)
                .foregroundStyle(Palette.uiInk2.color)
                .lineLimit(2)
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Palette.uiCell.color, in: .rect(cornerRadius: Radius.tile))
    }
}

struct ProfileRow: View {
    let title: String
    let systemImage: String

    var body: some View {
        HStack(spacing: 16) {
            Image(systemName: systemImage)
                .font(.body.weight(.semibold))
                .foregroundStyle(Palette.uiInk2.color)
                .frame(width: 24)
            Text(title)
                .font(.body)
                .foregroundStyle(Palette.uiInk.color)
            Spacer()
            Image(systemName: "chevron.right")
                .font(.footnote.weight(.semibold))
                .foregroundStyle(Palette.uiInk3.color)
        }
        .padding(.horizontal, 12)
        .frame(minHeight: 52)
        .contentShape(.rect)
    }
}
