/// Заявка петли: сервер построит контур из точек с номерами `startSeq…endSeq` (PLAN.md, §3.2).
public struct LoopClaim: Hashable, Codable, Sendable {
    public var startSeq: Int
    public var endSeq: Int
    /// Как замкнулась петля: след пересёк сам себя или вернулся ближе R к прежней точке.
    public var closure: LoopClosure
    /// Грубая площадь на телефоне, м² (для подсказки «≈+1,2 га»; точную считает сервер).
    public var estimatedArea: Double
}

public enum LoopClosure: String, Codable, Sendable {
    case crossing
    case proximity
}

/// Числа детектора петли. Хранятся в игровом конфиге.
public struct LoopDetectorSettings: Hashable, Codable, Sendable {
    /// Минимальный путь от начала петли до её замыкания, метры.
    public var minPathMeters: Double = 150
    /// R = clamp(1,62·√(accᵢ² + accₙ²), minRadius, maxRadius).
    public var radiusFactor: Double = 1.62
    public var minRadiusMeters: Double = 20
    public var maxRadiusMeters: Double = 50
    /// Заявку с меньшей грубой площадью не отправляем: сервер всё равно откажет (его A_min — 2 500 м²).
    public var minEstimatedAreaSquareMeters: Double = 1_000

    public init() {}
}

/// Детектор петли на телефоне (PLAN.md, §3.2): следит за отрезком и сообщает, когда след замкнулся.
///
/// Петля замыкается, если новый кусок следа пересёк прежний след или подошёл к прежней точке ближе R,
/// а путь между этими местами не короче 150 м. После заявки новая петля может начаться только дальше:
/// повторный круг вокруг квартала — это новая заявка (визит), а не та же самая.
/// Поиск соседей — через сетку ячеек 50 м, поэтому даже 4-часовой забег проверяется быстро.
public struct LoopDetector: Sendable {
    public let settings: LoopDetectorSettings

    private var plane: LocalTangentPlane?
    private var points: [PlanarPoint] = []
    private var seqs: [Int] = []
    private var accuracies: [Double] = []
    /// Путь от начала отрезка до точки, метры.
    private var pathLength: [Double] = []
    /// Префиксные суммы формулы шнурования: площадь любой петли start…index считается за O(1).
    private var shoelace: [Double] = []
    /// Точки раньше этого индекса не могут начинать петлю (уже заявлены).
    private var firstEligible = 0
    private var grid: [GridCell: [Int]] = [:]

    private static let cellSize = 50.0

    public init(settings: LoopDetectorSettings = LoopDetectorSettings()) {
        self.settings = settings
    }

    /// Разрыв отрезка: всё начинается заново.
    public mutating func reset() {
        self = LoopDetector(settings: settings)
    }

    /// Добавляет принятую точку. Возвращает заявку, если петля замкнулась.
    public mutating func add(_ point: TrackPoint) -> LoopClaim? {
        let plane = self.plane ?? LocalTangentPlane(origin: point.coordinate)
        self.plane = plane
        let p = plane.project(point.coordinate)
        let index = points.count
        let step = index > 0 ? distance(points[index - 1], p) : 0

        points.append(p)
        seqs.append(point.seq)
        accuracies.append(point.horizontalAccuracy)
        pathLength.append((pathLength.last ?? 0) + step)
        let term = index > 0 ? points[index - 1].east * p.north - p.east * points[index - 1].north : 0
        shoelace.append((shoelace.last ?? 0) + term)

        let claim = index > 0 ? findClosure(at: index) : nil
        grid[cell(of: p), default: []].append(index)
        if let claim {
            firstEligible = index
            return claim
        }
        return nil
    }

    // MARK: - Поиск замыкания

