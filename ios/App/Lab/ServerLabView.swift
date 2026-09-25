import Networking
import Observation
import Realtime
import SwiftUI

/// «Лаборатория», сервер: адрес из настроек сборки, вход и проверка связи (`GET /health`) через настоящий клиент
/// приложения — тот же транспорт и подпись запросов, что у синхронизации. Пока сервер не развёрнут, экран честно
/// показывает «адрес не задан».
///
/// Реальное время (спайк S7, PLAN.md §10: «SignalR переподключается») — то самое соединение приложения
/// (`AppDependencies.realtime`), а не отдельное: состояние, сколько раз оно восстановилось само и когда пришла последняя
/// подсказка сервера. Обновляется раз в секунду, пока экран открыт.
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
            Section {
                LabeledContent("Соединение", value: model.realtime.connection)
                LabeledContent("Переподключений", value: model.realtime.reconnects)
                LabeledContent("Последняя подсказка", value: model.realtime.lastHint)
            } header: {
                Text("Реальное время (S7)")
            } footer: {
                Text(
                    "Переподключение — соединение восстановилось само после разрыва. Проверка: авиарежим на 10–20 с "
                        + "и обратно — «ждёт повтора», потом «на связи», переподключений +1.")
            }
        }
        .navigationTitle("Сервер")
        .task {
            while !Task.isCancelled {
                await model.refresh()
                try? await Task.sleep(for: .seconds(1))
            }
        }
    }
}

@MainActor
@Observable
final class ServerLabModel {
    private(set) var signIn = "…"
    private(set) var realtime = RealtimeLabText(connection: "…", reconnects: "—", lastHint: "—")
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
        let signedIn = await dependencies.tokens.current() != nil
        signIn = signedIn ? "выполнен" : "не выполнен"
        let diagnostics = await dependencies.realtime?.diagnostics
        realtime = RealtimeLabText(diagnostics: diagnostics, signedIn: signedIn)
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
