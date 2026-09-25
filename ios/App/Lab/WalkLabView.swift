import GameCore
import SwiftUI

/// Экран прогулки для спайка S1: фоновый трекинг, Live Activity, живые правила GameCore.
struct WalkLabView: View {
    @State private var lab = WalkLab.shared

    var body: some View {
        Form {
            Section {
                Picker("Способ", selection: $lab.api) {
                    ForEach(LocationAPI.allCases) { api in
                        Text(api.title).tag(api)
                    }
                }
                .disabled(lab.isRunning)

                if lab.isRunning {
                    Button("Закончить прогулку", systemImage: "stop.fill", role: .destructive) {
                        lab.stop()
                    }
                } else {
                    Button("Начать прогулку", systemImage: "figure.walk") {
                        lab.start()
                    }
                    .buttonStyle(.glassProminent)
                }
            } footer: {
                Text(
                    "Начни, заблокируй телефон и положи в карман. Пройди 30–40 минут, где-то постой 3–5 минут. "
                        + "Потом открой этот экран и сделай снимок сводки. Второй раз — другим способом."
                )
            }

            if let stats = lab.stats {
                Section("Сейчас") {
                    if lab.isRunning {
                        LabeledContent("Идёт") {
                            Text(stats.startedAt, style: .timer).monospacedDigit()
                        }
                    }
                    LabeledContent("Точек", value: "\(stats.fixes) (отброшено \(stats.ignored))")
                    LabeledContent("Самый длинный разрыв", value: "\(Int(stats.longestGapSeconds)) с")
                    LabeledContent("Разрывов длиннее 15 с", value: "\(stats.gapsOver15Seconds)")
                    LabeledContent(
                        "Дистанция", value: NumberText.kilometers(fromMeters: stats.distanceMeters, fractionDigits: 2))
                    LabeledContent("Петель", value: "\(stats.loops)")
                    LabeledContent(
                        "Туман открыт",
                        value: NumberText.hectares(fromSquareMeters: stats.fogAreaSquareMeters, fractionDigits: 2))
                    if let event = lab.lastEvent {
                        Text(event).font(.footnote).foregroundStyle(.secondary)
                    }
                }

                Section {
                    Text(lab.summary)
                        .font(.system(.footnote, design: .monospaced))
                        .textSelection(.enabled)
                    ShareLink(item: lab.summary) {
                        Label("Отправить сводку", systemImage: "square.and.arrow.up")
                    }
                } header: {
                    Text("Сводка")
                } footer: {
                    Text("В сводке только числа, без координат и маршрута.")
                }
            }
        }
        .navigationTitle("Прогулка (S1)")
        .navigationBarTitleDisplayMode(.inline)
    }
}
