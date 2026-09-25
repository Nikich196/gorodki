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
                NavigationLink {
                    ProbeRunView()
                } label: {
                    Label("Пробный забег (без сервера)", systemImage: "figure.run")
                }
                NavigationLink {
                    MapStressView()
                } label: {
                    Label("Карта: 5 000 участков, туман, швы (S4)", systemImage: "map")
                }
                NavigationLink {
                    ServerLabView(dependencies: .shared)
                } label: {
                    Label("Сервер: связь, вход, реальное время (S7)", systemImage: "network")
                }
            } footer: {
                Text("Каждый экран отвечает на один вопрос из плана. Результат — снимок экрана со сводкой.")
            }
        }
        .navigationTitle("Лаборатория")
    }
}
