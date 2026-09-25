/// Пороги площади, по которым сервер судит контур петли (`capture.shape` игрового конфига,
/// contracts/game-config.v1.json). Телефон по ним ставит флаги подсказки «до замыкания» — заранее, до заявки.
public struct CaptureAreaLimits: Hashable, Codable, Sendable {
    /// Меньше — сервер откажет (`too_small`), м². Детектор при этом заявит петлю: его порог ниже
    /// (`LoopDetectorSettings.minEstimatedAreaSquareMeters`).
    public var minAreaSquareMeters: Double = 2_500
    /// Больше — не засчитывается (`too_large`; PLAN.md, §3.2: «Кольцо >3,5 км²… игроку объясняется причина»), м².
    public var maxAreaSquareMeters: Double = 3_500_000

    public init() {}

    public init(minAreaSquareMeters: Double, maxAreaSquareMeters: Double) {
        self.minAreaSquareMeters = minAreaSquareMeters
        self.maxAreaSquareMeters = maxAreaSquareMeters
    }
}

/// Подсказка «до замыкания» (PLAN.md, §6.7; docs/architecture/run-hud.md). Цель — **начало открытой петли** (вариант A,
/// решение 25.09): точка, с которой детектор начнёт петлю, если человек к ней вернётся. Считается по тем же точкам,
/// плоскости и формулам, что и сам детектор, поэтому подсказка и детектор не расходятся.
public enum ClosureHint: Hashable, Sendable {
    /// Принятых точек ещё нет: начало забега, разрыв без новых точек, перезапуск приложения.
    case noTrail
    /// Путь от начала петли короче `minPathMeters` — сразу после старта, заявки или разрыва следа.
    case needsPath(targetSeq: Int, remainingMeters: Double)
    /// Путь достаточный, но петля к началу почти без площади (прямая, «туда-обратно»): стрелку назад не показываем.
    case needsTurn(targetSeq: Int)
    /// Можно замкнуть: расстояние, азимут и оценка площади.
    case canClose(ClosureTarget)

    /// Номер точки-цели; сменился — цель новая (заявка, разрыв, перезапуск). `nil` — следа нет.
    public var targetSeq: Int? {
        switch self {
        case .noTrail: nil
        case .needsPath(let seq, _), .needsTurn(let seq): seq
        case .canClose(let target): target.seq
        }
    }
}

/// Цель «можно замкнуть».
public struct ClosureTarget: Hashable, Sendable {
    /// Номер точки — начала петли.
    public var seq: Int
    /// Где цель — для точки на карте.
    public var coordinate: Coordinate
    /// Сколько осталось по прямой: расстояние до начала минус R детектора, м. В ноль приходит ровно тогда, когда детектор
    /// замыкает петлю на начале; пересечь свой след в другом месте — тоже замыкание (другой петли), его подсказка
    /// не предсказывает.
    public var distanceMeters: Double
    /// Азимут на цель: градусы от севера по часовой стрелке, [0; 360). Относительно чего рисовать стрелку — решает экран.
    public var bearingDegrees: Double
    /// Оценка площади, если замкнуть сейчас (площадь по следу + хорда), м² — для «≈+1,2 га».
    public var estimatedAreaSquareMeters: Double
    /// Оценка меньше порога сервера: петлю детектор заявит, но сервер, скорее всего, откажет (`too_small`).
    public var belowServerMinimum: Bool
    /// Оценка больше порога сервера (3,5 км²): петля не засчитается (`too_large`) — сказать до замыкания.
    public var tooLarge: Bool

    public init(
        seq: Int, coordinate: Coordinate, distanceMeters: Double, bearingDegrees: Double,
        estimatedAreaSquareMeters: Double, belowServerMinimum: Bool, tooLarge: Bool
    ) {
        self.seq = seq
        self.coordinate = coordinate
        self.distanceMeters = distanceMeters
        self.bearingDegrees = bearingDegrees
        self.estimatedAreaSquareMeters = estimatedAreaSquareMeters
        self.belowServerMinimum = belowServerMinimum
        self.tooLarge = tooLarge
    }
}
