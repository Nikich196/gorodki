import Foundation
import GameCore

/// Что приходит в идущий забег. Всё — через одну очередь `RunTracker`, строго по порядку поступления.
public enum TrackerInput: Sendable {
    /// Точка GPS. `receivedAt` — когда телефон её получил (ставит источник): по нему судья решает, не устарела ли точка.
    /// Момент обработки для этого не годится — очередь может задержать точку дольше 10 секунд.
    case fix(LocationFix, receivedAt: Double)
    /// Вид движения от CoreMotion.
    case motion(MotionSample)
    /// Шаги от шагомера.
    case steps(PedometerSample)
    /// Таймер (раз в `RunTracker.tickInterval`): отметка датчиков, «созревший» кусок, предел длины забега.
    case tick(now: Double)
}

/// Что видно экрану забега.
public struct TrackerState: Equatable, Sendable {
    /// Идёт ли забег.
    public var isRunning = false
    /// Забег — идущий или последний завершённый (для экрана итога).
    public var runId: UUID?
    public var league: League?
    public var stats = RunStats()
    /// Последнее заметное событие — для подсказки на экране: петля заявлена, след порван, точка отброшена.
    public var lastEvent: RunEvent?
    /// Запись в очередь не удалась (например, нет места): точки забега могут теряться — сказать игроку.
    public var storageFailed = false
    /// Забег завершился сам — вышел предел длины.
    public var endedAtLimit = false

    public init() {}
}

public enum TrackerError: Error, Equatable {
    /// Забег уже идёт (или начинается): второй «Старт» не записывается в очередь.
    case alreadyRunning
}

