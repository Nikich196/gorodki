import GameCore

/// «Мои данные» (`GET /me/export`, docs/architecture/auth.md#мои-данные) — только то, что нужно разбору: забеги с точками
/// и датчиками и итоги заявок. Остальные разделы файла пропускаются. Точки — в том виде, в каком их прислал телефон
/// и хранит сервер (14 дней): время — мс Unix, точность — метры.
public struct MyData: Codable, Sendable, Equatable {
    public var runs: [Run]
    /// Заявки петель и их итог на сервере — для сверки; номеров точек в выгрузке у них нет.
    public var captures: [Capture]?

    public init(runs: [Run], captures: [Capture]? = nil) {
        self.runs = runs
        self.captures = captures
    }

    public struct Run: Codable, Sendable, Equatable {
        public var id: String
        public var league: League
        public var startedAtMs: Int64
        /// Без разрешения «Движение» сервер отклоняет все заявки забега (`motion_not_authorized`).
        public var motionAuthorized: Bool
        public var points: [Point]
        public var motion: [Motion]
        public var steps: [Steps]

        public init(
            id: String, league: League, startedAtMs: Int64, motionAuthorized: Bool = true, points: [Point],
            motion: [Motion] = [], steps: [Steps] = []
        ) {
            self.id = id
            self.league = league
            self.startedAtMs = startedAtMs
            self.motionAuthorized = motionAuthorized
            self.points = points
            self.motion = motion
            self.steps = steps
        }
    }

    /// Точка следа (`TrackPointDto` сервера).
    public struct Point: Codable, Sendable, Equatable {
        public var seq: Int
        /// Время по часам телефона, мс Unix.
        public var t: Int64
        public var lat: Double
        public var lon: Double
        /// Горизонтальная точность, метры.
        public var acc: Double
        /// Скорость, м/с; `nil` — телефон её не знал.
        public var speed: Double?
        public var flags: Int

        public init(seq: Int, t: Int64, lat: Double, lon: Double, acc: Double, speed: Double? = nil, flags: Int = 0) {
            self.seq = seq
            self.t = t
            self.lat = lat
            self.lon = lon
            self.acc = acc
            self.speed = speed
            self.flags = flags
        }

        /// Точка для судьи и детектора — в точности хранения, как её судит телефон.
        var trackPoint: TrackPoint {
            TrackPoint(
                seq: seq, coordinate: Coordinate(latitude: lat, longitude: lon), timestamp: Double(t) / 1_000,
                horizontalAccuracy: acc, speed: speed
            )
            .quantizedForStorage()
        }
    }

    /// Вид движения по CoreMotion с момента `t` (мс Unix).
    public struct Motion: Codable, Sendable, Equatable, Hashable {
        public var t: Int64
        public var activity: MotionActivity

        public init(t: Int64, activity: MotionActivity) {
            self.t = t
            self.activity = activity
        }
    }

    /// Шаги за интервал `start…end` (мс Unix); `steps == nil` — шагомер не знает.
    public struct Steps: Codable, Sendable, Equatable, Hashable {
        public var start: Int64
        public var end: Int64
        public var steps: Int?

        public init(start: Int64, end: Int64, steps: Int?) {
            self.start = start
            self.end = end
            self.steps = steps
        }
    }

    /// Итог заявки на сервере (`ExportCapture`).
    public struct Capture: Codable, Sendable, Equatable {
        public var runId: String
        public var status: String
        public var rejectCode: String?
        public var areaSquareMeters: Double?

        public init(runId: String, status: String, rejectCode: String? = nil, areaSquareMeters: Double? = nil) {
            self.runId = runId
            self.status = status
            self.rejectCode = rejectCode
            self.areaSquareMeters = areaSquareMeters
        }
    }
}
