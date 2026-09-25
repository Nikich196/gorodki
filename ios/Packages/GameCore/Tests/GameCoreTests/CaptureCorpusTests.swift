import Foundation
import Testing

@testable import GameCore

/// Корпус петель. PLAN §13: «GameCore: … корпус (квадраты с шумом, восьмёрка, хорда через след, «туда-обратно»,
/// велосипед в «Беге», машина в «Вело», прыжок GPS, стоянка)». Исходов §13 не называет, поэтому у каждого теста
/// в названии источник ожидания:
/// - «PLAN §3.2» — выведено из триггера телефона: «Отрезок пересёк свой след **или** подошёл к прежней точке ближе R…
///   Путь между этими местами ≥150 м»;
/// - «задача D» — ожидание из задачи пакета D, в PLAN его нет;
/// - «записано» — PLAN молчит: тест держит нынешнее поведение и упадёт, если оно изменится (вопросы Никите —
///   docs/architecture/loop-detector.md).
/// «Туда-обратно» уже в `LoopDetectorTests`, велосипед, машина и прыжок GPS — в `SegmentJudgeTests`.
///
/// Шум — с фиксированным зерном: прогон повторяется бит в бит. Модели шума — параметры теста, а не числа игры.
@Suite("Корпус петель: шумный квадрат, восьмёрка, хорда через след, стоянка (PLAN §13)")
struct CaptureCorpusTests {
    static let seeds: [UInt64] = Array(1...8)

    static let square: [(Double, Double)] = [(0, 0), (100, 0), (100, 100), (0, 100), (0, 0)]
    static let eightFromCrossing: [(Double, Double)] = [
        (0, 0), (100, 100), (200, 0), (100, -100), (0, 0), (-100, 100), (-200, 0), (-100, -100), (0, 0),
    ]
    static let eightFromFarEnd: [(Double, Double)] = [
        (200, 0), (100, -100), (0, 0), (-100, 100), (-200, 0), (-100, -100), (0, 0), (100, 100), (200, 0),
    ]

    // MARK: - Квадрат

    /// Настоящий GPS на 1 Гц коррелирован (AR(1)); независимый шум берём только малый — при σ от 3 м судья уже
    /// видит «слишком быстро» (находка F3 в loop-detector.md).
    @Test(
        "Квадрат 100 × 100 м с шумом GPS — одна заявка ≈ 1 га (PLAN §3.2)",
        arguments: [
            GPSNoise.correlated(rho: 0.9, sigma: 1.0, accuracy: 5),
            .correlated(rho: 0.95, sigma: 1.5, accuracy: 10),
            .independent(sigma: 2.0, accuracy: 5),
        ],
        seeds
    )
    func noisySquare(noise: GPSNoise, seed: UInt64) {
        var track = CorpusTrack(noise: noise, seed: seed)
        track.walk(Self.square)

        let result = ClaimPipeline.run(track.points, league: .run)

        #expect(result.claims.count == 1, "\(result)")
        #expect(result.claims.allSatisfy { (8_000...12_000).contains($0.estimatedArea) }, "\(result)")
        // Разрывы утверждаем только для коррелированного шума. При независимом σ = 2 м их на 200 зёрнах не было,
        // но запас мал: уже при σ = 3 м судья рвёт след «слишком быстро» на 28 % зёрен (F3).
        if case .correlated = noise {
            #expect(result.breaks.isEmpty, "\(result)")
        }
    }

    // MARK: - Восьмёрка

    @Test(
        "Восьмёрка от перекрёстка — две заявки, по лепестку (PLAN §3.2: каждое возвращение к перекрёстку)",
        arguments: seeds)
    func eightFromCrossing(seed: UInt64) throws {
        var track = CorpusTrack(noise: .correlated(rho: 0.9, sigma: 1.0, accuracy: 5), seed: seed)
        track.walk(Self.eightFromCrossing)

        let result = ClaimPipeline.run(track.points, league: .run)

        try #require(result.claims.count == 2, "\(result)")
        #expect(result.claims.allSatisfy { (18_000...22_000).contains($0.estimatedArea) }, "\(result)")
        #expect(result.claims[1].startSeq >= result.claims[0].endSeq, "\(result)")
    }

