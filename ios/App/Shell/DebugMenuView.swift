import SwiftUI

/// Отладочное меню (PLAN.md, §5: «только роли demo и admin»; кому ещё — `DebugAccess`): «Проверка установки» (спайк S2)
/// и «Лаборатория» — то, что раньше было стартовым экраном. Открывается из «Профиль → Отладка» и кнопкой «Отладка»
/// на онбординге.
struct DebugMenuView: View {
    var body: some View {
        List {
            Section {
                NavigationLink {
                    LabView()
                } label: {
                    Label("Лаборатория: пробная сборка", systemImage: "flask")
                }
            } footer: {
                Text("Проверки этапа 1 на настоящем телефоне: фоновый трекинг, Live Activity, туман.")
            }
            MyDataExportSection()
            InstallCheckSections()
        }
        .navigationTitle("Отладка")
    }
}

/// «Мои данные» файлом (`GET /me/export`) — для разбора петель полевого теста (docs/guides/calibration-replay.md):
/// точность точек и датчики есть только в этой выгрузке. Нужен вход.
private struct MyDataExportSection: View {
    @State private var file: URL?
    @State private var message: String?
    @State private var working = false

    var body: some View {
        Section {
            Button {
                Task { await export() }
            } label: {
                if working {
                    ProgressView()
                } else {
                    Label("Выгрузить «Мои данные»", systemImage: "square.and.arrow.down")
                }
            }
            .disabled(working)
            if let file {
                ShareLink(item: file) {
                    Label("Отправить файл", systemImage: "square.and.arrow.up")
                }
            }
            if let message {
                Text(message)
                    .foregroundStyle(.secondary)
            }
        } header: {
            Text("Мои данные")
        } footer: {
            Text("JSON со всеми забегами и точками — для разбора петель полевого теста. В нём настоящий маршрут.")
        }
    }

    private func export() async {
        working = true
        message = nil
        defer { working = false }
        do {
            if let url = try await AppDependencies.shared.exportMyData() {
                file = url
            } else {
                message = "В этой сборке не задан адрес сервера."
            }
        } catch {
            message = "Не удалось выгрузить: нужны вход и связь с сервером."
        }
    }
}
