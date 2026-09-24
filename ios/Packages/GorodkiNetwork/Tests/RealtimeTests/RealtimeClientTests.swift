import Foundation
import Networking
import SignalRClient
import Testing

@testable import Realtime

@Suite("Реальное время: соединение")
struct RealtimeClientTests {
    private let connector = FakeConnector()
    private let clock = FakeClock()

    private func client(sleep: FakeSleep) -> RealtimeClient {
        let clock = self.clock
        return RealtimeClient(connector: connector, sleep: sleep.sleep, now: { clock.now })
    }

    @Test("Подключение: подписка на лигу, событие connected, дальше подсказки сервера по порядку")
    func connects() async throws {
        let connection = FakeConnection()
        await connector.plan(.open(connection))
        let client = client(sleep: FakeSleep())
        let log = EventLog(client.events)

        await client.start()
        try await waitUntil { log.events == [.connected] }
        #expect(connection.subscriptions == [.run])
        #expect(await client.state == .connected)

        connection.push(.tilesChanged(.run, [TileKey(x: 684, y: 5775), TileKey(x: 685, y: 5775)]))
        connection.push(.captureDecided(runId: "r-1", captureId: "c-1", status: "applied"))
        try await waitUntil { log.events.count == 3 }
        #expect(
            log.events == [
                .connected,
                .tilesChanged(.run, [TileKey(x: 684, y: 5775), TileKey(x: 685, y: 5775)]),
                .captureDecided(runId: "r-1", captureId: "c-1", status: "applied"),
            ])