    /// ADR 0003 про кольцо сервера: «восьмёрка даёт оба лепестка». Телефон такого кольца не шлёт — он замыкает
    /// петлю раньше, а после заявки новая может начаться только дальше её конца. Первый лепесток теряется.
    @Test("Восьмёрка не от перекрёстка — заявлен один лепесток (записано, вопрос Q-D1)", arguments: seeds)
    func eightFromFarEnd(seed: UInt64) throws {
        var track = CorpusTrack(noise: .correlated(rho: 0.9, sigma: 1.0, accuracy: 5), seed: seed)
        track.walk(Self.eightFromFarEnd)

        let result = ClaimPipeline.run(track.points, league: .run)

        let claim = try #require(result.claims.first, "\(result)")
        #expect(result.claims.count == 1, "\(result)")
        #expect((18_000...22_000).contains(claim.estimatedArea), "\(result)")
        #expect(claim.startSeq > 0, "\(result)")
    }

    // MARK: - Хорда через след

    /// При 1 Гц и сплошных точках телефон всегда подходит к следу ближе R раньше, чем пересечёт его. Пересечение
    /// в настоящем пути бывает, когда GPS пропал: здесь на последнем отрезке нет точек с east ∈ (−30, 30).
    /// Толкование «хорды через след» — вопрос Q-D5.
    @Test(
        "Хорда через след: GPS пропал на ~60 м, отрезок пересёк прежний след — заявка «пересечение» (PLAN §3.2)",
        arguments: [ChordCase(league: .run, speed: 1.4), ChordCase(league: .bike, speed: 6)],
        seeds
    )
    func chordAcrossTrail(chord: ChordCase, seed: UInt64) throws {
        var track = CorpusTrack(noise: .correlated(rho: 0.9, sigma: 1.0, accuracy: 5), seed: seed)
        track.walk([(0, -40), (0, 100), (100, 100), (100, 0)], speed: chord.speed)
        track.walk([(100, 0), (-40, 0)], speed: chord.speed, continuing: true) { east, _ in
            (-30.0...30.0).contains(east)
        }

        let result = ClaimPipeline.run(track.points, league: chord.league)

        let claim = try #require(result.claims.first, "\(result)")
        #expect(result.claims.count == 1, "\(result)")
        #expect(claim.closure == .crossing, "\(result)")
        // Квадрат плюс клин у перекрёстка: на 200 зёрнах 9 000–11 079 м² (велосипед, шаг 6 м — клин больше).
        #expect((9_000...11_500).contains(claim.estimatedArea), "\(result)")
    }

    /// Обе половины квадрата уже заявлены; сервер засчитает проход визитом, а не новым захватом.
    @Test("Хорда через заявленную петлю (θ) — новой заявки нет (записано)", arguments: seeds)
    func thetaChord(seed: UInt64) throws {
        var track = CorpusTrack(noise: .correlated(rho: 0.9, sigma: 1.0, accuracy: 5), seed: seed)
        track.walk(Self.square)
        track.walk([(0, 0), (50, -40), (50, 140)], continuing: true)

        let result = ClaimPipeline.run(track.points, league: .run)

        let claim = try #require(result.claims.first, "\(result)")
        #expect(result.claims.count == 1, "\(result)")
        #expect((9_000...11_000).contains(claim.estimatedArea), "\(result)")
    }

    /// Нижнюю половину не обвели: западную сторону ниже y = 50 не проходили. Для сервера PLAN §3.2 говорит
    /// «P = union(Polygonize(Node(кольцо))) — всё, что обведено»; для телефона правила нет — поведение записано.
    @Test("Срез поперёк своего следа до замыкания — заявлена только обведённая часть (записано)", arguments: seeds)
    func cutAcrossTrail(seed: UInt64) throws {
        var track = CorpusTrack(noise: .correlated(rho: 0.9, sigma: 1.0, accuracy: 5), seed: seed)
        track.walk([(0, 0), (100, 0), (100, 100), (0, 100), (0, 50), (140, 50)])

        let result = ClaimPipeline.run(track.points, league: .run)

        let claim = try #require(result.claims.first, "\(result)")
        #expect(result.claims.count == 1, "\(result)")
        #expect((4_500...6_000).contains(claim.estimatedArea), "\(result)")
    }

    // MARK: - Стоянка

