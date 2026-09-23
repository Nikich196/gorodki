import Foundation
import GorodkiAPI
import Testing

@testable import Networking

@Suite("Токены входа: хранение")
struct TokenStoreTests {
    private static let tokens = AuthTokens(accessToken: "A1", refreshToken: "R1")

    @Test("Вход сохраняется и читается после перезапуска приложения (новый TokenStore на том же хранилище)")
    func roundTrip() async throws {
        let storage = InMemoryTokenStorage()
        await TokenStore(storage: storage).signIn(Self.tokens)

        let relaunched = TokenStore(storage: storage)
        #expect(await relaunched.current() == Self.tokens)
        let saved = try #require(storage.load())
        #expect(try JSONDecoder().decode(AuthTokens.self, from: saved) == Self.tokens)
    }

    @Test("Пустое хранилище — вход не выполнен")
    func empty() async {
        #expect(await TokenStore(storage: InMemoryTokenStorage()).current() == nil)
    }

    @Test("Выход стирает токены и сообщает об этом")
    func signOut() async {
        let storage = InMemoryTokenStorage()
        let store = TokenStore(storage: storage)
        await store.signIn(Self.tokens)

        await store.signOut()

        #expect(await store.current() == nil)
        #expect(storage.load() == nil)
        var events = store.events.makeAsyncIterator()
        #expect(await events.next() == .signedIn)
        #expect(await events.next() == .signedOut(.userRequested))
    }

    @Test("Недоступный Keychain — ещё не выход: вход читается, когда хранилище снова доступно")
    func storageUnavailable() async throws {
        let storage = FlakyStorage()
        try storage.save(JSONEncoder().encode(Self.tokens))
        storage.setAvailable(false)
        let store = TokenStore(storage: storage)

        #expect(await store.current() == nil)

        storage.setAvailable(true)
        #expect(await store.current() == Self.tokens)
    }

    @Test("Запись не удалась — вход работает по копии в памяти и дописывается, когда хранилище доступно")
    func saveRetried() async throws {
        let storage = FlakyStorage()
        let store = TokenStore(storage: storage)
        _ = await store.current()
        storage.setAvailable(false)

        await store.signIn(Self.tokens)
        #expect(await store.current() == Self.tokens)
        #expect(storage.stored == nil)

        storage.setAvailable(true)
        #expect(await store.current() == Self.tokens)
        let saved = try #require(storage.stored)
        #expect(try JSONDecoder().decode(AuthTokens.self, from: saved) == Self.tokens)
    }

    @Test("Испорченная запись — как отсутствие входа")
    func corrupted() async {
        let store = TokenStore(storage: InMemoryTokenStorage(Data("не JSON".utf8)))
        #expect(await store.current() == nil)
    }
}

@Suite("Токены входа: пара и игрок")
struct AuthTokensTests {
    @Test("Игрок — claim sub access-токена")
    func playerId() {
        let tokens = AuthTokens(
            accessToken: Jwt.make(#"{"sub":"0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b","role":"player","exp":1790000900}"#),
            refreshToken: "R1")
        #expect(tokens.playerId == "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b")
    }

    @Test(
        "Без sub или не JWT — игрок неизвестен",
        arguments: [
            // Образец ответа сервера: нагрузка пустая ({}).
            "eyJhbGciOiJIUzI1NiJ9.e30.c2lnbmF0dXJl",
            Jwt.make(#"{"sub":""}"#),
            Jwt.make(#"{"sub":42}"#),
            "не.jwt.вовсе",
            "abc",
            "",
        ])
    func noPlayer(_ token: String) {
        #expect(AuthTokens(accessToken: token, refreshToken: "R1").playerId == nil)
    }

    @Test("Нагрузка с символами Base64URL (- и _) и без выравнивания «=» читается")
    func base64URL() throws {
        // «?>» и «~~» дают в Base64 символы «/» и «+» (в JWT — «_» и «-»), 31 байт — два «=», которых в JWT нет.
        let token = Jwt.make(#"{"note":"?>?>~~~","sub":"p-12"}"#)
        let payload = try #require(token.split(separator: ".").dropFirst().first)
        #expect(payload.contains("_") && payload.contains("-") && !payload.contains("="))
        #expect(AuthTokens(accessToken: token, refreshToken: "R1").playerId == "p-12")
    }

    @Test("Из ответа сервера — образец contracts/samples/session.json")
    func fromSession() {
        let session = Components.Schemas.SessionResponse(
            accessToken: "eyJhbGciOiJIUzI1NiJ9.e30.c2lnbmF0dXJl", refreshToken: "cmVmcmVzaA", expiresIn: 900,
            isNewUser: true)
        #expect(
            AuthTokens(session)
                == AuthTokens(accessToken: "eyJhbGciOiJIUzI1NiJ9.e30.c2lnbmF0dXJl", refreshToken: "cmVmcmVzaA"))
    }

    @Test("Токены не попадают в журналы: при печати виден только игрок")
    func redacted() {
        let tokens = AuthTokens(accessToken: Jwt.make(#"{"sub":"p-1"}"#), refreshToken: "секрет-refresh")
        for text in [String(describing: tokens), String(reflecting: tokens), "\(tokens)"] {
            #expect(!text.contains(tokens.accessToken) && !text.contains("секрет-refresh"))
            #expect(text.contains("p-1"))
        }
    }
}
