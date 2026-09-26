import DesignSystem
import GameCore
import Networking
import Observation
import SwiftUI
import Sync

/// Что «Настройки» просят у сервера и у телефона — через протокол, чтобы модель проверялась без сети.
@MainActor
protocol SettingsAccount {
    /// `PUT /me/public-profile` → согласие, которое записал сервер.
    func setPublicProfile(_ enabled: Bool) async throws -> Bool
    /// `DELETE /fog` и перезапрос своего тумана.
    func clearExplorationHistory() async throws
    /// Закончить забег, удалить аккаунт (`DELETE /me`) и стереть телефон → когда сервер сотрёт данные, мс Unix.
    func deleteAccount() async throws -> Int64?
    /// Закончить забег, отозвать вход, стереть телефон (`RunController.signOut`).
    func signOut() async throws
}

/// Настоящие запросы: `AccountService`, забег — `RunController`, стирание — `AppDependencies`.
@MainActor
struct LiveSettingsAccount: SettingsAccount {
    let account: AccountService

    func setPublicProfile(_ enabled: Bool) async throws -> Bool {
        try await account.setPublicProfile(enabled).publicProfile
    }

    func clearExplorationHistory() async throws {
        try await account.clearExplorationHistory()
        // Подсказка `FogChanged` придёт и сама, но карта должна перезапросить туман сразу: свои тайлы — со своей версией,
        // сервер отдаст их пустыми.
        await AppDependencies.shared.fog?.invalidate()
    }

    func deleteAccount() async throws -> Int64? {
        // Как при выходе: идущий забег — закончить, пробный «Лаборатории» — тоже, иначе они писали бы куски стёртого.
        if RunController.shared.state.isRunning {
            try await RunController.shared.finish()
        }
        await ProbeRun.shared.finish()
        guard await !ProbeRun.shared.isOccupied() else { throw TrackerError.alreadyRunning }
        return try await AppDependencies.shared.deleteAccount()?.deleteByMs
    }

    func signOut() async throws {
        try await RunController.shared.signOut()
    }
}

/// «Настройки» (PLAN.md, §5, экран 26): «Сменить Дом», приватные зоны, «Очистить историю исследований», согласие
/// на показ ника, документы, выход и удаление аккаунта. Простые значения; запросы — `SettingsAccount`.
@MainActor
@Observable
final class SettingsModel {
    /// Чем кончилась очистка истории исследований.
    enum ClearResult: Equatable {
        case done
        case failed(RequestFailure)
    }

    let profile: ProfileModel
    let home: HomeModel
    private(set) var savingPublicProfile = false
    var publicProfileError: RequestFailure?
    private(set) var clearing = false
    var clearResult: ClearResult?
    private(set) var deleting = false
    var deleteError: String?
    private(set) var signingOut = false
    var signOutError: String?
    /// Растёт после удачного действия — вибрация `.success`.
    private(set) var successes = 0
    /// После очистки тумана: карта перезапрашивает туман, профиль — сводку.
    @ObservationIgnored var onFogCleared: (() -> Void)?
    @ObservationIgnored private let account: (any SettingsAccount)?
    /// «Сейчас», мс Unix — для срока стирания в тексте.
    @ObservationIgnored private let now: () -> Int64

    init(
        profile: ProfileModel, home: HomeModel, account: (any SettingsAccount)?,
        now: @escaping () -> Int64 = { Int64(Date.now.timeIntervalSince1970 * 1_000) }
    ) {
        self.profile = profile
        self.home = home
        self.account = account
        self.now = now
    }

    /// Настройки приложения; адреса сервера нет — без запросов (кнопки сервера выключены).
    static func live(profile: ProfileModel, home: HomeModel) -> SettingsModel {
        SettingsModel(
            profile: profile, home: home,
            account: AppDependencies.shared.account.map { LiveSettingsAccount(account: $0) })
    }

    /// Запросы к серверу возможны: вошёл и адрес сервера задан.
    var serverReady: Bool {
        profile.signedIn && account != nil
    }

    var publicProfile: Bool { profile.publicProfile ?? false }

    /// Согласие на показ ника: сразу на экране, сервер отказал — обратно, с причиной.
    func setPublicProfile(_ enabled: Bool) async {
        guard let account, !savingPublicProfile, enabled != publicProfile else { return }
        let previous = profile.publicProfile
        profile.publicProfile = enabled
        savingPublicProfile = true
        defer { savingPublicProfile = false }
        do {
            profile.publicProfile = try await account.setPublicProfile(enabled)
            publicProfileError = nil
            successes += 1
        } catch {
            profile.publicProfile = previous
            publicProfileError = RequestFailure(error)
        }
    }

    func clearExplorationHistory() async {
        guard let account, !clearing else { return }
        clearing = true
        defer { clearing = false }
        do {
            try await account.clearExplorationHistory()
            clearResult = .done
            successes += 1
            onFogCleared?()
        } catch {
            clearResult = .failed(RequestFailure(error))
        }
    }

