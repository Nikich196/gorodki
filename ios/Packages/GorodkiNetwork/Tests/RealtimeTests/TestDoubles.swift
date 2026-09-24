import Foundation
import Networking
import Synchronization
import Testing

@testable import Realtime

/// Соединение-подмена: подсказки шлёт тест (`push`), закрыть его могут и тест («сервер закрыл»), и клиент.
/// Рукопожатие и подписку можно придержать — как у медленного сервера.
final class FakeConnection: RealtimeConnection {
    struct SubscribeRefused: Error {}

    let messages: AsyncStream<RealtimeEvent>
    private let continuation: AsyncStream<RealtimeEvent>.Continuation
    private let state: Mutex<State>

    private struct State {
        var subscriptions: [League] = []
        var closed = false
        var refuseSubscribe: Bool
        var holdHandshake: Bool
        var holdSubscribe: Bool
    }

    init(refuseSubscribe: Bool = false, holdHandshake: Bool = false, holdSubscribe: Bool = false) {
        (messages, continuation) = AsyncStream.makeStream()
        state = Mutex(
            State(refuseSubscribe: refuseSubscribe, holdHandshake: holdHandshake, holdSubscribe: holdSubscribe))
    }

    var subscriptions: [League] { state.withLock { $0.subscriptions } }
    var closed: Bool { state.withLock { $0.closed } }

    func releaseHandshake() { state.withLock { $0.holdHandshake = false } }
    func releaseSubscribe() { state.withLock { $0.holdSubscribe = false } }
    func refuseNextSubscribe() { state.withLock { $0.refuseSubscribe = true } }

    func push(_ event: RealtimeEvent) { continuation.yield(event) }

    /// Сервер закрыл соединение.
    func serverClose() { continuation.finish() }

    /// Рукопожатие: ждёт, пока его не отпустят. Отмену не замечает — как настоящий медленный сервер.
    func handshake() async {
        await pollUntil { !self.state.withLock { $0.holdHandshake } }
    }

    func subscribe(to league: League) async throws {
        state.withLock { $0.subscriptions.append(league) }
        await pollUntil { !self.state.withLock { $0.holdSubscribe } }
        if state.withLock({ $0.refuseSubscribe }) {
            throw SubscribeRefused()
        }
    }

    func close() async {
        state.withLock { $0.closed = true }
        continuation.finish()
    }
}

/// Подключение-подмена: отвечает заранее заданными исходами по очереди; пока исходов нет — ждёт (отмена прерывает).
actor FakeConnector: RealtimeConnector {
    enum Outcome: Sendable {
        case open(FakeConnection)
        case fail
        case notSignedIn
    }

    struct Unreachable: Error {}

    private var planned: [Outcome] = []
    private(set) var attempts = 0

    func plan(_ outcomes: Outcome...) { planned += outcomes }

    func connect() async throws -> any RealtimeConnection {
        attempts += 1
        while planned.isEmpty {
            try await Task.sleep(for: .milliseconds(1))
        }
        switch planned.removeFirst() {
        case .open(let connection):
            await connection.handshake()
            return connection
        case .fail:
            throw Unreachable()
        case .notSignedIn:
            throw RealtimeConnectError.notSignedIn
        }
    }
}

/// Паузы-подмена: запоминает, сколько просили ждать. Без `blocking` не ждёт вовсе; с `blocking` — «вечно», пока паузу
/// не прервут (прерванные считает).
final class FakeSleep: Sendable {
    private let state = Mutex<(requested: [Duration], interrupted: Int)>(([], 0))
    private let blocking: Bool

    init(blocking: Bool = false) {
        self.blocking = blocking
    }

    var requested: [Duration] { state.withLock { $0.requested } }
    var interrupted: Int { state.withLock { $0.interrupted } }

    func sleep(_ duration: Duration) async throws {
        state.withLock { $0.requested.append(duration) }
        guard blocking else { return }
        do {
            try await Task.sleep(for: .seconds(3_600))
        } catch {
            state.withLock { $0.interrupted += 1 }
            throw error
        }
    }
}

/// Монотонные часы, которые двигает тест.
final class FakeClock: Sendable {
    private let seconds = Mutex(1_000.0)

    var now: Double { seconds.withLock { $0 } }

    func advance(_ by: Double) { seconds.withLock { $0 += by } }
}

/// Все события клиента по порядку: читает их одна задача с начала теста (поток — для одного читателя, а отмена чтения
/// закрыла бы его).
final class EventLog: Sendable {
    private let received = Mutex<[RealtimeEvent]>([])

    init(_ events: AsyncStream<RealtimeEvent>) {
        Task {
            for await event in events {
                self.received.withLock { $0.append(event) }
            }
        }
    }

    var events: [RealtimeEvent] { received.withLock { $0 } }
}

/// Ждёт условия (не дольше 5 секунд).
func waitUntil(_ condition: @Sendable () async -> Bool) async throws {
    for _ in 0..<2_500 {
        if await condition() { return }
        try await Task.sleep(for: .milliseconds(2))
    }
    Issue.record("Условие не выполнилось за 5 секунд")
}

/// Ждёт условия, не замечая отмены (подмены медленного сервера): ожидание — в своей задаче, отмена вызывающей до неё
/// не доходит.
func pollUntil(_ condition: @escaping @Sendable () -> Bool) async {
    await Task {
        while !condition() {
            try? await Task.sleep(for: .milliseconds(1))
        }
    }.value
}
