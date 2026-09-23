import Foundation

/// Что случилось со входом. Слушает один подписчик (модель входа приложения): `AsyncStream` отдаёт каждое событие
/// только одному из слушателей.
public enum AuthEvent: Equatable, Sendable {
    case signedIn
    case signedOut(SignOutReason)
}

public enum SignOutReason: Equatable, Sendable {
    /// Игрок вышел сам.
    case userRequested
    /// Сервер отверг refresh-токен (`refresh_invalid` — истёк или отозван, `refresh_reused` — вход отменён из
    /// соображений безопасности): нужно войти заново.
    case sessionExpired(code: String?)
}

/// Итог обмена refresh-токена на новую пару.
public enum RefreshResult: Equatable, Sendable {
    case renewed(AuthTokens)
    /// Сервер отверг refresh-токен (код из ProblemDetails): вход потерян.
    case rejected(code: String?)
}

/// Обмен refresh-токена на новую пару (`POST /auth/refresh`). Ошибка (нет сети, 5xx, 429) значит «попробовать позже»,
/// а не выход: иначе телефон без сети терял бы вход.
public protocol TokenRefresher: Sendable {
    func refresh(_ refreshToken: String) async throws -> RefreshResult
}

/// Токены входа (PLAN.md, §7.2: актор `TokenStore`): копия в памяти поверх постоянного хранилища (Keychain).
///
/// Через него же идёт обновление access-токена: запросы, получившие 401 одновременно, ждут одного обновления — второе
/// с тем же refresh-токеном сервер принял бы за повтор, а через 60 секунд и за кражу.
public actor TokenStore {
    /// События входа — см. `AuthEvent`.
    public nonisolated let events: AsyncStream<AuthEvent>

    private let storage: any TokenStorage
    private let eventsContinuation: AsyncStream<AuthEvent>.Continuation
    private var cache: Cache = .unknown
    /// Копия в памяти новее хранилища (запись не удалась): запись повторяется при следующем обращении.
    private var needsSave = false
    /// Access-токены этого входа, уже заменённые обновлением, — последние несколько. Запрос, ушедший с таким токеном,
    /// повторяется с текущим без нового обновления. Токен чужого входа (игрок вышел и вошёл) сюда не попадает.
    private var superseded: [String] = []
    private var refreshing: (number: Int, task: Task<AuthTokens?, any Error>)?
    private var refreshCount = 0

    private enum Cache {
        /// Хранилище ещё не прочитано или было недоступно.
        case unknown
        case loaded(AuthTokens?)
    }

    public init(storage: any TokenStorage) {
        self.storage = storage
        (events, eventsContinuation) = AsyncStream.makeStream(bufferingPolicy: .bufferingNewest(16))
    }

    deinit {
        eventsContinuation.finish()
    }

    /// Текущие токены; `nil` — вход не выполнен или хранилище сейчас недоступно (тогда следующий вызов прочитает его
    /// снова: недоступный Keychain — ещё не выход).
    public func current() -> AuthTokens? {
        switch cache {
        case .loaded(let tokens):
            if needsSave, let tokens {
                persist(tokens)
            }
            return tokens
        case .unknown:
            do {
                // Испорченная запись — как отсутствие входа: следующий вход её перезапишет.
                let tokens = try storage.load().flatMap { try? JSONDecoder().decode(AuthTokens.self, from: $0) }
                cache = .loaded(tokens)
                return tokens
            } catch {
                return nil
            }
        }
    }

    /// Вход выполнен (`POST /auth/google`): токены сохраняются. Если Keychain сейчас недоступен, вход работает по копии
    /// в памяти, а запись повторяется при следующем обращении.
    public func signIn(_ tokens: AuthTokens) {
        startSession()
        store(tokens)
        eventsContinuation.yield(.signedIn)
    }

    /// Выход: токены стираются с телефона. Отозвать их на сервере (`POST /auth/logout`) нужно до этого, пока
    /// refresh-токен ещё известен.
    public func signOut() {
        clear(.userRequested)
    }

    /// Access-токен `rejected` получил 401 — нужна новая пара.
    ///
    /// Если пару уже обновили (запрос ушёл со старым токеном), возвращается текущая без запроса к серверу; если
    /// обновление идёт, вызов ждёт его. Так на любое число одновременных 401 приходится одно `POST /auth/refresh`.
    /// - Returns: новые токены или `nil`, если входа, к которому относится `rejected`, больше нет: не выполнен, сервер
    ///   отверг refresh-токен или игрок успел выйти и войти заново.
    /// - Throws: ошибку обновления (нет сети, сервер недоступен): вход сохраняется, запрос повторится позже.
    public func renew(after rejected: String, using refresher: any TokenRefresher) async throws -> AuthTokens? {
        guard let tokens = current() else { return nil }
        guard tokens.accessToken == rejected else {
            return superseded.contains(rejected) ? tokens : nil
        }
        if let refreshing {
            return try await refreshing.task.value
        }
        refreshCount += 1
        let number = refreshCount
        let task = Task { try await self.refresh(tokens, using: refresher, number: number) }
        refreshing = (number, task)
        return try await task.value
    }

    private func refresh(_ tokens: AuthTokens, using refresher: any TokenRefresher, number: Int) async throws
        -> AuthTokens?
    {
        defer {
            if refreshing?.number == number {
                refreshing = nil
            }
        }
        let result = try await refresher.refresh(tokens.refreshToken)
        // Пока сервер отвечал, игрок мог выйти или войти заново — тогда ответ относится к прежнему входу.
        guard current()?.refreshToken == tokens.refreshToken else { return nil }
        switch result {
        case .renewed(let renewed):
            superseded = Array((superseded + [tokens.accessToken]).suffix(4))
            store(renewed)
            return renewed
        case .rejected(let code):
            clear(.sessionExpired(code: code))
            return nil
        }
    }

    /// Новый вход (или выход): ответы обновлений прежнего входа больше ничего не меняют.
    private func startSession() {
        refreshing = nil
        superseded = []
    }

    private func store(_ tokens: AuthTokens) {
        cache = .loaded(tokens)
        persist(tokens)
    }

    private func persist(_ tokens: AuthTokens) {
        do {
            try storage.save(JSONEncoder().encode(tokens))
            needsSave = false
        } catch {
            needsSave = true
        }
    }

    private func clear(_ reason: SignOutReason) {
        startSession()
        cache = .loaded(nil)
        needsSave = false
        // Если стереть не удалось, после перезапуска вернётся старый вход: первый же запрос получит 401, обновление
        // будет отвергнуто, и вход сотрётся снова.
        try? storage.delete()
        eventsContinuation.yield(.signedOut(reason))
    }
}
