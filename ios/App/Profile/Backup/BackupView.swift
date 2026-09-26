import DesignSystem
import GameCore
import GorodkiAPI
import SwiftUI
import Sync

/// Что хранит сервер об игроке — из `GET /me`, `/me/stats` и зон приватности.
struct BackupServerSummary: Equatable, Sendable {
    var displayName: String
    var publicProfile: Bool
    var runs: Int
    var distanceMeters: Double
    var exploredSquareMeters: Double
    var explorationRank: Int?
    /// `nil` — не узнали (нет связи с этим запросом).
    var privacyZones: Int?
}

/// Что ещё не дошло до сервера.
struct BackupQueue: Equatable, Sendable {
    var unsentRuns = 0
    var unsettledClaims = 0
}

/// Откуда «Резервная копия» берёт данные: в приложении — сервер и очередь, в фикстурах — числа.
struct BackupSource: Sendable {
    /// `nil` — сервер не ответил, не задан или вход не выполнен.
    var summary: @Sendable () async -> BackupServerSummary?
    var queue: @Sendable () async -> BackupQueue
    var log: @Sendable () -> SyncLog.State
    /// Проход синхронизации сейчас; `false` — синхронизации нет (адрес сервера не задан или вход не выполнен).
    var syncNow: @Sendable () async -> Bool

    static let live = BackupSource(
        summary: {
            let dependencies = AppDependencies.shared
            guard let api = dependencies.api, let me = try? await api.getMe().ok.body.json else { return nil }
            let stats = try? await api.getMyStats().ok.body.json
            let zones = try? await dependencies.account?.privacyZones().count
            return BackupServerSummary(
                displayName: me.displayName, publicProfile: me.publicProfile, runs: Int(stats?.runs ?? 0),
                distanceMeters: stats?.distanceMeters ?? 0, exploredSquareMeters: stats?.exploredSquareMeters ?? 0,
                explorationRank: stats?.explorationRank.map(Int.init), privacyZones: zones)
        },
        queue: {
            let dependencies = AppDependencies.shared
            var queue = BackupQueue(unsentRuns: await dependencies.unsentRunCount())
            if let owner = await dependencies.tokens.current()?.playerId {
                queue.unsettledClaims =
                    (try? await SyncBacklog.of(dependencies.syncStore, ownerId: owner).unsettledClaims) ?? 0
            }
            return queue
        },
        log: { AppDependencies.shared.syncLog.current },
        syncNow: {
            guard let scheduler = await AppDependencies.shared.syncScheduler() else { return false }
            await scheduler.trigger(.manual)
            return true
        })
}

/// «Резервная копия» (пункт 2 листика: облачное восстановление): что хранится на сервере, когда телефон последний
/// раз синхронизировался и чем кончилось, что ещё не отправлено, «Синхронизировать сейчас».
@MainActor
@Observable
final class BackupModel {
    var summary: BackupServerSummary?
    var queue = BackupQueue()
    var log = SyncLog.State()
    var loaded = false
    var syncing = false
    var message: String?

    @ObservationIgnored let source: BackupSource

    init(source: BackupSource) {
        self.source = source
    }

    static func live() -> BackupModel { BackupModel(source: .live) }

    func load() async {
        async let summary = source.summary()
        async let queue = source.queue()
        self.summary = await summary
        self.queue = await queue
        log = source.log()
        loaded = true
    }

    func syncNow() async {
        syncing = true
        let ran = await source.syncNow()
        await load()
        syncing = false
        message = ran ? log.last?.summary : "Синхронизация начнётся после входа: адрес сервера или вход ещё не настроены."
    }
}

struct BackupView: View {
    @State private var model: BackupModel
    private let ru = Locale(identifier: "ru_RU")

    init(model: BackupModel = .live()) {
        _model = State(initialValue: model)
    }

    var body: some View {
        TokenList {
            Section {
                SheetIntro(
                    systemImage: "icloud.and.arrow.up", title: "Копия — на сервере",
                    text: "Сервер — источник истины. Войди на другом iPhone — земля, туман и забеги вернутся.")
            }
            serverSection
            syncSection
        }
        .navigationTitle("Резервная копия")
        .refreshable { await model.load() }
        .task {
            if !model.loaded { await model.load() }
        }
    }

    @ViewBuilder
    private var serverSection: some View {
        Section {
            if let summary = model.summary {
                ValueRow(
                    title: "Забеги",
                    value: CountText.runs(summary.runs) + " · "
                        + NumberText.kilometers(fromMeters: summary.distanceMeters, fractionDigits: 1),
                    systemImage: "figure.run")
                ValueRow(
                    title: "Туман «Пешком»", value: exploredText(summary), systemImage: "cloud.fog")
                ValueRow(title: "Земля", value: "с картой после входа", systemImage: "map")
                ValueRow(
                    title: "Настройки",
                    value: summary.displayName + (summary.publicProfile ? " · профиль открыт" : " · профиль скрыт"),
                    systemImage: "person.crop.circle")
                if let zones = summary.privacyZones {
                    ValueRow(title: "Зоны приватности", value: NumberText.integer(zones), systemImage: "eye.slash")
                }
            } else if model.loaded {
                Label("Сервер не ответил или вход не выполнен — что на сервере, не видно.", systemImage: "wifi.slash")
                    .foregroundStyle(Palette.uiInk2.color)
            } else {
                ProgressView()
            }
        } header: {
            Text("На сервере")
        } footer: {
            Text("Вход на новый телефон не переезжает — войти нужно заново, остальное придёт с сервера.")
        }
    }

    private var syncSection: some View {
        Section {
            ValueRow(title: "Последняя", value: lastPassText, systemImage: "clock.arrow.circlepath")
            ValueRow(title: "Последняя удачная", value: lastSuccessText, systemImage: "checkmark.icloud")
            ValueRow(
                title: "Ждут отправки", value: CountText.runs(model.queue.unsentRuns), systemImage: "tray.and.arrow.up")
            ValueRow(
                title: "Петли ждут итога", value: NumberText.integer(model.queue.unsettledClaims),
                systemImage: "hourglass")
            Button {
                Task { await model.syncNow() }
            } label: {
                HStack {
                    Label("Синхронизировать сейчас", systemImage: "arrow.triangle.2.circlepath")
                    Spacer()
                    if model.syncing {
                        ProgressView()
                    }
                }
            }
            .disabled(model.syncing)
            if let message = model.message {
                Text(message)
                    .font(.footnote)
                    .foregroundStyle(Palette.uiInk2.color)
            }
        } header: {
            Text("Синхронизация")
        } footer: {
            Text("Забеги копятся на телефоне и уходят сами, как появится связь; очистка кэша их не трогает.")
        }
        .tint(Palette.uiInk.color)
    }

    private func exploredText(_ summary: BackupServerSummary) -> String {
        let area = NumberText.hectares(fromSquareMeters: summary.exploredSquareMeters, fractionDigits: 1)
        guard let rank = summary.explorationRank else { return area }
        return area + " · место " + NumberText.integer(rank)
    }

    private var lastPassText: String {
        guard let last = model.log.last else { return "ещё не было" }
        return relative(last.atMs) + " · " + last.summary
    }

    private var lastSuccessText: String {
        model.log.lastSuccessAtMs.map(relative) ?? "ещё не было"
    }

    private func relative(_ ms: Int64) -> String {
        Date(unix: Double(ms) / 1_000).formatted(.relative(presentation: .named).locale(ru))
    }
}