        await client.stop()
        #expect(connection.closed)
        #expect(await client.state == .stopped)
    }

    @Test("Сервер недоступен — паузы 1, 2, 4, 8, 16, 30, 30 с и без конца; подключились — события как обычно")
    func backoff() async throws {
        let connection = FakeConnection()
        await connector.plan(.fail, .fail, .fail, .fail, .fail, .fail, .fail, .open(connection))
        let sleep = FakeSleep()
        let client = client(sleep: sleep)
        let log = EventLog(client.events)

        await client.start()
        try await waitUntil { log.events == [.connected] }
        #expect(sleep.requested == [1, 2, 4, 8, 16, 30, 30].map { Duration.seconds($0) })
        #expect(await connector.attempts == 8)
        await client.stop()
    }

    @Test("Долгое соединение закрыл сервер (истёк токен) — переподключение сразу, подписка заново")
    func reconnectsAfterStableConnection() async throws {
        let first = FakeConnection()
        let second = FakeConnection()
        await connector.plan(.fail, .open(first), .open(second))
        let sleep = FakeSleep()
        let client = client(sleep: sleep)
        let log = EventLog(client.events)

        await client.start()
        try await waitUntil { log.events == [.connected] }
        clock.advance(RealtimeClient.stableAfter)
        first.serverClose()

        try await waitUntil { log.events == [.connected, .connected] }
        #expect(second.subscriptions == [.run])
        // Пауза — только одна, после первой неудачи: долгое соединение сбросило шаг, переподключение без паузы.
        #expect(sleep.requested == [.seconds(1)])
        #expect(first.closed)
        await client.stop()
    }

    @Test("Соединение закрылось быстрее 30 с — это неудача: повтор с паузой, шаг растёт")
    func shortConnectionIsFailure() async throws {
        let first = FakeConnection()
        let second = FakeConnection()
        let third = FakeConnection()
        await connector.plan(.open(first), .open(second), .open(third))
        let sleep = FakeSleep()
        let client = client(sleep: sleep)
        let log = EventLog(client.events)

        await client.start()
        try await waitUntil { log.events == [.connected] }
        clock.advance(RealtimeClient.stableAfter - 1)
        first.serverClose()
        try await waitUntil { log.events.count == 2 }
        second.serverClose()
        try await waitUntil { log.events.count == 3 }

        #expect(sleep.requested == [.seconds(1), .seconds(2)])
        await client.stop()
    }

    @Test("Подписка не прошла — соединение закрывается, повтор с паузой")
    func subscribeRefused() async throws {
        let refusing = FakeConnection(refuseSubscribe: true)
        let good = FakeConnection()
        await connector.plan(.open(refusing), .open(good))
        let sleep = FakeSleep()
        let client = client(sleep: sleep)
        let log = EventLog(client.events)

        await client.start()
        try await waitUntil { log.events == [.connected] }
        #expect(refusing.closed)
        #expect(good.subscriptions == [.run])
        #expect(sleep.requested == [.seconds(1)])
        await client.stop()
    }

    @Test("Входа нет — цикл останавливается без повторов; start после входа подключается")
    func notSignedIn() async throws {
        await connector.plan(.notSignedIn)
        let sleep = FakeSleep()
        let client = client(sleep: sleep)
        let log = EventLog(client.events)

        await client.start()
        try await waitUntil { await connector.attempts == 1 }
        try await waitUntil { await client.state == .stopped }
        #expect(sleep.requested.isEmpty)
        #expect(log.events.isEmpty)

        let connection = FakeConnection()
        await connector.plan(.open(connection))
        await client.start()
        try await waitUntil { log.events == [.connected] }
        #expect(await connector.attempts == 2)
        await client.stop()
    }

    @Test("stop закрывает соединение; start подключается заново")
    func stopAndStart() async throws {
        let first = FakeConnection()
        let second = FakeConnection()
        await connector.plan(.open(first))
        let client = client(sleep: FakeSleep())
        let log = EventLog(client.events)

        await client.start()
        try await waitUntil { log.events == [.connected] }
        await client.stop()
        #expect(first.closed)
        #expect(await client.state == .stopped)

        await connector.plan(.open(second))
        await client.start()
        try await waitUntil { log.events == [.connected, .connected] }
        #expect(second.subscriptions == [.run])
        #expect(await connector.attempts == 2)
        await client.stop()
    }

    @Test("Остановили, пока сервер отвечал на подключение, — открывшееся соединение сразу закрывается")
    func stopDuringHandshake() async throws {
        let late = FakeConnection(holdHandshake: true)
        await connector.plan(.open(late))
        let client = client(sleep: FakeSleep())
        let log = EventLog(client.events)

        await client.start()
        try await waitUntil { await connector.attempts == 1 }
        await client.stop()
        late.releaseHandshake()

        try await waitUntil { late.closed }
        #expect(late.subscriptions.isEmpty)
        #expect(log.events.isEmpty)
        #expect(await client.state == .stopped)
    }

    @Test("Второй start во время соединения ничего не открывает")
    func startTwice() async throws {
        let connection = FakeConnection()
        await connector.plan(.open(connection))
        let client = client(sleep: FakeSleep())
        let log = EventLog(client.events)

        await client.start()
        try await waitUntil { log.events == [.connected] }
        await client.start()
        try await Task.sleep(for: .milliseconds(50))
        #expect(await connector.attempts == 1)
        #expect(!connection.closed)
        await client.stop()
    }

    @Test("wake (или start) во время паузы — подключение сразу, шаг повторов сначала", arguments: [false, true])
    func wakeDuringPause(viaStart: Bool) async throws {
        await connector.plan(.fail, .fail, .fail)
        let sleep = FakeSleep(blocking: true)
        let client = client(sleep: sleep)

        await client.start()
        try await waitUntil { await client.state == .waiting(.seconds(1)) }
        if viaStart {
            await client.start()
        } else {
            await client.wake()
        }

        // Без сброса вторая пауза была бы 2 с.
        try await waitUntil { await connector.attempts == 2 }
        try await waitUntil { await client.state == .waiting(.seconds(1)) }
        #expect(sleep.requested == [.seconds(1), .seconds(1)])
        await client.stop()
    }

    @Test("wake без паузы ничего не делает")
    func wakeWhileConnected() async throws {
        let connection = FakeConnection()
        await connector.plan(.open(connection))
        let client = client(sleep: FakeSleep())
        let log = EventLog(client.events)

        await client.wake()
        #expect(await client.state == .stopped)
        await client.start()
        try await waitUntil { log.events == [.connected] }
        await client.wake()
        #expect(await client.state == .connected)
        #expect(await connector.attempts == 1)
        await client.stop()
    }

    @Test("Смена лиги: подписка на текущем соединении и на следующих")
    func selectLeague() async throws {
        let first = FakeConnection()
        let second = FakeConnection()
        await connector.plan(.open(first), .open(second))
        let client = client(sleep: FakeSleep())
        let log = EventLog(client.events)

        await client.start()
        try await waitUntil { log.events == [.connected] }
        await client.select(.bike)
        #expect(first.subscriptions == [.run, .bike])
        await client.select(.bike)
        #expect(first.subscriptions == [.run, .bike])

        clock.advance(RealtimeClient.stableAfter)
        first.serverClose()
        try await waitUntil { log.events == [.connected, .connected] }
        #expect(second.subscriptions == [.bike])
        await client.stop()
    }

    @Test("Лигу сменили, пока шла подписка, — подписка повторяется на новую до события connected")
    func selectDuringSubscribe() async throws {
        let slow = FakeConnection(holdSubscribe: true)
        await connector.plan(.open(slow))
        let client = client(sleep: FakeSleep())
        let log = EventLog(client.events)

        await client.start()
        try await waitUntil { slow.subscriptions == [.run] }
        await client.select(.bike)
        #expect(log.events.isEmpty)
        slow.releaseSubscribe()

        try await waitUntil { log.events == [.connected] }
        #expect(slow.subscriptions == [.run, .bike])
        #expect(await client.league == .bike)
        await client.stop()
    }

    @Test("Лигу выбрали до подключения — подписка сразу на неё")
    func selectBeforeStart() async throws {
        let connection = FakeConnection()
        await connector.plan(.open(connection))
        let client = client(sleep: FakeSleep())
        let log = EventLog(client.events)

        await client.select(.bike)
        await client.start()
        try await waitUntil { log.events == [.connected] }
        #expect(connection.subscriptions == [.bike])
        await client.stop()
    }

    @Test("stop во время паузы — пауза прервана, подключений больше нет")
    func stopDuringPause() async throws {
        await connector.plan(.fail)
        let sleep = FakeSleep(blocking: true)
        let client = client(sleep: sleep)

        await client.start()
        try await waitUntil { await client.state == .waiting(.seconds(1)) }
        await client.stop()
        try await waitUntil { sleep.interrupted == 1 }

        await connector.plan(.open(FakeConnection()))
        try await Task.sleep(for: .milliseconds(50))
        #expect(await connector.attempts == 1)
        #expect(await client.state == .stopped)
    }

    @Test("stop во время подписки — события connected нет, соединение закрыто")
    func stopDuringSubscribe() async throws {
        let slow = FakeConnection(holdSubscribe: true)
        await connector.plan(.open(slow))
        let client = client(sleep: FakeSleep())
        let log = EventLog(client.events)

        await client.start()
        try await waitUntil { slow.subscriptions == [.run] }
        await client.stop()
        slow.releaseSubscribe()
        try await Task.sleep(for: .milliseconds(50))

        #expect(slow.closed)
        #expect(log.events.isEmpty)
        #expect(await client.state == .stopped)
    }

    @Test("Смена лиги не прошла — соединение закрывается, переподключение подписывается на новую лигу")
    func selectRefused() async throws {
        let first = FakeConnection()
        let second = FakeConnection()
        await connector.plan(.open(first), .open(second))
        let client = client(sleep: FakeSleep())
        let log = EventLog(client.events)

        await client.start()
        try await waitUntil { log.events == [.connected] }
        first.refuseNextSubscribe()
        await client.select(.bike)

        #expect(first.closed)
        try await waitUntil { log.events == [.connected, .connected] }
        #expect(second.subscriptions == [.bike])
        await client.stop()
    }

    @Test("wake во время попытки — если она не удалась, следующая сразу, без паузы")
    func wakeDuringConnecting() async throws {
        let connection = FakeConnection()
        let sleep = FakeSleep()
        let client = client(sleep: sleep)
        let log = EventLog(client.events)

        await client.start()
        try await waitUntil { await connector.attempts == 1 }
        await client.wake()
        await connector.plan(.fail, .open(connection))

        try await waitUntil { log.events == [.connected] }
        #expect(sleep.requested.isEmpty)
        await client.stop()
    }

    @Test(
        "Пауза после неудач подряд",
        arguments: [(0, 0), (1, 1), (2, 2), (3, 4), (4, 8), (5, 16), (6, 30), (7, 30), (1_000, 30)])
    func retryDelay(failures: Int, seconds: Int) {
        #expect(RealtimeClient.retryDelay(afterFailures: failures) == .seconds(seconds))
    }
}

