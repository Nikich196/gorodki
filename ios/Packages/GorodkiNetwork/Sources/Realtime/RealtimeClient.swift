import Foundation
import GorodkiAPI
import Networking

/// Реальное время в приложении (PLAN.md, D6; docs/architecture/realtime.md): одно соединение с хабом, пока приложение
/// на переднем плане. После каждого подключения — подписка на лигу и событие `.connected` (пересинхронизироваться),
/// дальше — подсказки сервера в `events`.
///
/// Переподключение — без конца, пока не остановят:
/// - соединение прожило не меньше `stableAfter` (обычно сервер закрыл его, потому что истёк access-токен) — сразу;
/// - иначе через 1, 2, 4… до 30 секунд: холодный старт сервера на Render Free дольше стандартной политики SignalR
///   (0, 2, 10, 30 с — и сдаться);
/// - сеть вернулась или приложение открыто (`wake`) — сразу, и шаг повторов сначала.
///
/// Потеря подсказки ничего не ломает: `.connected` после разрыва и запасной опрос (синхронизация) догоняют пропущенное.
public actor RealtimeClient {
    public enum State: Equatable, Sendable {
        case stopped
        case connecting
        case connected
        /// Ждёт повтора подключения.
        case waiting(Duration)
    }

    public static let longestRetry: Duration = .seconds(30)
    /// Сколько секунд соединение должно прожить, чтобы его закрытие не считалось неудачей.
    public static let stableAfter: Double = 30

    /// Подсказки. Слушает один подписчик (приложение); если он отстал, теряются самые старые — их догонит `.connected`.
    public nonisolated let events: AsyncStream<RealtimeEvent>
    public private(set) var state: State = .stopped
    /// Лига, на которую подписываться.
    public private(set) var league: League

    private let connector: any RealtimeConnector
    private let sleep: @Sendable (Duration) async throws -> Void
    private let now: @Sendable () -> Double
    private let eventsContinuation: AsyncStream<RealtimeEvent>.Continuation
    /// Цикл подключения. Номер отличает текущий цикл от остановленного, который ещё не заметил остановки.
    private var loop: (id: Int, task: Task<Void, Never>)?
    private var loopCount = 0
    private var connection: (loop: Int, link: any RealtimeConnection)?
    private var pause: Task<Void, Never>?
    /// Просили подключиться сейчас (`wake`), пока шла попытка или пауза: следующая попытка — без паузы.
    private var woken = false

    /// - Parameters:
    ///   - now: монотонные часы в секундах — сколько прожило соединение.
    public init(
        connector: any RealtimeConnector,
        league: League = .run,
        sleep: @escaping @Sendable (Duration) async throws -> Void = { try await Task.sleep(for: $0) },
        now: @escaping @Sendable () -> Double = { ProcessInfo.processInfo.systemUptime }
    ) {
        self.connector = connector
        self.league = league
        self.sleep = sleep
        self.now = now
        (events, eventsContinuation) = AsyncStream.makeStream(bufferingPolicy: .bufferingNewest(64))
    }

    deinit {
        eventsContinuation.finish()
    }

    /// Пауза перед подключением после `failures` неудач подряд: сразу, 1, 2, 4… до 30 секунд.
    public static func retryDelay(afterFailures failures: Int) -> Duration {
        guard failures > 0 else { return .zero }
        return min(.seconds(1 << min(failures - 1, 5)), longestRetry)
    }

    /// Подключиться (приложение на переднем плане, игрок вошёл). Если цикл подключения уже идёт — как `wake`.
    public func start() {
        guard loop == nil else {
            wake()
            return
        }
        loopCount += 1
        let id = loopCount
        loop = (id, Task { await self.run(id) })
    }

    /// Отключиться (приложение ушло в фон, игрок вышел или сменился).
    public func stop() async {
        loop?.task.cancel()
        loop = nil
        woken = false
        let open = connection?.link
        connection = nil
        state = .stopped
        await open?.close()
    }

    /// Сеть вернулась или приложение открыто: подключиться сейчас, шаг повторов сначала. Во время паузы она
    /// прерывается; во время попытки — если попытка не удастся, следующая пойдёт без паузы (она могла споткнуться
    /// о прежнюю сеть).
    public func wake() {
        switch state {
        case .waiting:
            woken = true
            pause?.cancel()
        case .connecting:
            woken = true
        case .connected, .stopped:
            break
        }
    }

    /// Сменить лигу. Подписка меняется на текущем соединении и действует для всех следующих.
    public func select(_ league: League) async {
        guard league != self.league else { return }
        self.league = league
        guard state == .connected, let open = connection?.link else { return }
        do {
            try await open.subscribe(to: league)
        } catch {
            // Подписка не прошла — соединение закрывается, переподключение подпишется на новую лигу.
            await open.close()
        }
    }

    private func run(_ id: Int) async {
        var failures = 0
        while isCurrent(id) {
            state = .connecting
            var connectedAt: Double?
            do {
                let open = try await connector.connect()
                guard isCurrent(id) else {
                    await open.close()
                    return
                }
                connection = (id, open)
                // Лигу могли сменить, пока шла подписка, — тогда подписаться ещё раз.
                var subscribed: League
                repeat {
                    subscribed = league
                    try await open.subscribe(to: subscribed)
                } while subscribed != league && isCurrent(id)
                guard isCurrent(id) else { return }
                state = .connected
                woken = false
                connectedAt = now()
                eventsContinuation.yield(.connected)
                for await message in open.messages {
                    // Остановили, пока сообщение было в пути, — оно уже не нужно.
                    guard isCurrent(id) else { break }
                    eventsContinuation.yield(message)
                }
            } catch RealtimeConnectError.notSignedIn {
                // Подключаться не с чем: цикл кончается, приложение запустит его после входа.
                if isCurrent(id) {
                    loop = nil
                    state = .stopped
                }
                return
            } catch {
                // Нет сети, сервер не отвечает или отказал — повтор ниже.
            }

            if let current = connection, current.loop == id {
                connection = nil
                await current.link.close()
            }
            guard isCurrent(id) else { return }
            if woken || connectedAt.map({ now() - $0 >= Self.stableAfter }) == true {
                woken = false
                failures = 0
            } else {
                failures += 1
            }

            let delay = Self.retryDelay(afterFailures: failures)
            guard delay > .zero else { continue }
            state = .waiting(delay)
            await wait(delay)
            if woken {
                woken = false
                failures = 0
            }
        }
    }

    private func isCurrent(_ id: Int) -> Bool {
        loop?.id == id && !Task.isCancelled
    }

    /// Пауза, которую прерывает `wake` (или остановка).
    private func wait(_ delay: Duration) async {
        let sleep = self.sleep
        let task = Task { _ = try? await sleep(delay) }
        pause = task
        await withTaskCancellationHandler {
            await task.value
        } onCancel: {
            task.cancel()
        }
        if pause == task {
            pause = nil
        }
    }
}
