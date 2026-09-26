import DesignSystem
import GameCore
import SwiftUI

/// «Уведомления» (пункт 4 листика без платного аккаунта): разрешение по кнопке, напоминания «Сезон заканчивается
/// завтра» и «Забег всё ещё идёт», пробное уведомление через 5 секунд — показать на защите.
@MainActor
@Observable
final class NotificationsModel {
    var access: NotificationAccess = .notDetermined
    var seasonEnding: Bool
    var runStillGoing: Bool
    /// Напоминание о конце ближайшего сезона — когда придёт (`nil` — сезона с датой конца впереди нет).
    var seasonReminder: ReminderDraft?
    var message: String?
    var loaded = false

    @ObservationIgnored let notifications: any LocalNotifying
    @ObservationIgnored let settings: ReminderSettings
    @ObservationIgnored let seasons: @MainActor () async -> [SeasonInfo]
    @ObservationIgnored let now: () -> Double
    @ObservationIgnored let timeZone: TimeZone

    init(
        notifications: any LocalNotifying, settings: ReminderSettings,
        seasons: @escaping @MainActor () async -> [SeasonInfo],
        now: @escaping () -> Double = { Date.now.timeIntervalSince1970 }, timeZone: TimeZone = .current
    ) {
        self.notifications = notifications
        self.settings = settings
        self.seasons = seasons
        self.now = now
        self.timeZone = timeZone
        seasonEnding = settings.seasonEnding
        runStillGoing = settings.runStillGoing
    }

    static func live() -> NotificationsModel {
        NotificationsModel(
            notifications: SystemNotifications.shared, settings: .standard, seasons: { await SeasonsSource.live() })
    }

    func load() async {
        access = await notifications.access()
        let current = now()
        let upcoming = Reminders.upcomingEnd(in: await seasons(), now: current)
        seasonReminder = upcoming.flatMap { Reminders.seasonEndingTomorrow($0, now: current, timeZone: timeZone) }
        loaded = true
    }

    func requestAccess() async {
        access = await notifications.requestAccess()
        if access == .allowed {
            await scheduleSeasonReminder()
        }
    }

    func setSeasonEnding(_ on: Bool) async {
        seasonEnding = on
        settings.seasonEnding = on
        await scheduleSeasonReminder()
    }

    func setRunStillGoing(_ on: Bool) {
        runStillGoing = on
        settings.runStillGoing = on
        if !on {
            notifications.cancel(Reminders.runStillGoingID)
        }
    }

    /// Поставить или снять напоминание о конце сезона — по переключателю и разрешению.
    func scheduleSeasonReminder() async {
        guard let reminder = seasonReminder else { return }
        guard seasonEnding, access == .allowed else {
            notifications.cancel(reminder.id)
            return
        }
        do {
            try await notifications.schedule(reminder)
            message = nil
        } catch {
            message = "Не получилось поставить напоминание: \(error.localizedDescription)"
        }
    }

    /// Пробное уведомление через 5 секунд — заблокируй телефон, и оно придёт на экран блокировки.
    func sendTest() async {
        guard access == .allowed else { return }
        let test = ReminderDraft(
            id: "test", title: "Городки: проверка",
            body: "Уведомления работают — так придут напоминания о конце сезона и о забытом забеге.",
            fireAt: now() + 5)
        do {
            try await notifications.schedule(test)
            message = "Пробное уведомление придёт через 5 секунд."
        } catch {
            message = "Не получилось: \(error.localizedDescription)"
        }
    }
}

struct NotificationsView: View {
    @State private var model: NotificationsModel
    @Environment(\.openURL) private var openURL

    init(model: NotificationsModel = .live()) {
        _model = State(initialValue: model)
    }

    var body: some View {
        TokenList {
            Section {
                SheetIntro(
                    systemImage: "bell.badge", title: "Напоминания от телефона",
                    text: "Без платного аккаунта Apple push с сервера недоступен — напоминания ставит сам телефон.")
                accessRow
            }
            Section {
                Toggle(isOn: Bindable(model).seasonEnding) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Сезон заканчивается завтра")
                        Text(seasonDetail)
                            .font(.footnote)
                            .foregroundStyle(Palette.uiInk2.color)
                    }
                }
                Toggle(isOn: Bindable(model).runStillGoing) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Забег всё ещё идёт")
                        Text("Через 2 часа, если приложение закрыли, а «Финиш» не нажали.")
                            .font(.footnote)
                            .foregroundStyle(Palette.uiInk2.color)
                    }
                }
            } header: {
                Text("Напоминания")
            } footer: {
                Text("Ночью телефон молчит: напоминание о сезоне приходит в 19:00, до тишины с 22:00.")
            }
            .disabled(model.access != .allowed)
            Section {
                Button {
                    Task { await model.sendTest() }
                } label: {
                    Label("Прислать пробное через 5 секунд", systemImage: "paperplane")
                }
                .disabled(model.access != .allowed)
                if let message = model.message {
                    Text(message)
                        .font(.footnote)
                        .foregroundStyle(Palette.uiInk2.color)
                }
            } footer: {
                Text("Нажми и заблокируй телефон — уведомление придёт на экран блокировки.")
            }
        }
        .tint(Palette.uiInk.color)
        .navigationTitle("Уведомления")
        .onChange(of: model.seasonEnding) { _, on in
            Task { await model.setSeasonEnding(on) }
        }
        .onChange(of: model.runStillGoing) { _, on in
            model.setRunStillGoing(on)
        }
        .task {
            if !model.loaded { await model.load() }
        }
    }

    private var seasonDetail: String {
        guard let reminder = model.seasonReminder else { return "Конца сезона впереди пока не видно." }
        return "Накануне последнего дня: \(SeasonTime.text(reminder.fireAt, withTime: true))."
    }

    @ViewBuilder
    private var accessRow: some View {
        switch model.access {
        case .notDetermined:
            Button {
                Task { await model.requestAccess() }
            } label: {
                Label("Разрешить уведомления", systemImage: "bell")
            }
        case .denied:
            Button {
                if let url = SystemSettings.notificationsURL { openURL(url) }
            } label: {
                Label("Уведомления запрещены — открыть Настройки", systemImage: "bell.slash")
            }
        case .allowed:
            Label("Уведомления разрешены", systemImage: "checkmark.circle.fill")
                .foregroundStyle(Palette.uiInk.color)
        }
    }
}