    /// Удалить аккаунт. Удалось — вход стёрт, корень покажет онбординг и сообщение со сроком стирания на сервере.
    func deleteAccount() async {
        guard let account, !deleting else { return }
        deleting = true
        defer { deleting = false }
        do {
            let deleteBy = try await account.deleteAccount()
            deleteError = nil
            AppSession.shared.show(
                notice: Self.deletedNotice(deleteByMs: deleteBy, now: now()), title: "Аккаунт удалён")
        } catch {
            deleteError = Self.deleteFailure(error)
        }
    }

    /// Выход — как было в профиле: забег заканчивается, всё об игроке на телефоне стирается.
    func signOut() async {
        guard let account, !signingOut else { return }
        signingOut = true
        defer { signingOut = false }
        do {
            try await account.signOut()
            signOutError = nil
        } catch {
            let signedOut = await AppDependencies.shared.tokens.current() == nil
            let message = ProfileTab.signOutFailure(error, signedOut: signedOut)
            if message.stayedSignedIn {
                signOutError = message.text
            } else {
                // Вход уже стёрт: корень переключится на онбординг, и этот экран ошибку не покажет.
                AppSession.shared.show(notice: message.text, title: "Выход")
            }
        }
    }

    /// «Аккаунт удалён…» со сроком от сервера.
    static func deletedNotice(deleteByMs: Int64?, now: Int64, timeZone: TimeZone = .current) -> String {
        let base = "На телефоне всё стёрто."
        guard let deleteByMs else { return base + " На сервере аккаунта уже нет." }
        return base + " Сервер сотрёт землю, забеги, захваты и туман "
            + MomentText.until(deleteByMs, now: now, timeZone: timeZone) + "."
    }

    /// Почему удалить не вышло — на телефоне ничего не стёрто.
    static func deleteFailure(_ error: any Error) -> String {
        if error as? TrackerError == .alreadyRunning {
            return "Сначала закончи пробный забег в «Лаборатории»."
        }
        if error is TrackerError {
            return "Не получилось закончить забег, поэтому удаление отменено. Попробуй ещё раз."
        }
        return RequestFailure(error).message + " Аккаунт не удалён, на телефоне ничего не стёрто."
    }
}

/// Экран «Настройки» — контентный слой, без стекла: ячейки `ui-cell` на `ui-bg`.
struct SettingsView: View {
    @State var model: SettingsModel
    @State private var clearAsked = false
    @State private var deleteAsked = false
    @State private var signOutAsked = false
    @State private var unsentRuns = 0
    @State private var document: LegalDocument.Name?

    var body: some View {
        List {
            mapSection
            privacySection
            documentsSection
            accountSection
        }
        .scrollContentBackground(.hidden)
        .background(Palette.uiBackground.color)
        .navigationTitle("Настройки")
        .navigationBarTitleDisplayMode(.inline)
        .sensoryFeedback(.success, trigger: model.successes)
        .sheet(item: $document) { name in
            LegalDocumentSheet(name: name)
        }
        .confirmationDialog("Очистить историю исследований?", isPresented: $clearAsked, titleVisibility: .visible) {
            Button("Очистить", role: .destructive) {
                Task { await model.clearExplorationHistory() }
            }
            Button("Отмена", role: .cancel) {}
        } message: {
            Text(
                "Весь твой туман — «Пешком» и «Вело», за всё время и по сезонам — снова закроется, места в «Кто открыл "
                    + "больше» сотрутся. Вернуть нельзя. Земля, забеги и «Дом» останутся.")
        }
        .confirmationDialog("Удалить аккаунт навсегда?", isPresented: $deleteAsked, titleVisibility: .visible) {
            Button("Удалить аккаунт", role: .destructive) {
                Task { await model.deleteAccount() }
            }
            Button("Отмена", role: .cancel) {}
        } message: {
            Text(
                "Земля, забеги с точками, захваты и туман сотрутся на сервере — по закону не позже 15 дней. Вернуть "
                    + "их нельзя. На телефоне всё сотрётся сразу, идущий забег закончится.")
        }
        .confirmationDialog("Выйти из аккаунта?", isPresented: $signOutAsked, titleVisibility: .visible) {
            Button("Выйти", role: .destructive) {
                Task { await model.signOut() }
            }
            Button("Отмена", role: .cancel) {}
        } message: {
            Text(signOutWarning)
        }
    }

    // MARK: - Разделы

