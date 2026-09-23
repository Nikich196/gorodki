import Networking
import Observation
import SwiftUI

/// «Лаборатория», сервер: адрес из настроек сборки, вход и проверка связи (`GET /health`) через настоящий клиент
/// приложения — тот же транспорт и подпись запросов, что у синхронизации. Пока сервер не развёрнут, экран честно
/// показывает «адрес не задан».
struct ServerLabView: View {
    @State private var model: ServerLabModel

    init(dependencies: AppDependencies) {
        _model = State(initialValue: ServerLabModel(dependencies: dependencies))
    }

    var body: some View {
        List {
            Section {
                LabeledContent("Адрес", value: model.address)
                LabeledContent("Вход", value: model.signIn)
            } footer: {
                Text("Адрес задаётся при сборке: переменная GORODKI_SERVER_URL (docs/architecture/ios-app.md).")
            }
            Section {
                Button("Проверить связь", systemImage: "antenna.radiowaves.left.and.right") {
                    Task { await model.check() }
                }
                .disabled(!model.canCheck || model.isChecking)
                if let result = model.result {
                    Text(result)
                        .font(.footnote)
                        .textSelection(.enabled)
                }
            } footer: {
                Text("GET /health: версия сервера и игровой день по Минску.")
            }
        }
        .navigationTitle("Сервер")
        .task { await model.refresh() }
    }
}

@MainActor
@Observable
final class ServerLabModel {
    private(set) var signIn = "…"
    private(set) var result: String?
    private(set) var isChecking = false
    private let dependencies: AppDependencies

    init(dependencies: AppDependencies) {
        self.dependencies = dependencies
    }

    var address: String { dependencies.serverURL?.absoluteString ?? "не задан" }
    var canCheck: Bool { dependencies.api != nil }

    func refresh() async {
        // Только «да/нет»: id игрока на снимке экрана ни к чему.
        signIn = await dependencies.tokens.current() == nil ? "не выполнен" : "выполнен"
    }

    func check() async {
        guard let api = dependencies.api else { return }
        isChecking = true
        defer { isChecking = false }
        switch await ServerCheck.run(api) {
        case .online(let version, let gameDay):
            result = "На связи: версия \(version), игровой день \(gameDay)"
        case .unexpectedStatus(let status):
            result = "Сервер ответил кодом \(status)"
        case .unreachable(let reason):
            result = "Нет связи: \(reason)"
        }
    }
}
