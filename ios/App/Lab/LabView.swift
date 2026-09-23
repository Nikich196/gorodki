import SwiftUI

/// «Лаборатория»: проверки спайков этапа 1 на настоящем телефоне (PLAN.md, §10).
/// Одна установка — одна прогулка: так дешевле для людей, чем отдельная сборка на каждый вопрос.
struct LabView: View {
    var body: some View {
        List {
            Section {
                NavigationLink {
                    WalkLabView()
                } label: {
                    Label("Прогулка: фоновый трекинг (S1)", systemImage: "figure.walk")
                }
            } footer: {
                Text("Каждый экран отвечает на один вопрос из плана. Результат — снимок экрана со сводкой.")
            }
        }
        .navigationTitle("Лаборатория")
    }
}
