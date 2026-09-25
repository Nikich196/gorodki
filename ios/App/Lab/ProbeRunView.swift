import GameCore
import SwiftUI
import Sync

/// Экран пробного забега: настоящий путь забега без входа и без сервера (`ProbeRun`).
struct ProbeRunView: View {
    @State private var probe = ProbeRun.shared
    @State private var confirmingRemoval = false

    var body: some View {
        Form {
            Section {
                if probe.isRunning {
                    Button("Закончить пробный забег", systemImage: "stop.fill", role: .destructive) {
                        Task { await probe.finish() }
                    }
                } else {
                    Button("Начать пробный забег", systemImage: "figure.run") {
                        Task { await probe.start() }
                    }
                    .buttonStyle(.glassProminent)
                }
            } footer: {
                Text(
                    "Тот же путь, что у настоящего забега: геопозиция и датчики → трекер → судья, петли, туман → куски "
                        + "в очереди на телефоне. Вход и сервер не нужны: на сервер пробный забег не уходит. Переживает "
                        + "закрытие приложения. Пока идёт настоящий забег, пробный не начать — и наоборот."
                )
            }
            .disabled(probe.busy)

            if let problem = probe.problem {
                Section {
                    Text(problem).foregroundStyle(.red)
                }
            }

            if let record = probe.record {
                Section("Сейчас") {
                    if probe.isRunning {
                        LabeledContent("Идёт") {
                            Text(record.startedAt, style: .timer).monospacedDigit()
                        }
                    }
                    let counters = probe.counters
                    LabeledContent("Точек принято", value: NumberText.integer(counters.accepted))
                    LabeledContent("Отброшено судьёй", value: NumberText.integer(counters.rejected))
                    LabeledContent("Петель", value: NumberText.integer(counters.loops))
                    LabeledContent("Туман на телефоне", value: CountText.fogCells(counters.fogCells))
                    if let queued = probe.queued {
                        LabeledContent("Разрывов GPS длиннее 15 с", value: NumberText.integer(queued.gapsOverLimit))
                        LabeledContent(
                            "Отметка датчиков отстаёт",
                            value: queued.sensorLagSeconds.map { NumberText.seconds($0, fractionDigits: 1) } ?? "—")
                        LabeledContent(
                            "В очереди",
                            value: CountText.pieces(queued.chunks) + " · " + CountText.points(queued.points))
                    }
                }

                Section {
                    Text(probe.summary)
                        .font(.system(.footnote, design: .monospaced))
                        .textSelection(.enabled)
                    ShareLink(item: probe.summary) {
                        Label("Отправить сводку", systemImage: "square.and.arrow.up")
                    }
                } header: {
                    Text("Сводка")
                } footer: {
                    Text(
                        "В сводке только числа, без координат и маршрута. Отметка датчиков обычно отстаёт на 10–15 с; "
                            + "больше — таймер трекера вставал. Закрытие приложения даёт один разрыв GPS: время "
                            + "до нового запуска и до минуты точек перед закрытием, ещё не записанных в очередь."
                    )
                }
            }

            Section {
                Button("Удалить пробные забеги", systemImage: "trash", role: .destructive) {
                    confirmingRemoval = true
                }
                // Сводка остаётся и после стирания всей очереди (выход из аккаунта) — её тоже убирает эта кнопка.
                .disabled(probe.isRunning || probe.busy || (probe.storedRuns == 0 && probe.record == nil))
            } footer: {
                Text("Пробных забегов в очереди: \(probe.storedRuns). Забеги игрока не трогаются.")
            }
        }
        .navigationTitle("Пробный забег")
        .navigationBarTitleDisplayMode(.inline)
        .task { await probe.watchQueue() }
        .confirmationDialog("Удалить пробные забеги?", isPresented: $confirmingRemoval, titleVisibility: .visible) {
            Button("Удалить", role: .destructive) {
                Task { await probe.removeProbeRuns() }
            }
        } message: {
            Text("Их точки сотрутся из очереди на телефоне.")
        }
    }
}
