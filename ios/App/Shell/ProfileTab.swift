import DesignSystem
import GameCore
import GorodkiAPI
import Networking
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
    // Профиль, настройки, статистика (docs/architecture/ios-app.md, «Профиль и настройки»).
    /// Согласие на показ ника (`publicProfile` в `GET /me`); `nil` — ещё не знаем.
    var publicProfile: Bool? = nil
    /// Забеги, засчитанные метры и место в «Кто открыл больше» (`GET /me/stats`); `nil` — сервер не ответил.
    var runs: Int? = nil
    var distanceMeters: Double? = nil
    var explorationRank: Int? = nil
    /// Текущий сезон и его день («день 10 из 14»).
    var season: SeasonProgress? = nil
    /// Статистика «Исследования» из той же сводки тумана — экран 15 открывается с ней сразу.
    var exploration: ExplorationSummary? = nil
    /// Почему профиль не загрузился (нет сети, сервер недоступен); `nil` — загрузился или ещё не пробовали.
    var loadFailure: RequestFailure? = nil
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
        publicProfile = me.publicProfile
    }

    func apply(_ stats: Components.Schemas.MyStatsResponse) {
        runs = Int(stats.runs)
        distanceMeters = stats.distanceMeters
        explorationRank = stats.explorationRank.map(Int.init)
    }

    /// Сводка тумана (`GET /fog/summary`): слой «Пешком» за всё время (`season == nil`) и за сезон `current`.
    func apply(
        _ summary: Components.Schemas.FogSummaryResponse, currentSeason: Int?,
        seasons: Components.Schemas.SeasonsResponse? = nil
    ) {
        exploration = ExplorationSummary(summary: summary, seasons: seasons)
        let foot = summary.layers.filter { $0.layer == .foot }
        exploredSquareMeters = foot.first { $0.season == nil }?.areaSquareMeters ?? 0
        if let currentSeason {
            seasonExploredSquareMeters = foot.first { $0.season.map(Int.init) == currentSeason }?.areaSquareMeters ?? 0
        }
    }

    func apply(
        _ seasons: Components.Schemas.SeasonsResponse, nowMs: Int64 = Int64(Date.now.timeIntervalSince1970 * 1_000)
    ) {
        seasonName = seasons.seasons.first { seasons.current.map(Int.init) == Int($0.number) }?.name
        season = SeasonProgress(seasons: seasons, nowMs: nowMs)
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

    /// Данные вошедшего игрока с сервера. Профиль остаётся с тем, что уже знает; не ответил `GET /me` — причина
    /// в `loadFailure` (карточка «Нет сети» в профиле), следующий заход на вкладку или «потянуть вниз» попробуют снова.
    func load(api: any APIProtocol) async {
        do {
            switch try await api.getMe() {
            case .ok(let ok): apply(try ok.body.json)
            case .notFound: throw AccountServiceError.notFound
            case .undocumented(let status, _): throw AccountServiceError.unexpectedStatus(status)
            }
            loadFailure = nil
        } catch {
            guard !Task.isCancelled else { return }
            loadFailure = RequestFailure(error)
            return
        }
        let seasons = try? await api.getSeasons().ok.body.json
        if let seasons {
            apply(seasons)
        }
        if let summary = try? await api.getFogSummary().ok.body.json {
            apply(summary, currentSeason: seasons?.current.map(Int.init), seasons: seasons)
        }
        // Своя статистика — только числа; сервер без неё (задача #116) — плитки с «—».
        if let stats = try? await api.getMyStats().ok.body.json {
            apply(stats)
        }
    }
}

struct ProfileTab: View {
    let model: ProfileModel
    /// «Дом» для «Настроек»; `nil` — только в памяти.
    var home: HomeModel? = nil
    /// После «Очистить историю исследований»: карта перезапрашивает туман, профиль — сводку.
    var onFogCleared: (() -> Void)? = nil

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    ProfileOverview(model: model)
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
        }
    }

    /// «Настройки» с тем же профилем и «Домом», что у карты.
    private func settings() -> SettingsModel {
        let settings = SettingsModel.live(profile: model, home: home ?? HomeModel())
        settings.onFogCleared = onFogCleared
        return settings
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
                    DebugMenuView()
                } label: {
                    ProfileRow(title: "Отладка", systemImage: "ladybug")
                }
                Divider().padding(.leading, 52)
            }
            NavigationLink {
                SettingsView(model: settings())
            } label: {
                ProfileRow(title: "Настройки", systemImage: "gearshape")
            }
            if !model.signedIn {
                Divider().padding(.leading, 52)
                Button {
                    AppSession.shared.browsingWithoutSignIn = false
                } label: {
                    ProfileRow(title: "Войти", systemImage: "person.crop.circle.badge.checkmark")
                }
            }
        }
        .buttonStyle(.plain)
        .background(Palette.uiCell.color, in: .rect(cornerRadius: Radius.card))
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

struct StatTile: View {
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

private struct ProfileRow: View {
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
