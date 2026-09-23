import SwiftUI

/// Секции экрана «Проверка установки». Встраиваются в список стартового экрана.
struct InstallCheckSections: View {
    @State private var model = InstallCheckModel()

    var body: some View {
        Section {
            ForEach(model.items) { item in
                CheckRow(item: item)
            }
        } header: {
            Text("Проверка установки")
        } footer: {
            Text("Спайк S2: сделай снимок этого экрана после установки через Sideloadly и пришли его в чат.")
        }
        .task { model.refresh() }

        Section {
            if model.activityID == nil {
                Button("Запустить Live Activity", systemImage: "play.fill") {
                    model.startActivity()
                }
                .buttonStyle(.glassProminent)
            } else {
                Button("Обновить текст", systemImage: "arrow.clockwise") {
                    Task { await model.updateActivity() }
                }
                Button("Завершить", systemImage: "stop.fill", role: .destructive) {
                    Task { await model.endActivity() }
                }
            }
            if let error = model.activityError {
                Text(error)
                    .font(.footnote)
                    .foregroundStyle(.red)
            }
            Button("Проверить заново", systemImage: "checklist") {
                model.refresh()
            }
        } header: {
            Text("Live Activity и виджет")
        } footer: {
            Text(
                "Запусти Live Activity и заблокируй телефон: должна появиться плашка «Городки» с таймером. "
                    + "Потом добавь виджет «Городки» на экран «Домой»: он покажет время последнего запуска."
            )
        }
    }
}

private struct CheckRow: View {
    let item: CheckItem

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            Image(systemName: item.status.symbolName)
                .foregroundStyle(item.status.tint)
            VStack(alignment: .leading, spacing: 2) {
                Text(item.title)
                    .font(.subheadline.weight(.semibold))
                Text(item.value)
                    .font(.footnote)
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
            }
        }
        .accessibilityElement(children: .combine)
    }
}

extension CheckItem.Status {
    fileprivate var symbolName: String {
        switch self {
        case .ok: "checkmark.circle.fill"
        case .warning: "exclamationmark.triangle.fill"
        case .failed: "xmark.octagon.fill"
        case .info: "info.circle.fill"
        }
    }

    fileprivate var tint: Color {
        switch self {
        case .ok: .green
        case .warning: .orange
        case .failed: .red
        case .info: .secondary
        }
    }
}
