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
            InstallCheckSections()
        }
        .navigationTitle("Отладка")
    }
}
