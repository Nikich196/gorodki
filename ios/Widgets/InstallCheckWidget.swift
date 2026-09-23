import Platform
import SwiftUI
import WidgetKit

/// Проверочный виджет спайка S2: показывает время последнего запуска приложения,
/// прочитанное из App Group. Видно время — значит, работают и расширение, и App Group.
struct InstallCheckWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: WidgetKind.installCheck, provider: InstallCheckProvider()) { entry in
            InstallCheckWidgetView(entry: entry)
                .containerBackground(.fill.tertiary, for: .widget)
        }
        .configurationDisplayName("Проверка установки")
        .description("Показывает, видит ли виджет данные приложения.")
        .supportedFamilies([.systemSmall])
    }
}

struct InstallCheckEntry: TimelineEntry {
    let date: Date
    let appGroupAvailable: Bool
    let lastAppLaunch: Date?
}

struct InstallCheckProvider: TimelineProvider {
    func placeholder(in context: Context) -> InstallCheckEntry {
        InstallCheckEntry(date: .now, appGroupAvailable: true, lastAppLaunch: .now)
    }

    func getSnapshot(in context: Context, completion: @escaping (InstallCheckEntry) -> Void) {
        completion(currentEntry())
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<InstallCheckEntry>) -> Void) {
        // Приложение само просит обновить виджет после каждого запуска; раз в час — на всякий случай.
        let nextUpdate = Date.now.addingTimeInterval(60 * 60)
        completion(Timeline(entries: [currentEntry()], policy: .after(nextUpdate)))
    }

    private func currentEntry() -> InstallCheckEntry {
        InstallCheckEntry(
            date: .now,
            appGroupAvailable: AppGroup.containerURL != nil,
            lastAppLaunch: InstallCheckRecord.load()?.lastAppLaunch
        )
    }
}

struct InstallCheckWidgetView: View {
    let entry: InstallCheckEntry

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Label("Городки", systemImage: "map.fill")
                .font(.caption.weight(.semibold))
                .foregroundStyle(.secondary)
            Spacer(minLength: 0)
            if let lastLaunch = entry.lastAppLaunch {
                Text("Приложение запускалось")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Text(lastLaunch, style: .time)
                    .font(.title2.weight(.bold))
                    .monospacedDigit()
                Label("App Group работает", systemImage: "checkmark.circle.fill")
                    .font(.caption2)
                    .foregroundStyle(.green)
            } else {
                Text(entry.appGroupAvailable ? "Открой приложение" : "Нет доступа к App Group")
                    .font(.headline)
                Label(
                    entry.appGroupAvailable ? "Ждём первый запуск" : "Проверь подпись",
                    systemImage: entry.appGroupAvailable ? "clock" : "xmark.octagon.fill"
                )
                .font(.caption2)
                .foregroundStyle(entry.appGroupAvailable ? Color.secondary : Color.red)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
    }
}
