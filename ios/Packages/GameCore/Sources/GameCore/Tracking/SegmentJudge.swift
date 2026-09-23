/// Почему точка не принята или след порван. Причина показывается игроку.
public enum TrackIssue: String, Codable, Sendable {
    /// Слой 1: точность хуже допустимой.
    case poorAccuracy
    /// Слой 1: точка устарела.
    case staleFix
    /// Слой 1: время пошло назад.
    case timeWentBackwards
    /// Слой 1: скачок, которого не бывает у человека.
    case teleport
    /// Слой 2: средняя скорость за окно выше порога лиги.
    case tooFast
    /// Слой 2: датчики говорят «транспорт».
    case vehicle
    /// Слой 2: датчики говорят «велосипед» в лиге «Бег».
    case cycling
    /// Слой 2: движение без шагов.
    case noSteps
    /// Слой 2: длина шага не человеческая.
    case strideOutOfRange
    /// Слой 2: разгон, как у машины.
    case carLaunch
}

/// Решение по новой точке.
public enum JudgeVerdict: Equatable, Sendable {
    /// Точка принята в текущий отрезок.
    case accepted
    /// Точка отброшена, отрезок продолжается (плохая точность, устаревшая точка…).
    case ignored(TrackIssue)
    /// Отрезок порван: всё до этой точки — старый отрезок, с неё начинается новый.
    /// Петля не может охватывать два отрезка (PLAN.md, D16).
    case segmentBroken(TrackIssue)
}

/// Античит на телефоне, слои 1 и 2 (PLAN.md, §3.9). Сервер повторяет те же проверки той же версией правил.
///
/// Хранит только короткую историю: точки за 5 минут, датчики за 2 минуты.
public struct SegmentJudge: Sendable {
    public let league: League
    public let rules: LeagueRules

    private var points: [TrackPoint] = []
    private var motion: [MotionSample] = []
    private var pedometer: [PedometerSample] = []

    public init(league: League, rules: LeagueRules? = nil) {
        self.league = league
        self.rules = rules ?? .default(for: league)
    }

    public mutating func record(_ sample: MotionSample) {
        motion.append(sample)
        prune(now: sample.timestamp)
    }

    public mutating func record(_ sample: PedometerSample) {
        pedometer.append(sample)
        prune(now: sample.end)
    }

    /// Проверяет новую точку. `now` — текущее время телефона (для проверки «свежести»).
    public mutating func judge(_ point: TrackPoint, now: Double) -> JudgeVerdict {
        // Слой 1 — сама точка.
        if point.horizontalAccuracy > rules.maxAccuracyMeters || point.horizontalAccuracy < 0 {
            return .ignored(.poorAccuracy)
        }

        if now - point.timestamp > rules.maxFixAgeSeconds {
            return .ignored(.staleFix)
        }

        if let last = points.last {
            let dt = point.timestamp - last.timestamp
            if dt <= 0 {
                return .ignored(.timeWentBackwards)
            }

            if Geodesy.distance(from: last.coordinate, to: point.coordinate) / dt > rules.teleportMetersPerSecond {
                return breakSegment(at: point, .teleport)
            }
        }

        points.append(point)
        prune(now: point.timestamp)

        // Слой 2 — отрезки.
        if let issue = segmentIssue(now: point.timestamp) {
            return breakSegment(at: point, issue)
        }

        return .accepted
    }

    // MARK: - Слой 2

    private func segmentIssue(now: Double) -> TrackIssue? {
        for limit in rules.speedLimits {
            if let speed = averageSpeed(overLast: limit.windowSeconds, now: now),
                speed * 3.6 > limit.maxKilometersPerHour
            {
                return .tooFast
            }
        }

        if let seconds = rules.vehicleSeconds, continuousDuration(of: .automotive, now: now) >= seconds {
            return .vehicle
        }

        if let seconds = rules.cyclingSeconds, continuousDuration(of: .cycling, now: now) >= seconds {
            return .cycling
        }

        if let rule = rules.vehicleShare,
            share(of: .automotive, overLast: rule.windowSeconds, now: now) >= rule.minShare,
            let speed = averageSpeed(overLast: rule.speedWindowSeconds, now: now),
            speed * 3.6 > rule.minKilometersPerHour
        {
            return .vehicle
        }

        if let rule = rules.carLaunch, isCarLaunch(rule, now: now) {
            return .carLaunch
        }

        return stepIssue(now: now)
    }

    /// Средняя скорость за последние `window` секунд (путь / время) или nil, если истории ещё мало.
    private func averageSpeed(overLast window: Double, now: Double) -> Double? {
        guard let first = points.first, now - first.timestamp >= window else { return nil }
        let from = now - window
        var distance = 0.0
        var start: Double?
        for index in points.indices.dropFirst() where points[index].timestamp >= from {
            let previous = points[index - 1]
            if previous.timestamp < from {
                start = previous.timestamp
            }
            distance += Geodesy.distance(from: previous.coordinate, to: points[index].coordinate)
        }
        let duration = now - (start ?? from)
        return duration > 0 ? distance / duration : nil
    }

