import SwiftUI

/// Отладочное меню (PLAN.md, §5: «только роли demo и admin»; кому ещё — `DebugAccess`): «Пункты задания» (листик для
/// защиты), «Проверка установки» (спайк S2) и «Лаборатория» — то, что раньше было стартовым экраном. Открывается из
/// «Профиль → Отладка» и кнопкой «Отладка» на онбординге.
struct DebugMenuView: View {
    /// Игрок — для экранов из «Пунктов задания» (цвет видео-повтора, свой QR); `nil` — меню с онбординга.
    var profile: ProfileModel?

    var body: some View {
        List {
            Section {
                NavigationLink {
                    AssignmentSheetView(
                        player: profile?.playerColor ?? .blue, playerId: profile?.playerId,
                        playerName: profile?.displayName)
                } label: {
                    Label("Пункты задания", systemImage: "checklist")
                }
            } footer: {
                Text("14 пунктов листика: где в приложении показать каждый — для защиты.")
            }
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
