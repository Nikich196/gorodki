import Foundation
import Synchronization
import Testing

@testable import Networking

@Suite("Токены входа: токен для соединения реального времени")
struct ValidAccessTokenTests {
    /// Когда сервер выдал токен (его часы).
    private static let issued = 1_790_000_000

    /// Access-токен на 15 минут, выданный в `issued + shift`.
    private static func tokens(shift: Int = 0, refresh: String = "R1", player: String = "p-1") -> AuthTokens {
        let iat = issued + shift
        return AuthTokens(
            accessToken: Jwt.make(#"{"sub":"\#(player)","iat":\#(iat),"exp":\#(iat + 900)}"#), refreshToken: refresh)
    }

    private static let renewed = tokens(shift: 900, refresh: "R2")

    /// Часы телефона, которые двигает тест.
    private final class Clock: Sendable {
        private let seconds: Mutex<Double>

        init(_ seconds: Int) {
            self.seconds = Mutex(Double(seconds))
        }

        var now: Date { Date(timeIntervalSince1970: seconds.withLock { $0 }) }

        func advance(_ by: Double) { seconds.withLock { $0 += by } }
    }

    /// Вход в момент `issued` по часам телефона, сбитым на `skew` секунд.
    private static func signedIn(_ tokens: AuthTokens, skew: Int = 0) async -> (TokenStore, Clock) {
        let clock = Clock(issued + skew)
        let store = TokenStore(storage: InMemoryTokenStorage(), now: { clock.now })
        await store.signIn(tokens)
        return (store, clock)
    }

    @Test("Токену жить ещё больше минуты — отдаётся как есть, без обновления")
    func fresh() async throws {
        let (store, clock) = await Self.signedIn(Self.tokens())
        clock.advance(600)
        let refresher = FakeRefresher(.renewed(Self.renewed))

        #expect(try await store.validAccessToken(using: refresher) == Self.tokens().accessToken)
        #expect(await refresher.calls.isEmpty)
    }

    @Test(
        "Жить меньше минуты или уже истёк — обновляется, новая пара сохранена",
        arguments: [59.0, 0, -300])
    func expiring(remaining: Double) async throws {
        let (store, clock) = await Self.signedIn(Self.tokens())
        clock.advance(900 - remaining)
        let refresher = FakeRefresher(.renewed(Self.renewed))

        #expect(try await store.validAccessToken(using: refresher) == Self.renewed.accessToken)
        #expect(await refresher.calls == ["R1"])
        #expect(await store.current() == Self.renewed)
    }

    @Test("Ровно минута в запасе — ещё годится")
    func boundary() async throws {
        let (store, clock) = await Self.signedIn(Self.tokens())
        clock.advance(840)
        let refresher = FakeRefresher(.renewed(Self.renewed))

        #expect(try await store.validAccessToken(using: refresher) == Self.tokens().accessToken)
        #expect(await refresher.calls.isEmpty)
    }

    @Test(
        "Часы телефона сбиты — срок считается от получения токена, а не по часам",
        arguments: [1_200, -1_200])
    func skewedClock(skew: Int) async throws {
        let (store, clock) = await Self.signedIn(Self.tokens(), skew: skew)
        let refresher = FakeRefresher(.renewed(Self.renewed))

        clock.advance(830)
        #expect(try await store.validAccessToken(using: refresher) == Self.tokens().accessToken)
        #expect(await refresher.calls.isEmpty)

        clock.advance(20)
        #expect(try await store.validAccessToken(using: refresher) == Self.renewed.accessToken)
        #expect(await refresher.calls == ["R1"])
    }

    @Test("После перезапуска (токен из хранилища) — срок по часам телефона")
    func afterRelaunch() async throws {
        let storage = InMemoryTokenStorage()
        await TokenStore(storage: storage).signIn(Self.tokens())
        let clock = Clock(Self.issued + 850)
        let relaunched = TokenStore(storage: storage, now: { clock.now })
        let refresher = FakeRefresher(.renewed(Self.renewed))

        #expect(try await relaunched.validAccessToken(using: refresher) == Self.renewed.accessToken)
        #expect(await refresher.calls == ["R1"])
    }

    @Test("Без iat — срок по часам телефона")
    func noIssuedAt() async throws {
        let tokens = AuthTokens(
            accessToken: Jwt.make(#"{"sub":"p-1","exp":\#(Self.issued + 900)}"#), refreshToken: "R1")
        let (store, _) = await Self.signedIn(tokens, skew: 1_200)
        let refresher = FakeRefresher(.renewed(Self.renewed))

        #expect(try await store.validAccessToken(using: refresher) == Self.renewed.accessToken)
    }

    @Test("Срок не указан — токен отдаётся как есть: судит сервер")
    func noExpiry() async throws {
        let tokens = AuthTokens(accessToken: Jwt.make(#"{"sub":"p-1"}"#), refreshToken: "R1")
        let (store, _) = await Self.signedIn(tokens)
        let refresher = FakeRefresher(.renewed(Self.renewed))

        #expect(try await store.validAccessToken(using: refresher) == tokens.accessToken)
        #expect(await refresher.calls.isEmpty)
    }

    @Test("Входа нет — nil без запроса к серверу")
    func signedOut() async throws {
        let refresher = FakeRefresher(.renewed(Self.renewed))
        let store = TokenStore(storage: InMemoryTokenStorage())

        #expect(try await store.validAccessToken(using: refresher) == nil)
        #expect(await refresher.calls.isEmpty)
    }

    @Test("Сервер отверг refresh-токен — nil и выход")
    func rejected() async throws {
        let (store, clock) = await Self.signedIn(Self.tokens())
        clock.advance(900)

        #expect(try await store.validAccessToken(using: FakeRefresher(.rejected(code: "refresh_invalid"))) == nil)
        #expect(await store.current() == nil)
        #expect(
            await authEvents(of: store, count: 2) == [.signedIn, .signedOut(.sessionExpired(code: "refresh_invalid"))])
    }

    @Test("Нет сети при обновлении — ошибка, вход сохраняется")
    func offline() async throws {
        let (store, clock) = await Self.signedIn(Self.tokens())
        clock.advance(900)

        await #expect(throws: FakeRefresher.NetworkDown.self) {
            try await store.validAccessToken(using: FakeRefresher(failing: .init()))
        }
        #expect(await store.current() == Self.tokens())
    }

    @Test("Игрок вышел и вошёл, пока шло обновление, — токен нового входа, а не nil")
    func sessionChangedDuringRefresh() async throws {
        let (store, clock) = await Self.signedIn(Self.tokens())
        clock.advance(900)
        let other = Self.tokens(shift: 900, refresh: "B1", player: "p-2")
        let refresher = FakeRefresher(.renewed(Self.renewed))
        await refresher.whileRefreshing { await store.signIn(other) }

        #expect(try await store.validAccessToken(using: refresher) == other.accessToken)
        #expect(await refresher.calls == ["R1"])
        #expect(await store.current() == other)
    }

    @Test("Истекающий токен запросили трое, пока шло обновление, — одно обновление, всем новый токен")
    func singleFlight() async throws {
        let (store, clock) = await Self.signedIn(Self.tokens())
        clock.advance(900)
        let refresher = FakeRefresher(.renewed(Self.renewed))
        await refresher.hold()

        async let first = store.validAccessToken(using: refresher)
        try await waitUntil { await refresher.calls.count == 1 }
        // Обновление держится: второй и третий застают старый токен и должны присоединиться к нему.
        async let second = store.validAccessToken(using: refresher)
        async let third = store.validAccessToken(using: refresher)
        try await Task.sleep(for: .milliseconds(100))
        await refresher.release()

        let results = try await [first, second, third]
        #expect(results == Array(repeating: Self.renewed.accessToken, count: 3))
        #expect(await refresher.calls == ["R1"])
    }
}

@Suite("Токены входа: срок access-токена")
struct AccessExpiryTests {
    @Test("Срок — claim exp, выдан — claim iat, секунды Unix")
    func expiry() {
        let tokens = AuthTokens(
            accessToken: Jwt.make(#"{"sub":"p-1","iat":1790000000,"exp":1790000900}"#), refreshToken: "R1")
        #expect(tokens.accessExpiresAt == Date(timeIntervalSince1970: 1_790_000_900))
        #expect(tokens.accessIssuedAt == Date(timeIntervalSince1970: 1_790_000_000))
    }

    @Test(
        "Без exp или не JWT — срок неизвестен",
        arguments: [
            Jwt.make(#"{"sub":"p-1"}"#), Jwt.make(#"{"exp":"завтра"}"#), "eyJhbGciOiJIUzI1NiJ9.e30.c2lnbmF0dXJl", "abc",
        ])
    func unknown(_ token: String) {
        #expect(AuthTokens(accessToken: token, refreshToken: "R1").accessExpiresAt == nil)
    }

    @Test("Неверный тип одного claim не прячет другой")
    func independentClaims() {
        let badSubject = AuthTokens(accessToken: Jwt.make(#"{"sub":42,"exp":1790000900}"#), refreshToken: "R1")
        #expect(badSubject.playerId == nil)
        #expect(badSubject.accessExpiresAt == Date(timeIntervalSince1970: 1_790_000_900))

        let badExpiry = AuthTokens(accessToken: Jwt.make(#"{"sub":"p-1","exp":"завтра"}"#), refreshToken: "R1")
        #expect(badExpiry.playerId == "p-1")
        #expect(badExpiry.accessExpiresAt == nil)
    }
}