    /// Сколько секунд подряд (до `now`) датчики сообщают этот вид движения.
    private func continuousDuration(of activity: MotionActivity, now: Double) -> Double {
        guard let last = motion.last, last.activity == activity else { return 0 }
        var since = last.timestamp
        for sample in motion.reversed().dropFirst() {
            guard sample.activity == activity else { break }
            since = sample.timestamp
        }
        return now - since
    }

    /// Доля времени за окно, когда датчики сообщали этот вид движения.
    private func share(of activity: MotionActivity, overLast window: Double, now: Double) -> Double {
        let from = now - window
        var total = 0.0
        for (index, sample) in motion.enumerated() {
            let end = index + 1 < motion.count ? motion[index + 1].timestamp : now
            let overlap = min(end, now) - max(sample.timestamp, from)
            if sample.activity == activity, overlap > 0 {
                total += overlap
            }
        }
        return total / window
    }

    /// Скорость выросла на Δ за короткое время и достигла порога — так разгоняется машина, а не велосипед.
    private func isCarLaunch(_ rule: CarLaunchRule, now: Double) -> Bool {
        guard let current = instantSpeed(at: points.count - 1), current * 3.6 >= rule.reachingKilometersPerHour else {
            return false
        }
        for index in points.indices.dropLast().reversed() {
            guard now - points[index].timestamp <= rule.withinSeconds else { break }
            if let earlier = instantSpeed(at: index),
                (current - earlier) * 3.6 >= rule.deltaKilometersPerHour
            {
                return true
            }
        }
        return false
    }

    /// Мгновенная скорость: от GPS, а если её нет — по соседней точке.
    private func instantSpeed(at index: Int) -> Double? {
        guard points.indices.contains(index) else { return nil }
        if let speed = points[index].speed, speed >= 0 {
            return speed
        }
        guard index > 0 else { return nil }
        let dt = points[index].timestamp - points[index - 1].timestamp
        guard dt > 0 else { return nil }
        return Geodesy.distance(from: points[index - 1].coordinate, to: points[index].coordinate) / dt
    }

    /// Шаги: движение без шагов и нечеловеческая длина шага. Неизвестное число шагов (nil) ничего не решает.
    private func stepIssue(now: Double) -> TrackIssue? {
        if let window = rules.noStepsWindowSeconds,
            let steps = steps(overLast: window, now: now),
            steps == 0,
            let speed = averageSpeed(overLast: window, now: now),
            speed >= rules.noStepsMinSpeed
        {
            return .noSteps
        }

        if let range = rules.strideMeters,
            let steps = steps(overLast: rules.strideWindowSeconds, now: now),
            steps > 0,
            let speed = averageSpeed(overLast: rules.strideWindowSeconds, now: now)
        {
            let stride = speed * rules.strideWindowSeconds / Double(steps)
            if !range.contains(stride) {
                return .strideOutOfRange
            }
        }

        return nil
    }

    /// Шаги за окно, если шагомер покрыл его целиком и знает число шагов; иначе nil.
    private func steps(overLast window: Double, now: Double) -> Int? {
        let from = now - window
        let covering = pedometer.filter { $0.end > from && $0.start < now }
        guard let earliest = covering.map(\.start).min(), earliest <= from,
            let latest = covering.map(\.end).max(), latest >= now - 1
        else { return nil }
        var total = 0
        for sample in covering {
            guard let steps = sample.steps else { return nil }
            let length = sample.end - sample.start
            let overlap = min(sample.end, now) - max(sample.start, from)
            total += length > 0 ? Int((Double(steps) * overlap / length).rounded()) : steps
        }
        return total
    }

    // MARK: - История

    /// Разрыв: история начинается заново с этой точки.
    private mutating func breakSegment(at point: TrackPoint, _ issue: TrackIssue) -> JudgeVerdict {
        points = [point]
        return .segmentBroken(issue)
    }

    private mutating func prune(now: Double) {
        let keepPoints = (rules.speedLimits.map(\.windowSeconds).max() ?? 300) + 10
        if let index = points.firstIndex(where: { now - $0.timestamp <= keepPoints }), index > 1 {
            points.removeFirst(index - 1)
        }
        let keepSensors = 130.0
        if let index = motion.firstIndex(where: { now - $0.timestamp <= keepSensors }), index > 1 {
            motion.removeFirst(index - 1)
        }
        pedometer.removeAll { now - $0.end > keepSensors }
    }
}
