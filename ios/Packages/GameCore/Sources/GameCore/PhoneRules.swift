/// Числа правил, которые нужны телефону во время забега, — часть игрового конфига: раздел `rules` ответа `GET /config`
/// (contracts/game-config.v1.json). Остальные разделы (земля, приватность) телефону не нужны: их применяет сервер.
///
/// Забег судится той версией конфига, с которой начат, — и на телефоне, и на сервере; поэтому версия и числа едут
/// вместе (`LocalRun.configVersion`).
public struct PhoneRules: Hashable, Sendable, Decodable {
    public var loopDetector: LoopDetectorSettings
    /// Предел длины забега, часы (`capture.maxRunHours`).
    public var maxRunHours: Double
    /// Порог точности для новичка (ещё ни одного засчитанного захвата), м — вместо порога лиги
    /// (`capture.newcomerMaxAccuracyMeters`, docs/architecture/runs.md).
    public var newcomerMaxAccuracyMeters: Double
    public var run: LeagueRules
    public var bike: LeagueRules
    public var exploration: ExplorationSettings

    public init(
        loopDetector: LoopDetectorSettings = LoopDetectorSettings(), maxRunHours: Double = 4,
        newcomerMaxAccuracyMeters: Double = 35, run: LeagueRules = .run, bike: LeagueRules = .bike,
        exploration: ExplorationSettings = ExplorationSettings()
    ) {
        self.loopDetector = loopDetector
        self.maxRunHours = maxRunHours
        self.newcomerMaxAccuracyMeters = newcomerMaxAccuracyMeters
        self.run = run
        self.bike = bike
        self.exploration = exploration
    }

    /// Версия 1 — числа, с которыми собрано приложение (сверены с contracts/game-config.v1.json тестом): пока телефон
    /// ни разу не получил конфиг, забег начинается с ними.
    public static let version1 = PhoneRules()

    /// Правила судьи для забега: лиги — и порог точности новичка, если игрок ещё ничего не захватил.
    public func rules(for league: League, newcomer: Bool = false) -> LeagueRules {
        var rules =
            switch league {
            case .run: run
            case .bike: bike
            }
        if newcomer {
            rules.maxAccuracyMeters = newcomerMaxAccuracyMeters
        }
        return rules
    }

    private enum Keys: String, CodingKey {
        case capture, leagues, exploration
    }

    private enum CaptureKeys: String, CodingKey {
        case loopDetector, maxRunHours, newcomerMaxAccuracyMeters
    }

    private enum LeagueKeys: String, CodingKey {
        case run, bike
    }

    /// Из раздела `rules` конфига: лишние разделы и поля пропускаются.
    public init(from decoder: any Decoder) throws {
        let root = try decoder.container(keyedBy: Keys.self)
        let capture = try root.nestedContainer(keyedBy: CaptureKeys.self, forKey: .capture)
        let leagues = try root.nestedContainer(keyedBy: LeagueKeys.self, forKey: .leagues)
        self.init(
            loopDetector: try capture.decode(LoopDetectorSettings.self, forKey: .loopDetector),
            maxRunHours: try capture.decode(Double.self, forKey: .maxRunHours),
            newcomerMaxAccuracyMeters: try capture.decode(Double.self, forKey: .newcomerMaxAccuracyMeters),
            run: try leagues.decode(LeagueRules.self, forKey: .run),
            bike: try leagues.decode(LeagueRules.self, forKey: .bike),
            exploration: try root.decode(ExplorationSettings.self, forKey: .exploration))
    }
}
