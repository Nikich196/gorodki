import Foundation
import GameCore
import Synchronization

/// Запись забега для демо-повтора (PLAN.md §7.2 «Демо-повтор»): всё, что пришло в трекер, — точки, вид движения, шаги —
/// с временем от старта. Абсолютной даты нет: повтор сдвигает запись в прошлое, а дата прогулки не нужна.
///
/// В записи — настоящий маршрут: хранится только на телефоне (в репозиторий записи не кладутся, PLAN.md §13).
public struct RunRecording: Codable, Sendable, Equatable {
    public static let currentVersion = 1

    public var version = RunRecording.currentVersion
    public var league: League
    /// Длина записи — от «Старта» до «Финиша», секунды.
    public var duration: Double
    public var entries: [Entry]

    public struct Entry: Codable, Sendable, Equatable {
        /// Когда поступило — секунды от старта (время получения).
        public var at: Double
        public var input: Input
    }

    /// Поступление; все времена — секунды от старта.
    public enum Input: Codable, Sendable, Equatable {
        case fix(
            latitude: Double, longitude: Double, time: Double, horizontalAccuracy: Double, speed: Double?, flags: Int)
        case motion(time: Double, activity: MotionActivity)
        case steps(start: Double, end: Double, steps: Int?)
    }

    public init(league: League, duration: Double, entries: [Entry]) {
        self.league = league
        self.duration = duration
        self.entries = entries
    }

    /// Были ли в записи датчики движения (для `motionAuthorized` забега-повтора).
    public var hasMotion: Bool {
        entries.contains {
            if case .fix = $0.input { false } else { true }
        }
    }
}

/// Идёт запись: поступления трекера с временем от старта — то, что получил забег, в порядке его очереди.
final class RunRecordingBuffer: Sendable {
    private let state: Mutex<(startedAt: Double, league: League, entries: [RunRecording.Entry])>

    init(startedAt: Double, league: League) {
        state = Mutex((startedAt, league, []))
    }

    func append(_ input: TrackerInput) {
        state.withLock { state in
            let start = state.startedAt
            let entry: RunRecording.Entry
            switch input {
            case .fix(let fix, let receivedAt):
                entry = .init(
                    at: receivedAt - start,
                    input: .fix(
                        latitude: fix.coordinate.latitude, longitude: fix.coordinate.longitude,
                        time: fix.timestamp - start, horizontalAccuracy: fix.horizontalAccuracy, speed: fix.speed,
                        flags: fix.source.rawValue))
            // Датчик пришёл не раньше уже записанного: в повторе он должен прийти в том же порядке, иначе запоздавшая
            // в оригинале запись (её отбросили) в повторе была бы принята, и вердикты судьи разошлись бы.
            case .motion(let sample):
                entry = .init(
                    at: max(sample.timestamp - start, lastAt(state.entries)),
                    input: .motion(time: sample.timestamp - start, activity: sample.activity))
            case .steps(let sample):
                entry = .init(
                    at: max(sample.end - start, lastAt(state.entries)),
                    input: .steps(start: sample.start - start, end: sample.end - start, steps: sample.steps))
            case .tick:
                return  // таймер повтор создаёт сам — по своему времени
            }
            state.entries.append(entry)
        }
    }

    private func lastAt(_ entries: [RunRecording.Entry]) -> Double { entries.last?.at ?? -.infinity }

    /// Забег закончился сам (предел длины): конец — последнее поступление.
    func finishAtLastEntry() -> RunRecording {
        let (start, last) = state.withLock { ($0.startedAt, $0.entries.map(\.at).max() ?? 0) }
        return finish(at: start + last)
    }

    func finish(at seconds: Double) -> RunRecording {
        state.withLock { state in
            let duration = max(0, seconds - state.startedAt)
            // Поступившее позже конца (по времени получения) в запись не входит: повтор кончается на этом времени.
            return RunRecording(
                league: state.league, duration: duration,
                entries: state.entries.filter { $0.at <= duration }.sorted { $0.at < $1.at })
        }
    }
}

/// Демо-повтор записи: поступления идут в трекер с ускорением и по «виртуальному времени» (PLAN.md §7.2).
///
/// Время записи сдвигается в прошлое: начало = «сейчас» − длина записи, промежутки прежние (docs/architecture/runs.md) —
/// при ускорении ×20 точки никогда не оказываются «из будущего». Таймер трекера в повторе — тоже виртуальный: настоящий
/// поставил бы отметку датчиков далеко впереди записи, и все записанные датчики отбросились бы как запоздавшие.
public struct RunReplay: Sendable {
    public let recording: RunRecording
    /// Во сколько раз быстрее записи.
    public let speed: Double

    public init(_ recording: RunRecording, speed: Double = 20) {
        self.recording = recording
        self.speed = max(1, speed)
    }

    /// Начало забега-повтора (секунды Unix) при запуске «сейчас».
    public func startedAt(now: Double) -> Double { now - recording.duration }

    /// Проиграть запись в трекер (забег-повтор в нём уже начат) и закончить его. Отмена — забег заканчивается на
    /// достигнутом виртуальном времени.
    /// - Parameters:
    ///   - startedAt: начало забега-повтора (`startedAt(now:)`).
    ///   - sleep: ожидание настоящего времени — подменяется в проверках.
    public func play(
        into tracker: RunTracker, startedAt: Double,
        sleep: @Sendable (Duration) async throws -> Void = { try await Task.sleep(for: $0) }
    ) async throws {
        let tick = Double(RunTracker.tickInterval.components.seconds)
        var virtual = 0.0  // секунды от начала записи
        var nextTick = tick
        func advance(to target: Double) async throws {
            while nextTick <= target {
                try await wait(nextTick - virtual)
                virtual = nextTick
                tracker.send(.tick(now: startedAt + virtual))
                nextTick += tick
            }
            try await wait(target - virtual)
            virtual = target
        }
        func wait(_ seconds: Double) async throws {
            guard seconds > 0 else { return }
            try await sleep(.milliseconds(Int64((seconds / speed * 1_000).rounded())))
        }

        do {
            for entry in recording.entries {
                try await advance(to: max(virtual, entry.at))
                tracker.send(Self.input(entry.input, receivedAt: startedAt + entry.at, startedAt: startedAt))
            }
            try await advance(to: max(virtual, recording.duration))
        } catch {
            try await tracker.finish(at: startedAt + virtual)
            throw error
        }
        try await tracker.finish(at: startedAt + recording.duration)
    }

    static func input(_ input: RunRecording.Input, receivedAt: Double, startedAt: Double) -> TrackerInput {
        switch input {
        case .fix(let latitude, let longitude, let time, let accuracy, let speed, let flags):
            .fix(
                LocationFix(
                    coordinate: Coordinate(latitude: latitude, longitude: longitude), timestamp: startedAt + time,
                    horizontalAccuracy: accuracy, speed: speed, source: PointSource(rawValue: flags)),
                receivedAt: receivedAt)
        case .motion(let time, let activity):
            .motion(MotionSample(timestamp: startedAt + time, activity: activity))
        case .steps(let start, let end, let steps):
            .steps(PedometerSample(start: startedAt + start, end: startedAt + end, steps: steps))
        }
    }
}
