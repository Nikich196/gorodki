import Foundation
import Networking
import SignalRClient

/// Подключение к хабу `/hubs/game` официальным клиентом SignalR (PLAN.md, D6: signalr-client-swift 1.0).
///
/// Каждое подключение — новое соединение SignalR без встроенного переподключения: повторами управляет `RealtimeClient`
/// (у встроенного политика короче холодного старта сервера), а токен каждого соединения — свежий: сервер закрывает
/// соединение, когда токен истекает, и следующее подключается уже с новым.
///
/// Две особенности клиента 1.0, под которые подстроен код:
/// - `invoke` ждёт ответа сервера без отмены, а закрытие соединения ожидающие вызовы не завершает: вызов, ответ на
///   который потерялся с соединением, висел бы вечно. Поэтому `Subscribe` уходит через `send` — без ожидания ответа
///   (ошибка у него одна, неизвестная лига, а лига здесь — из перечисления);
/// - `start` идёт в своей задаче и отмену вызывающего не замечает. Поэтому отмена и срок `connectTimeout` закрывают
///   соединение явно: остановленное приложение не держит лишних подключений (у сервера их не больше трёх на игрока).
public struct SignalRConnector: RealtimeConnector {
    /// Сколько ждать подключения целиком: согласование, WebSocket и рукопожатие.
    public static let connectTimeout: Duration = .seconds(60)

    private let hubURL: URL
    private let tokens: TokenStore
    private let refresher: any TokenRefresher

    public init(serverURL: URL, tokens: TokenStore, refresher: any TokenRefresher) {
        self.hubURL = serverURL.appending(path: "hubs/game")
        self.tokens = tokens
        self.refresher = refresher
    }

    public func connect() async throws -> any RealtimeConnection {
        guard let accessToken = try await tokens.validAccessToken(using: refresher) else {
            throw RealtimeConnectError.notSignedIn
        }
        var options = HttpConnectionOptions()
        options.accessTokenFactory = { accessToken }
        options.timeout = 30
        // Сообщения и адреса с токеном в журнал не пишутся: только предупреждения и ошибки.
        options.logLevel = .warning
        #if os(Linux)
            // Только для проверок на Linux: там клиент 1.0 не умеет WebSocket, а Server-Sent Events роняет URLSession
            // (бесконечный тайм-аут). На iPhone — WebSocket, как выберет согласование.
            options.transport = .longPolling
        #endif
        let hub = HubConnectionBuilder()
            .withUrl(url: hubURL.absoluteString, options: options)
            .withLogLevel(logLevel: .warning)
            .build()

        let (messages, continuation) = AsyncStream.makeStream(
            of: RealtimeEvent.self, bufferingPolicy: .bufferingNewest(64))
        await hub.on("TilesChanged") { (league: String, tiles: [[Int]]) in
            guard let league = League(rawValue: league) else { return }
            let keys = tiles.compactMap { $0.count == 2 ? TileKey(x: $0[0], y: $0[1]) : nil }
            continuation.yield(.tilesChanged(league, keys))
        }
        await hub.on("CaptureDecided") { (runId: String, captureId: String, status: String) in
            continuation.yield(.captureDecided(runId: runId, captureId: captureId, status: status))
        }
        await hub.on("FogChanged") {
            continuation.yield(.fogChanged)
        }
        await hub.onClosed { _ in continuation.finish() }

        let deadline = Task {
            try await Task.sleep(for: Self.connectTimeout)
            await hub.stop()
        }
        defer { deadline.cancel() }
        do {
            try await withTaskCancellationHandler {
                try await hub.start()
            } onCancel: {
                Task { await hub.stop() }
            }
        } catch {
            continuation.finish()
            if Self.isUnauthorized(error) {
                // Сервер не принял токен, который по часам телефона ещё действует (например, сменился ключ подписи):
                // следующее подключение пойдёт с обновлённым.
                _ = try? await tokens.renew(after: accessToken, using: refresher)
            }
            throw error
        }
        return Connection(hub: hub, messages: messages)
    }

    /// 401 на согласовании: клиент SignalR сообщает его только текстом ошибки.
    static func isUnauthorized(_ error: any Error) -> Bool {
        guard case SignalRError.negotiationError(let message) = error else { return false }
        return message.contains("'401'")
    }

    private struct Connection: RealtimeConnection {
        let hub: HubConnection
        let messages: AsyncStream<RealtimeEvent>

        func subscribe(to league: League) async throws {
            try await hub.send(method: "Subscribe", arguments: league.rawValue)
        }

        func close() async {
            await hub.stop()
        }
    }
}
