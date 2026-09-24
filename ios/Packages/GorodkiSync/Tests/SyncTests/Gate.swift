import Synchronization
import Testing

/// Ворота для проверок гонок: `wait()` ждёт, пока кто-нибудь не откроет их (`open()`, можно и из синхронного кода).
/// Порядок событий задаётся явно, а не сном: на загруженной машине сон «не успевает», и проверка падает зря или проходит,
/// не проверив того, что должна. Не открылись за `patience` — это ошибка в коде, и проверка падает с понятной причиной,
/// а не висит до тайм-аута CI.
final class Gate: Sendable {
    /// Сколько ждать, прежде чем признать, что ворота не откроются. На исход проверки не влияет: открытые вовремя ворота
    /// его не ждут.
    static let patience: Duration = .seconds(10)

    private struct State {
        var isOpen = false
        var waiters: [Int: CheckedContinuation<Void, Never>] = [:]
        var nextId = 0
    }

    private let state = Mutex(State())

    func open() {
        let waiters = state.withLock { state in
            state.isOpen = true
            defer { state.waiters = [:] }
            return Array(state.waiters.values)
        }
        waiters.forEach { $0.resume() }
    }

    func wait(sourceLocation: SourceLocation = #_sourceLocation) async {
        await withCheckedContinuation { continuation in
            let id: Int? = state.withLock { state in
                guard !state.isOpen else { return nil }
                let id = state.nextId
                state.nextId += 1
                state.waiters[id] = continuation
                return id
            }
            guard let id else { return continuation.resume() }
            Task {
                try? await Task.sleep(for: Self.patience)
                guard let late = self.state.withLock({ $0.waiters.removeValue(forKey: id) }) else { return }
                Issue.record("ворота не открылись за \(Self.patience)", sourceLocation: sourceLocation)
                late.resume()
            }
        }
    }
}
