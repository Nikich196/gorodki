import DesignSystem
import SwiftUI

/// Пункт листика курсовой (PLAN.md, §1 и §4): где его показать в приложении.
struct AssignmentItem: Identifiable, Equatable, Sendable {
    /// Статус без платного аккаунта Apple — столбец «Без Paid» таблицы §4.
    enum Status: Hashable, Sendable {
        /// ✅ реализовано.
        case done
        /// 🔁 iOS-аналог (фоновый сервис — Android-понятие).
        case analog
        /// ⚠️ частично: полностью — только с Paid.
        case partial
    }

    /// Куда ведёт строка.
    enum Screen: Hashable, Sendable {
        case backup, gallery, notifications, storage, files, calendar, invite, replay, myQR, lab, design, server
    }

    var number: Int
    var title: String
    var status: Status
    /// Где показать на защите.
    var place: String
    /// Оговорка: чего ещё нет или что только в Paid.
    var note: String?
    var screens: [Screen]

    var id: Int { number }

    /// 14 пунктов по таблице §4 PLAN.md; «где» — экраны этой сборки.
    static let all: [AssignmentItem] = [
        AssignmentItem(
            number: 1, title: "OAuth", status: .done, place: "Онбординг → «Войти через Google» → свой JWT",
            note: "Кнопка ждёт Client ID Google (#4); SIWA — только Paid", screens: []),
        AssignmentItem(
            number: 2, title: "Облачное восстановление", status: .done,
            place: "Профиль → Резервная копия: что на сервере, синхронизация", note: nil, screens: [.backup]),
        AssignmentItem(
            number: 3, title: "Медиатека", status: .done,
            place: "Профиль → Галерея: фильтры, кэш миниатюр, ограниченный доступ; видео и кадр — в «Фото»",
            note: nil, screens: [.gallery]),
        AssignmentItem(
            number: 4, title: "Push", status: .partial,
            place: "Профиль → Уведомления: локальные напоминания, пробное через 5 с",
            note: "APNs на заблокированный экран — только Paid", screens: [.notifications]),
        AssignmentItem(
            number: 5, title: "Кэш во внутренней FS", status: .done,
            place: "Профиль → Хранилище: тайлы земли и тумана, база, очистка кэша", note: nil, screens: [.storage]),
        AssignmentItem(
            number: 6, title: "Внешняя FS", status: .done,
            place:
                "Профиль → Файлы и экспорт: fileExporter/fileImporter, автоэкспорт, «Мои данные», Exports в «Файлах»",
            note: nil, screens: [.files]),
        AssignmentItem(
            number: 7, title: "Календарь и контакты", status: .done,
            place: "Профиль → Календарь (только запись) и Пригласить друга (ContactAccessButton)", note: nil,
            screens: [.calendar, .invite]),
        AssignmentItem(
            number: 8, title: "Фоновый сервис", status: .analog,
            place: "Видео-повтор → «Собрать видео»: BGContinuedProcessingTask с системным прогрессом",
            note: "Плюс фоновая досылка забегов (BGAppRefresh, BGProcessing)", screens: [.replay]),
        AssignmentItem(
            number: 9, title: "PiP-видео", status: .done,
            place: "Видео-повтор → «Картинка в картинке»: окно поверх Карт", note: nil, screens: [.replay]),
        AssignmentItem(
            number: 10, title: "Фоновая геолокация", status: .done,
            place: "Лаборатория → Прогулка и Пробный забег: запись на заблокированном экране", note: nil,
            screens: [.lab]),
        AssignmentItem(
            number: 11, title: "Дизайн-язык ОС", status: .done,
            place: "Весь интерфейс: Liquid Glass iOS 26+; Лаборатория → Дизайн", note: nil, screens: [.design]),
        AssignmentItem(
            number: 12, title: "Сложный интерфейс", status: .done,
            place: "Галерея (сетка с переходом-зумом), Хранилище (диаграмма), карта",
            note: "Лента, Коллекция и Рюкзак — следующие экраны", screens: [.gallery, .storage]),
        AssignmentItem(
            number: 13, title: "NFC и камера", status: .partial,
            place: "Профиль → Мой QR → Сканировать: своя камера AVFoundation, QR, снимок", note: "NFC — только Paid",
            screens: [.myQR]),
        AssignmentItem(
            number: 14, title: "Сложный сервер", status: .done,
            place: "Лаборатория → Сервер: связь, вход, реальное время; геодвижок и тесты — в репозитории", note: nil,
            screens: [.server]),
    ]
}