@Suite("Реальное время: подключение SignalR")
struct SignalRConnectorTests {
    /// Обновление токенов, которое не должно понадобиться.
    private struct NoRefresh: TokenRefresher {
        func refresh(_ refreshToken: String) async throws -> RefreshResult {
            Issue.record("Обновление не ожидалось")
            return .rejected(code: nil)
        }
    }

    @Test("Входа нет — notSignedIn без обращения к сети")
    func notSignedIn() async throws {
        let connector = SignalRConnector(
            serverURL: try #require(URL(string: "https://gorodki.invalid")),
            tokens: TokenStore(storage: InMemoryTokenStorage()), refresher: NoRefresh())

        await #expect(throws: RealtimeConnectError.notSignedIn) {
            _ = try await connector.connect()
        }
    }

    @Test("401 на согласовании распознаётся, другие ошибки — нет")
    func unauthorized() {
        // Текст — как у настоящего клиента 1.0 против настоящего хаба (проба 24.09): ошибка обёрнута дважды.
        #expect(
            SignalRConnector.isUnauthorized(
                SignalRError.negotiationError(
                    "Failed to complete negotiation with the server: Negotiation error: "
                        + "Unexpected status code returned from negotiate '401'")))
        #expect(
            !SignalRConnector.isUnauthorized(
                SignalRError.negotiationError(
                    "Failed to complete negotiation with the server: Negotiation error: "
                        + "Unexpected status code returned from negotiate '404'")))
        #expect(!SignalRConnector.isUnauthorized(SignalRError.unexpectedResponseCode(401)))
        #expect(!SignalRConnector.isUnauthorized(CancellationError()))
    }
}
