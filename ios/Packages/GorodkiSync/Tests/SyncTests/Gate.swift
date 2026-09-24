import Synchronization

/// Ворота для проверок гонок: `wait()` ждёт, пока кто-нибудь не откроет их (`open()`, можно и из синхронного кода).
/// Порядок событий задаётся явно, а не сном: на загруженной машине сон «не успевает», и проверка падает зря или проходит,
/// не проверив того, что должна. Не открылись — проверка висит, и это видно (CI обрывает шаг по тайм-ауту).
final class Gate: Sendable {
    private let state = Mutex<(isOpen: Bool, waiters: [CheckedContinuation<Void, Never>])>((false, []))

    func open() {
        let waiters = state.withLock { state in
            state.isOpen = true
            defer { state.waiters = [] }
            return state.waiters
        }
        waiters.forEach { $0.resume() }
    }

    func wait() async {
        await withCheckedContinuation { continuation in
            let isOpen = state.withLock { state in
                if !state.isOpen { state.waiters.append(continuation) }
                return state.isOpen
            }
            if isOpen { continuation.resume() }
        }
    }
}