/// «Отладка → Пункты задания»: 14 пунктов листика со статусом по таблице §4 и переходом на экран, где каждый
/// показывается, — чтобы на защите пройти по листику подряд.
struct AssignmentSheetView: View {
    var player: PlayerColor = .blue
    var playerId: String?
    var playerName: String?
    @State private var appeared = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        TokenList {
            Section {
                summary
            }
            ForEach(AssignmentItem.all) { item in
                Section {
                    row(item)
                    ForEach(item.screens, id: \.self) { screen in
                        NavigationLink {
                            destination(screen)
                        } label: {
                            Label(screen.title, systemImage: screen.symbolName)
                                .foregroundStyle(Palette.uiInk.color)
                        }
                    }
                }
            }
        }
        .navigationTitle("Пункты задания")
        .onAppear {
            if !reduceMotion { appeared = true }
        }
    }

    private var summary: some View {
        let counts = Dictionary(grouping: AssignmentItem.all, by: \.status).mapValues(\.count)
        return HStack(spacing: 0) {
            ForEach([AssignmentItem.Status.done, .analog, .partial], id: \.self) { status in
                VStack(spacing: 4) {
                    Text(NumberText.integer(counts[status] ?? 0))
                        .font(.role(.statTile))
                        .foregroundStyle(Palette.uiInk.color)
                    Text(status.title)
                        .font(.caption)
                        .foregroundStyle(Palette.uiInk2.color)
                        .multilineTextAlignment(.center)
                }
                .frame(maxWidth: .infinity)
            }
        }
        .padding(.vertical, 6)
        .accessibilityElement(children: .combine)
    }

    private func row(_ item: AssignmentItem) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: item.status.symbolName)
                .font(.title3)
                .foregroundStyle(item.status.color)
                .symbolEffect(.bounce.down.byLayer, options: .nonRepeating, value: appeared)
                .frame(width: 28)
                .accessibilityLabel(item.status.title)
            VStack(alignment: .leading, spacing: 4) {
                Text("\(item.number). \(item.title)")
                    .font(.headline)
                    .foregroundStyle(Palette.uiInk.color)
                Text(item.place)
                    .font(.subheadline)
                    .foregroundStyle(Palette.uiInk2.color)
                if let note = item.note {
                    Text(note)
                        .font(.caption)
                        .foregroundStyle(Palette.uiInk3.color)
                }
            }
        }
        .padding(.vertical, 2)
    }

    @ViewBuilder
    private func destination(_ screen: AssignmentItem.Screen) -> some View {
        switch screen {
        case .backup: BackupView()
        case .gallery: GalleryView()
        case .notifications: NotificationsView()
        case .storage: StorageView()
        case .files: FilesView()
        case .calendar: CalendarView()
        case .invite: InviteView(model: .live(senderName: playerName))
        case .replay: ReplayView(model: .live(player: player))
        case .myQR: MyQRView(playerId: playerId, playerName: playerName, player: player)
        case .lab: LabView()
        case .design: DesignLabView()
        case .server: ServerLabView(dependencies: .shared)
        }
    }
}

extension AssignmentItem.Status {
    var title: String {
        switch self {
        case .done: "сделано"
        case .analog: "iOS-аналог"
        case .partial: "частично, полностью — с Paid"
        }
    }

    var symbolName: String {
        switch self {
        case .done: "checkmark.seal.fill"
        case .analog: "arrow.triangle.2.circlepath.circle.fill"
        case .partial: "exclamationmark.triangle.fill"
        }
    }

    var color: Color {
        switch self {
        case .done: Palette.uiInk.color
        case .analog: FogStyle.exploreFill.color
        case .partial: Palette.warn.color
        }
    }
}

extension AssignmentItem.Screen {
    var title: String {
        switch self {
        case .backup: "Резервная копия"
        case .gallery: "Галерея"
        case .notifications: "Уведомления"
        case .storage: "Хранилище"
        case .files: "Файлы и экспорт"
        case .calendar: "Календарь"
        case .invite: "Пригласить друга"
        case .replay: "Видео-повтор"
        case .myQR: "Мой QR и сканер"
        case .lab: "Лаборатория"
        case .design: "Лаборатория → Дизайн"
        case .server: "Лаборатория → Сервер"
        }
    }

    var symbolName: String {
        switch self {
        case .backup: "icloud.and.arrow.up"
        case .gallery: "photo.on.rectangle"
        case .notifications: "bell.badge"
        case .storage: "internaldrive"
        case .files: "folder"
        case .calendar: "calendar"
        case .invite: "person.badge.plus"
        case .replay: "play.rectangle"
        case .myQR: "qrcode"
        case .lab: "flask"
        case .design: "paintpalette"
        case .server: "network"
        }
    }
}