/// Идущий забег (PLAN.md, §7.2 «Трекинг»): точки GPS, датчики и таймер — в `RunSession` строго по одному, в порядке
/// поступления; синхронизация — когда запечатан кусок или заявлена петля.
///
/// Источники (CoreLocation, CoreMotion, таймер) живут в приложении и шлют всё в `send` — очередь без ожидания, её
/// порядок и есть порядок обработки. Пока забег не начат или не продолжен, очередь копит поступившее: после перезапуска
/// приложения источники запускаются сразу (иначе iOS может усыпить приложение), а забег из базы подключается позже.
/// Экрану — `onChange` со снимком состояния (`TrackerState`), без координат.
public actor RunTracker {
    /// Как часто приложение шлёт `.tick`.
    public static let tickInterval: Duration = .seconds(5)
    /// Запаздывание CoreMotion: записи о виде движения и шагах приходят позже своего времени, поэтому отметка «все
    /// датчики до этого момента получены» отстаёт от «сейчас». Точное значение — по замеру в спайке S1 (строки
    /// «Запаздывание…» в сводке «Лаборатории»: получение минус время записи); до замера — 10 секунд.
    public static let sensorLagSeconds: Double = 10
    /// Продолжить забег после перезапуска можно, если последняя записанная точка (или старт) не старше этого.
    public static let maxResumeGapSeconds: Double = 10 * 60

    private enum Command: Sendable {
        case input(TrackerInput)
        case finish(at: Double, CheckedContinuation<Void, any Error>)
        case drain(CheckedContinuation<Void, Never>)
        /// Забег подключён: накопленное, пока он начинался, — в него, по порядку.
        case attached(CheckedContinuation<Void, Never>)
    }

    private let onQueued: @Sendable () -> Void
    private let onChange: @Sendable (TrackerState) -> Void
    private let commands: AsyncStream<Command>
    private nonisolated let queue: AsyncStream<Command>.Continuation
    private var loop: Task<Void, Never>?
    private var session: RunSession?
    private var starting = false
    /// Пока забег начинается или продолжается, поступившее копится здесь, в порядке очереди, и уходит в него после
    /// подключения. Иначе терялись бы первые точки и первая запись CoreMotion о текущем виде движения (с давним
    /// временем начала: следующей может не быть до смены вида движения).
    private var buffering = false
    private var pending: [TrackerInput] = []
    private var sealed = 0
    /// Идёт запись для демо-повтора. Пишется при обработке, а не в `send`: в запись попадает ровно то, что получил забег,
    /// в том же порядке — и накопленное, пока он начинался (прежде оно терялось, а с ним первая запись CoreMotion о виде
    /// движения, и повтор судил без неё).
    private var recording: RunRecordingBuffer?
    private var finishedRecording: RunRecording?
    public private(set) var state = TrackerState() {
        didSet {
            if state != oldValue { onChange(state) }
        }
    }

    /// - Parameters:
    ///   - onQueued: в очереди синхронизации новое (кусок, заявка, конец забега) — приложение зовёт синхронизацию.
    ///   - onChange: снимок для экрана изменился.
    public init(
        onQueued: @escaping @Sendable () -> Void = {},
        onChange: @escaping @Sendable (TrackerState) -> Void = { _ in }
    ) {
        self.onQueued = onQueued
        self.onChange = onChange
        (commands, queue) = AsyncStream.makeStream(of: Command.self)
    }

    /// Поступление от источника — без ожидания, в порядке вызовов.
    public nonisolated func send(_ input: TrackerInput) {
        queue.yield(.input(input))
    }

    /// «Старт». `makeSession` записывает забег в очередь (`AppDependencies.startRun`) — только если забега нет: второй
    /// «Старт» отклоняется до записи в базу.
    /// - Parameter recording: записать забег для демо-повтора (`takeRecording` после «Финиша»).
    public func start(recording: Bool = false, _ makeSession: @Sendable () async throws -> RunSession) async throws {
        guard session == nil, !starting else { throw TrackerError.alreadyRunning }
        starting = true
        buffering = true
        defer { starting = false }
        let session: RunSession
        do {
            session = try await makeSession()
        } catch {
            buffering = false
            pending = []
            throw error
        }
        finishedRecording = nil
        if recording {
            self.recording = RunRecordingBuffer(startedAt: Double(session.startedAtMs) / 1_000, league: session.league)
        }
        await attach(session, stats: RunStats(), sealed: 0)
        onQueued()  // забег в очереди: сервер узнает о нём раньше первой петли
    }

    /// Запись последнего забега для демо-повтора (если он записывался) — один раз.
    public func takeRecording() -> RunRecording? {
        defer { finishedRecording = nil }
        return finishedRecording
    }

    /// Продолжить забег после перезапуска приложения (`recover`). Поступившее до этого обрабатывается уже в нём.
    public func resume(_ session: RunSession) async throws {
        guard self.session == nil, !starting else { throw TrackerError.alreadyRunning }
        starting = true
        buffering = true
        defer { starting = false }
        await attach(session, stats: await session.stats, sealed: await session.sealedChunks)
    }

    /// Дождаться, пока обработано всё поступившее. Без забега оно выбрасывается: так после перезапуска, когда продолжать
    /// нечего, уходит накопленное источниками.
    public func flush() async {
        ensureLoop()
        await withCheckedContinuation { queue.yield(.drain($0)) }
    }

    /// «Финиш». Точки, поступившие раньше, обрабатываются до конца забега.
    /// - Throws: ошибку записи в очередь — забег продолжается, «Финиш» можно повторить.
    public func finish(at seconds: Double) async throws {
        ensureLoop()
        try await withCheckedThrowingContinuation { queue.yield(.finish(at: seconds, $0)) }
    }

    private func attach(_ session: RunSession, stats: RunStats, sealed: Int) async {
        self.session = session
        self.sealed = sealed
        var fresh = TrackerState()
        fresh.isRunning = true
        fresh.runId = session.runId
        fresh.league = session.league
        fresh.stats = stats
        state = fresh
        ensureLoop()
        // Всё, что уже в очереди, — после накопленного: метка встаёт в ту же очередь.
        await withCheckedContinuation { queue.yield(.attached($0)) }
    }

    private func releasePending(into session: RunSession) async {
        guard buffering else { return }
        buffering = false
        let inputs = pending
        pending = []
        for input in inputs {
            await process(input, in: session)
        }
    }

    private func ensureLoop() {
        guard loop == nil else { return }
        let commands = self.commands
        loop = Task { [weak self] in
            for await command in commands {
                guard let self else { return }
                await self.handle(command)
            }
        }
    }

    private func handle(_ command: Command) async {
        switch command {
        case .input(let input):
            if buffering { return pending.append(input) }
            guard let session else { return }  // забега нет: поступление не относится ни к одному
            await process(input, in: session)
        case .attached(let done):
            if let session { await releasePending(into: session) }
            done.resume()
        case .finish(let seconds, let done):
            guard let session else { return done.resume() }
            await releasePending(into: session)  // «Финиш» раньше метки: накопленное — до конца забега
            do {
                try await session.finish(endedAt: seconds)
            } catch {
                state.storageFailed = true
                return done.resume(throwing: error)
            }
            if let buffer = recording.take() {
                finishedRecording = buffer.finish(at: seconds)
            }
            await ended(session)
            done.resume()
        case .drain(let done):
            done.resume()
        }
    }

    private func process(_ input: TrackerInput, in session: RunSession) async {
        recording?.append(input)
        var events: [RunEvent] = []
        do {
            switch input {
            case .fix(let fix, let receivedAt):
                events = try await session.handle(fix, now: receivedAt)
                state.storageFailed = false  // запись точки удалась
            case .motion(let sample):
                await session.record(sample)
            case .steps(let sample):
                await session.record(sample)
            case .tick(let now):
                await session.sensorsComplete(through: now - Self.sensorLagSeconds)
                events = try await session.tick(now: now)
            }
        } catch {
            state.storageFailed = true
        }
        if let notable = events.last(where: { $0 != .accepted && $0 != .dropped }) {
            state.lastEvent = notable
        }
        if events.contains(.finishedAtLimit) {
            state.endedAtLimit = true
            await ended(session)
            return
        }
        state.stats = await session.stats
        let claimed = events.contains { if case .loopClaimed = $0 { true } else { false } }
        let sealedNow = await session.sealedChunks
        if claimed || sealedNow != sealed {
            sealed = sealedNow
            onQueued()
        }
    }

    private func ended(_ session: RunSession) async {
        if let buffer = recording.take() {  // конец по пределу длины
            finishedRecording = buffer.finishAtLastEntry()
        }
        state.stats = await session.stats
        state.isRunning = false
        self.session = nil
        onQueued()
    }

    // MARK: - Продолжение после перезапуска

    /// Какой забег продолжить после перезапуска приложения. Продолжается забег этого устройства, не завершённый и не
    /// отвергнутый сервером, игрока, который вошёл (или ничей вход: он мог истечь — запись от входа не зависит), если
    /// правила его версии известны, предел длины не вышел и последняя записанная точка (или старт) не старше
    /// `maxResumeGapSeconds`. Все остальные незавершённые забеги закрываются — их петли иначе ждали бы датчиков вечно.
    /// - Parameter rules: правила версии конфига (`RulesStore.rules(version:)`); `nil` — неизвестна.
    public static func recover(
        store: any SyncStore, deviceId: UUID, signedIn playerId: String?, now: Double,
        rules: @Sendable (Int) async -> PhoneRules?, policy: ChunkPolicy = ChunkPolicy()
    ) async throws -> RunSession? {
        let nowMs = StoragePrecision.milliseconds(now)
        var chosen: (run: LocalRun, rules: PhoneRules)?
        let open = try await store.runs().filter { !$0.isFinishedLocally && $0.serverState != .rejected }
        for run in open.sorted(by: { $0.startedAtMs > $1.startedAtMs }) {
            // Демо-повтор не продолжается: настоящая геопозиция в забеге-повторе была бы подделкой.
            guard run.source == .live, run.deviceId == deviceId, playerId == nil || run.ownerId == playerId,
                let known = await rules(run.configVersion)
            else { continue }
            let limitMs = run.startedAtMs + Int64(known.maxRunHours * 3_600_000)
            let lastMs = run.lastPointMs ?? run.startedAtMs
            if nowMs <= limitMs, nowMs - lastMs <= Int64(maxResumeGapSeconds * 1_000) {
                chosen = (run, known)
                break
            }
        }
        try await RunRecorder.closeInterrupted(except: chosen?.run.id, store: store)
        guard let chosen else { return nil }
        return try await RunSession.resume(runId: chosen.run.id, store: store, rules: chosen.rules, policy: policy)
    }
}
