import Realtime
import Testing

@testable import Gorodki

/// Раздел «Реальное время» в «Лаборатории → Сервер» (спайк S7): на прогулке по нему решают, переподключается ли
/// соединение, поэтому «нет входа» и «нет адреса» не должны выглядеть как поломка.
@Suite("Лаборатория, сервер: реальное время (S7)")
struct ServerLabTests {
    @Test("Адрес сервера не задан — так и сказано, соединения нет")
    func noServer() {
        #expect(
            RealtimeLabText(diagnostics: nil, signedIn: false)
                == RealtimeLabText(connection: "адрес не задан", reconnects: "—", lastHint: "—"))
    }

    @Test("Входа нет — «нужен вход», а не «остановлено»; и когда цикл сам остановился из-за входа")
    func needsSignIn() {
        let stopped = RealtimeClient.Diagnostics(state: .stopped, needsSignIn: false, reconnects: 0)
        #expect(RealtimeLabText(diagnostics: stopped, signedIn: false).connection == "нужен вход")

        let lostSignIn = RealtimeClient.Diagnostics(state: .stopped, needsSignIn: true, reconnects: 1)
        #expect(RealtimeLabText(diagnostics: lostSignIn, signedIn: true).connection == "нужен вход")
    }

    @Test("На связи: переподключения и секунды с последней подсказки")
    func connected() {
        let diagnostics = RealtimeClient.Diagnostics(
            state: .connected, needsSignIn: false, reconnects: 2, secondsSinceLastHint: 12.4)
        #expect(
            RealtimeLabText(diagnostics: diagnostics, signedIn: true)
                == RealtimeLabText(connection: "на связи", reconnects: "2", lastHint: "12\u{00A0}с назад"))
    }

    @Test("Ждёт повтора — с паузой; подсказок ещё не было")
    func waiting() {
        let diagnostics = RealtimeClient.Diagnostics(state: .waiting(.seconds(4)), needsSignIn: false, reconnects: 0)
        let text = RealtimeLabText(diagnostics: diagnostics, signedIn: true)
        #expect(text.connection == "ждёт повтора (4 с)")
        #expect(text.lastHint == "ещё не было")
    }
}
