import DesignSystem
import EventKit
import GameCore
import SwiftUI

/// Итог записи события в Календарь.
enum CalendarWriteResult: Equatable, Sendable {
    case added
    case denied
    case failed(String)
}

/// Календарь — только запись (пункт 7 листика; PLAN.md, §6.6): игра добавляет событие, но не читает календарь игрока.
@MainActor
protocol CalendarWriting {
    func add(_ event: CalendarEventDraft) async -> CalendarWriteResult
}

/// EventKit: `requestWriteOnlyAccessToEvents` — системный запрос только на добавление событий.
struct EventKitCalendar: CalendarWriting {
    func add(_ event: CalendarEventDraft) async -> CalendarWriteResult {
        await Self.save(event)
    }

    /// Хранилище событий создаётся и живёт внутри одной функции: `EKEventStore` не `Sendable`, между акторами его
    /// не передать.
    @concurrent
    nonisolated
        private static func save(_ draft: CalendarEventDraft) async -> CalendarWriteResult
    {
        let store = EKEventStore()
        do {
            guard try await store.requestWriteOnlyAccessToEvents() else { return .denied }
            let event = EKEvent(eventStore: store)
            event.title = draft.title
            event.notes = draft.notes
            event.startDate = Date(unix: draft.start)
            event.endDate = Date(unix: draft.end)
            event.addAlarm(EKAlarm(relativeOffset: -draft.alarmBefore))
            event.calendar = store.defaultCalendarForNewEvents
            try store.save(event, span: .thisEvent)
            return .added
        } catch {
            return .failed(error.localizedDescription)
        }
    }
}

/// «Календарь»: сезоны с сервера (`GET /seasons`, в фикстурах — образец) и «В Календарь» — конец сезона событием
/// с напоминанием за сутки. Что уже добавлено, помнит UserDefaults: прочитать календарь игра не может.
@MainActor
@Observable
final class CalendarModel {
    var seasons: [SeasonInfo] = []
    var added: Set<Int>
    var loaded = false
    var working: Int?
    var message: String?

    @ObservationIgnored let calendar: any CalendarWriting
    @ObservationIgnored let loadSeasons: @MainActor () async -> [SeasonInfo]
    @ObservationIgnored let now: () -> Double
    @ObservationIgnored let defaults: UserDefaults
    private static let addedKey = "calendar.addedSeasons"

    init(
        calendar: any CalendarWriting, seasons: @escaping @MainActor () async -> [SeasonInfo],
        defaults: UserDefaults = .standard, now: @escaping () -> Double = { Date.now.timeIntervalSince1970 }
    ) {
        self.calendar = calendar
        self.loadSeasons = seasons
        self.defaults = defaults
        self.now = now
        added = Set(defaults.array(forKey: Self.addedKey) as? [Int] ?? [])
    }

    static func live() -> CalendarModel {
        CalendarModel(calendar: EventKitCalendar(), seasons: { await SeasonsSource.live() })
    }

    func load() async {
        seasons = await loadSeasons()
        loaded = true
    }

    enum Phase: Equatable {
        case upcoming, going, over
    }

    func phase(of season: SeasonInfo) -> Phase {
        let current = now()
        if Double(season.startsAtMs) / 1_000 > current { return .upcoming }
        if let end = season.endsAt, end <= current { return .over }
        return .going
    }

    func add(_ season: SeasonInfo) async {
        guard let draft = Reminders.calendarEvent(for: season) else { return }
        working = season.number
        defer { working = nil }
        switch await calendar.add(draft) {
        case .added:
            added.insert(season.number)
            defaults.set(Array(added).sorted(), forKey: Self.addedKey)
            message = "«\(season.name)»: конец сезона в Календаре, напоминание — за сутки."
        case .denied:
            message = "Нет доступа к Календарю. Разрешить добавление событий можно в Настройках."
        case .failed(let text):
            message = "Не получилось добавить событие: \(text)"
        }
    }
}

struct CalendarView: View {
    @State private var model: CalendarModel
    @Environment(\.openURL) private var openURL

    init(model: CalendarModel = .live()) {
        _model = State(initialValue: model)
    }

    var body: some View {
        TokenList {
            Section {
                SheetIntro(
                    systemImage: "calendar.badge.plus", title: "Сезоны — в Календарь",
                    text: "Сезон длится две недели. Добавь его конец — телефон напомнит за сутки до мягкого сброса.")
            }
            Section {
                if model.seasons.isEmpty {
                    Text(model.loaded ? "Сезонов пока не видно: нет связи с сервером." : "Загружаю сезоны…")
                        .foregroundStyle(Palette.uiInk2.color)
                }
                ForEach(model.seasons, id: \.number) { season in
                    seasonRow(season)
                }
            } header: {
                Text("Сезоны")
            } footer: {
                Text("Доступ — только на запись: игра добавляет события, но не видит твой календарь.")
            }
            if let message = model.message {
                Section {
                    Text(message)
                        .font(.footnote)
                        .foregroundStyle(Palette.uiInk2.color)
                    if message.contains("Настройках"), let url = SystemSettings.appURL {
                        Button("Открыть Настройки") { openURL(url) }
                    }
                }
            }
        }
        .tint(Palette.uiInk.color)
        .navigationTitle("Календарь")
        .task {
            if !model.loaded { await model.load() }
        }
    }

    private func seasonRow(_ season: SeasonInfo) -> some View {
        let phase = model.phase(of: season)
        return HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 8) {
                    Text(season.name)
                        .font(.headline)
                        .foregroundStyle(Palette.uiInk.color)
                    Text(phase.title)
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(phase == .going ? Palette.uiButtonInk.color : Palette.uiInk2.color)
                        .padding(.horizontal, 8)
                        .padding(.vertical, 2)
                        .background(phase == .going ? Palette.uiButton.color : Palette.uiFill.color, in: .capsule)
                }
                Text(dates(season))
                    .font(.subheadline)
                    .foregroundStyle(Palette.uiInk2.color)
            }
            Spacer(minLength: 8)
            if model.added.contains(season.number) {
                Image(systemName: "checkmark.circle.fill")
                    .font(.title2)
                    .foregroundStyle(Palette.uiInk.color)
                    .accessibilityLabel("Добавлено в Календарь")
            } else if season.endsAt != nil && phase != .over {
                Button {
                    Task { await model.add(season) }
                } label: {
                    if model.working == season.number {
                        ProgressView()
                    } else {
                        Label("В Календарь", systemImage: "calendar.badge.plus")
                            .labelStyle(.iconOnly)
                            .font(.title2)
                    }
                }
                .buttonStyle(.borderless)
                .accessibilityLabel("Добавить конец сезона в Календарь")
            }
        }
        .padding(.vertical, 4)
    }

    /// «16 ноября — 29 ноября»: последний день — тот, что перед концом в полночь.
    private func dates(_ season: SeasonInfo) -> String {
        let start = SeasonTime.text(Double(season.startsAtMs) / 1_000)
        guard let end = season.endsAt else { return "с \(start), конец ещё не назначен" }
        return "\(start) — \(SeasonTime.text(end - 1))"
    }
}

extension CalendarModel.Phase {
    var title: String {
        switch self {
        case .upcoming: "впереди"
        case .going: "идёт"
        case .over: "закончился"
        }
    }
}