    @Test(
        "Стоянка 5 минут с дрожанием GPS — заявок нет (задача D)",
        arguments: [
            GPSNoise.correlated(rho: 0.9, sigma: 1.0, accuracy: 5),
            .correlated(rho: 0.95, sigma: 2.0, accuracy: 15),
            .correlated(rho: 0.98, sigma: 1.5, accuracy: 15),
        ],
        seeds
    )
    func standstill(noise: GPSNoise, seed: UInt64) {
        var track = CorpusTrack(noise: noise, seed: seed)
        track.stand(seconds: 300)

        let result = ClaimPipeline.run(track.points, league: .run)

        #expect(result.claims.isEmpty, "\(result)")
    }

    /// Порог телефона `minEstimatedAreaSquareMeters` (1 000 м²) ниже A_min сервера (2 500 м²), поэтому при сильном
    /// независимом шуме телефон шлёт заявки-«каракули». Сервер их отвергнет, но они тратят суточные лимиты.
    /// Когда Q-D2 решат, `withKnownIssue` упадёт сам («known issue was not recorded») — тогда обёртку снять.
    @Test("Стоянка при сильном независимом шуме (σ 12 м, точность 20 м) — известная проблема Q-D2")
    func standstillHeavyNoise() throws {
        let serverMinimum = try ServerShapeRules.load().minAreaSquareMeters
        var claims: [LoopClaim] = []
        for seed in Self.seeds {
            var track = CorpusTrack(noise: .independent(sigma: 12, accuracy: 20), seed: seed)
            track.stand(seconds: 300)
            claims += ClaimPipeline.run(track.points, league: .run).claims
        }

        withKnownIssue("Q-D2: телефон заявляет петли меньше A_min сервера на стоянке с плохим GPS") {
            #expect(claims.isEmpty, "заявок: \(claims.count)")
        }
        // Каракули не должны дотягивать до A_min: иначе сервер их примет и игрок получит землю за стоянку.
        #expect(claims.allSatisfy { $0.estimatedArea < serverMinimum }, "\(claims.map(\.estimatedArea))")
    }
}

// MARK: - Путь точки

/// Повторяет `RunSession.process`: округление для хранения → судья → детектор (разрыв сбрасывает детектор).
/// Меняется там — меняй здесь.
struct ClaimPipeline {
    struct Result: CustomStringConvertible {
        var claims: [LoopClaim] = []
        var breaks: [TrackIssue] = []

        var description: String {
            let loops = claims.map { "[\($0.startSeq)–\($0.endSeq) \($0.closure) \(Int($0.estimatedArea)) м²]" }
            return "заявки \(loops), разрывы \(breaks.map(\.rawValue))"
        }
    }

    private var judge: SegmentJudge
    private var detector: LoopDetector

    init(league: League, rules: PhoneRules = .version1) {
        judge = SegmentJudge(league: league, rules: rules.rules(for: league))
        detector = LoopDetector(settings: rules.loopDetector)
    }

    static func run(_ points: [TrackPoint], league: League) -> Result {
        var pipeline = ClaimPipeline(league: league)
        return pipeline.feed(points)
    }

    mutating func feed(_ points: [TrackPoint]) -> Result {
        var result = Result()
        for raw in points {
            let point = raw.quantizedForStorage()
            switch judge.judge(point, now: point.timestamp + 1) {
            case .accepted:
                break
            case .ignored:
                continue
            case .segmentBroken(let issue):
                result.breaks.append(issue)
                detector.reset()
            }
            if let claim = detector.add(point) {
                result.claims.append(claim)
            }
        }
        return result
    }
}

// MARK: - Шум и след

/// SplitMix64 (Vigna): простой генератор с зерном, одинаковый на всех платформах.
struct SeededGenerator: RandomNumberGenerator {
    private var state: UInt64

    init(seed: UInt64) {
        state = seed
    }

    mutating func next() -> UInt64 {
        state &+= 0x9E37_79B9_7F4A_7C15
        var z = state
        z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
        z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
        return z ^ (z >> 31)
    }

    /// Нормальное N(0, 1) по Боксу — Мюллеру.
    mutating func gaussian() -> Double {
        let u1 = max(Double.random(in: 0..<1, using: &self), 1e-300)
        let u2 = Double.random(in: 0..<1, using: &self)
        return (-2 * log(u1)).squareRoot() * cos(2 * .pi * u2)
    }
}