    private var mapSection: some View {
        Section {
            NavigationLink {
                HomeView(model: model.home)
            } label: {
                SettingsRow(
                    "«Дом»", systemImage: "house",
                    value: model.home.home == nil ? "не поставлен" : "стоит")
            }
            NavigationLink {
                PrivacyZonesView(model: .live())
            } label: {
                SettingsRow("Приватные зоны", systemImage: "eye.slash")
            }
            Button {
                clearAsked = true
            } label: {
                HStack {
                    SettingsRow("Очистить историю исследований", systemImage: "cloud.fog", destructive: true)
                    if model.clearing { ProgressView() }
                }
            }
            .disabled(!model.serverReady || model.clearing)
        } header: {
            Text("Карта и исследование")
        } footer: {
            switch model.clearResult {
            case .done:
                Text("История исследований очищена: туман снова закрыт. Круг «Дома» остался — он только на телефоне.")
            case .failed(let failure):
                Text(failure.message)
            case nil:
                Text("«Дом» и его круг хранятся только на этом телефоне.")
            }
        }
        .listRowBackground(Palette.uiCell.color)
    }

    private var privacySection: some View {
        Section {
            Toggle(
                isOn: Binding(
                    get: { model.publicProfile },
                    set: { enabled in Task { await model.setPublicProfile(enabled) } })
            ) {
                SettingsRow("Показывать мой ник", systemImage: "person.text.rectangle")
            }
            .tint(Palette.uiInk.color)
            .disabled(!model.serverReady || model.savingPublicProfile)
        } header: {
            Text("Приватность")
        } footer: {
            if let failure = model.publicProfileError {
                Text("Не сохранилось: \(failure.message)")
            } else {
                Text(
                    "Отдельное согласие на показ ника, цвета и земли по нику. Без него в рейтингах и на карте у других "
                        + "ты — «Игрок #1234».")
            }
        }
        .listRowBackground(Palette.uiCell.color)
    }

    private var documentsSection: some View {
        Section("Документы") {
            ForEach(LegalDocument.Name.allCases) { name in
                Button {
                    document = name
                } label: {
                    SettingsRow(Self.documentTitle(name), systemImage: "doc.text", chevron: true)
                }
            }
        }
        .listRowBackground(Palette.uiCell.color)
    }

    private var accountSection: some View {
        Section {
            if model.profile.signedIn {
                Button {
                    Task {
                        unsentRuns = await AppDependencies.shared.unsentRunCount()
                        signOutAsked = true
                    }
                } label: {
                    HStack {
                        SettingsRow("Выйти", systemImage: "rectangle.portrait.and.arrow.right")
                        if model.signingOut { ProgressView() }
                    }
                }
                .disabled(model.signingOut)
                Button {
                    deleteAsked = true
                } label: {
                    HStack {
                        SettingsRow("Удалить аккаунт", systemImage: "trash", destructive: true)
                        if model.deleting { ProgressView() }
                    }
                }
                .disabled(!model.serverReady || model.deleting)
            }
        } header: {
            Text("Аккаунт")
        } footer: {
            VStack(alignment: .leading, spacing: 8) {
                if let error = model.signOutError ?? model.deleteError {
                    Text(error)
                }
                Text("Городки \(AppDependencies.appVersion)")
                    .monospacedDigit()
            }
        }
        .listRowBackground(Palette.uiCell.color)
    }

    /// Что потеряется при выходе: `RunController.signOut` заканчивает забег и стирает очередь синхронизации.
    private var signOutWarning: String {
        let base = "Идущий забег закончится, а всё об игроке на этом телефоне сотрётся."
        guard unsentRuns > 0 else { return base }
        return base + " Ещё не дошли до сервера \(CountText.runs(unsentRuns)) — они пропадут."
    }

    private static func documentTitle(_ name: LegalDocument.Name) -> String {
        switch name {
        case .terms: "Пользовательское соглашение"
        case .privacy: "Политика обработки данных"
        case .consent: "Согласие на обработку данных"
        }
    }
}

/// Строка настроек: значок, название, справа — значение.
private struct SettingsRow: View {
    let title: String
    let systemImage: String
    var value: String?
    var destructive = false
    var chevron = false

    init(_ title: String, systemImage: String, value: String? = nil, destructive: Bool = false, chevron: Bool = false) {
        self.title = title
        self.systemImage = systemImage
        self.value = value
        self.destructive = destructive
        self.chevron = chevron
    }

    var body: some View {
        HStack(spacing: 14) {
            Image(systemName: systemImage)
                .font(.body.weight(.semibold))
                .foregroundStyle(destructive ? Color.red : Palette.uiInk2.color)
                .frame(width: 26)
                .accessibilityHidden(true)
            Text(title)
                .foregroundStyle(destructive ? Color.red : Palette.uiInk.color)
            Spacer(minLength: 8)
            if let value {
                Text(value)
                    .foregroundStyle(Palette.uiInk2.color)
            }
            if chevron {
                Image(systemName: "chevron.right")
                    .font(.footnote.weight(.semibold))
                    .foregroundStyle(Palette.uiInk3.color)
                    .accessibilityHidden(true)
            }
        }
        .frame(minHeight: 36)
        .contentShape(.rect)
    }
}