    /// Кандидаты идут по возрастанию, поэтому первый подходящий — самое раннее начало, то есть самая большая петля.
    private func findClosure(at index: Int) -> LoopClaim? {
        let current = points[index]
        let previous = points[index - 1]

        for start in nearbyIndices(of: current, and: previous) where start >= firstEligible && start < index - 1 {
            guard pathLength[index] - pathLength[start] >= settings.minPathMeters else { continue }

            // Пересечение: новый отрезок (index-1 → index) пересёк прежний (start → start+1).
            let crossed =
                start + 1 < index - 1
                && segmentsIntersect(previous, current, points[start], points[start + 1])
            // Сближение: вернулись к прежней точке ближе R.
            let returned = !crossed && distance(points[start], current) <= radius(start, index)
            guard crossed || returned else { continue }

            let area = loopArea(from: start, to: index)
            guard area >= settings.minEstimatedAreaSquareMeters else { continue }
            return LoopClaim(
                startSeq: seqs[start],
                endSeq: seqs[index],
                closure: crossed ? .crossing : .proximity,
                estimatedArea: area
            )
        }

        return nil
    }

    /// Площадь многоугольника из точек start…index с замыкающей хордой: сумма членов шнурования + хорда.
    private func loopArea(from start: Int, to end: Int) -> Double {
        let chord = points[end].east * points[start].north - points[start].east * points[end].north
        return abs(shoelace[end] - shoelace[start] + chord) / 2
    }

    private func radius(_ i: Int, _ n: Int) -> Double {
        let r = settings.radiusFactor * (accuracies[i] * accuracies[i] + accuracies[n] * accuracies[n]).squareRoot()
        return min(max(r, settings.minRadiusMeters), settings.maxRadiusMeters)
    }

    // MARK: - Сетка соседей

    private struct GridCell: Hashable, Sendable {
        var x: Int
        var y: Int
    }

    private func cell(of p: PlanarPoint) -> GridCell {
        GridCell(x: Int((p.east / Self.cellSize).rounded(.down)), y: Int((p.north / Self.cellSize).rounded(.down)))
    }

    /// Точки в ячейках вокруг текущей и предыдущей (радиус R ≤ 50 м — это соседние ячейки).
    private func nearbyIndices(of a: PlanarPoint, and b: PlanarPoint) -> [Int] {
        var result = Set<Int>()
        for center in [cell(of: a), cell(of: b)] {
            for dx in -1...1 {
                for dy in -1...1 {
                    result.formUnion(grid[GridCell(x: center.x + dx, y: center.y + dy)] ?? [])
                }
            }
        }
        return result.sorted()
    }
}

// MARK: - Геометрия на плоскости

private func distance(_ a: PlanarPoint, _ b: PlanarPoint) -> Double {
    let dx = b.east - a.east
    let dy = b.north - a.north
    return (dx * dx + dy * dy).squareRoot()
}

/// Пересекаются ли отрезки AB и CD (включая касание).
func segmentsIntersect(_ a: PlanarPoint, _ b: PlanarPoint, _ c: PlanarPoint, _ d: PlanarPoint) -> Bool {
    func orientation(_ p: PlanarPoint, _ q: PlanarPoint, _ r: PlanarPoint) -> Double {
        (q.east - p.east) * (r.north - p.north) - (q.north - p.north) * (r.east - p.east)
    }
    func onSegment(_ p: PlanarPoint, _ q: PlanarPoint, _ r: PlanarPoint) -> Bool {
        min(p.east, r.east) <= q.east && q.east <= max(p.east, r.east)
            && min(p.north, r.north) <= q.north && q.north <= max(p.north, r.north)
    }
    let d1 = orientation(c, d, a)
    let d2 = orientation(c, d, b)
    let d3 = orientation(a, b, c)
    let d4 = orientation(a, b, d)
    if ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)) {
        return true
    }
    return (d1 == 0 && onSegment(c, a, d)) || (d2 == 0 && onSegment(c, b, d))
        || (d3 == 0 && onSegment(a, c, b)) || (d4 == 0 && onSegment(a, d, b))
}