/// Модель ошибки GPS, метры по каждой оси.
enum GPSNoise: Sendable, CustomTestStringConvertible {
    /// Независимая ошибка в каждой точке.
    case independent(sigma: Double, accuracy: Double)
    /// AR(1) по каждой оси: s = ρ·s + σ·N(0, 1) — ошибка медленно «плывёт», как у настоящего приёмника.
    case correlated(rho: Double, sigma: Double, accuracy: Double)

    var accuracy: Double {
        switch self {
        case .independent(_, let accuracy), .correlated(_, _, let accuracy): accuracy
        }
    }

    var testDescription: String {
        switch self {
        case .independent(let sigma, let accuracy): "независимый σ \(sigma) м, точность \(accuracy) м"
        case .correlated(let rho, let sigma, let accuracy): "AR(1) ρ \(rho), σ \(sigma) м, точность \(accuracy) м"
        }
    }
}

struct ChordCase: Sendable, CustomTestStringConvertible {
    var league: League
    var speed: Double

    var testDescription: String { "\(league.rawValue), \(speed) м/с" }
}

/// След из точек раз в секунду поверх `TrackSimulator`, с шумом.
struct CorpusTrack {
    private var simulator = TrackSimulator()
    private var generator: SeededGenerator
    private var drift = (east: 0.0, north: 0.0)
    let noise: GPSNoise
    private(set) var points: [TrackPoint] = []

    init(noise: GPSNoise, seed: UInt64) {
        self.noise = noise
        generator = SeededGenerator(seed: seed)
    }

    private mutating func error() -> (east: Double, north: Double) {
        switch noise {
        case .independent(let sigma, _):
            return (sigma * generator.gaussian(), sigma * generator.gaussian())
        case .correlated(let rho, let sigma, _):
            drift = (rho * drift.east + sigma * generator.gaussian(), rho * drift.north + sigma * generator.gaussian())
            return drift
        }
    }

    /// Точка в (east, north) с ошибкой. Пропущенная (`dropped`) точка сдвигает только время: телефон нумерует
    /// лишь полученные точки, провал GPS номеров не тратит.
    private mutating func fix(east: Double, north: Double, dropped: Bool = false) {
        let (dx, dy) = error()
        if dropped {
            simulator.time += 1
        } else {
            points.append(simulator.next(east: east + dx, north: north + dy, accuracy: noise.accuracy))
        }
    }

    /// Обход по вершинам с постоянной скоростью. `continuing` — продолжение прошлого обхода: первая вершина
    /// уже записана. `skip(east, north)` — где GPS пропал (по истинному положению).
    mutating func walk(
        _ vertices: [(Double, Double)], speed: Double = 1.4, continuing: Bool = false,
        skip: (Double, Double) -> Bool = { _, _ in false }
    ) {
        for i in 0..<(vertices.count - 1) {
            let (x1, y1) = vertices[i]
            let (x2, y2) = vertices[i + 1]
            let length = ((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)).squareRoot()
            let steps = max(1, Int((length / speed).rounded(.up)))
            for s in 0..<steps where !(continuing && i == 0 && s == 0) {
                let t = Double(s) / Double(steps)
                let (east, north) = (x1 + (x2 - x1) * t, y1 + (y2 - y1) * t)
                fix(east: east, north: north, dropped: skip(east, north))
            }
        }
        if let (east, north) = vertices.last {
            fix(east: east, north: north, dropped: skip(east, north))
        }
    }

    /// Стоим на месте: точка раз в секунду, только ошибка GPS.
    mutating func stand(seconds: Int) {
        for _ in 0..<seconds {
            fix(east: 0, north: 0)
        }
    }
}

/// A_min сервера — из контракта, а не числом в тесте.
struct ServerShapeRules: Decodable {
    var minAreaSquareMeters: Double

    private struct File: Decodable {
        struct Capture: Decodable { var shape: ServerShapeRules }
        var capture: Capture
    }

    static func load() throws -> ServerShapeRules {
        let relative = "contracts/game-config.v1.json"
        var directory = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
        for _ in 0..<10 {
            let candidate = directory.appendingPathComponent(relative)
            if FileManager.default.fileExists(atPath: candidate.path) {
                return try JSONDecoder().decode(File.self, from: Data(contentsOf: candidate)).capture.shape
            }
            directory.deleteLastPathComponent()
        }
        throw CocoaError(.fileNoSuchFile, userInfo: [NSFilePathErrorKey: relative])
    }
}
