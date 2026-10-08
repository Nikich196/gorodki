import Foundation

/// Видео-повтор забега (пункты 8 и 9 листика): след, вписанный в кадр, и сколько его нарисовано к каждому кадру.
/// Здесь только геометрия — рисует и пишет MP4 приложение (`ReplayVideoWriter`), поэтому кадры проверяются на Linux.
///
/// След проецируется на плоскость «восток — север» (метры), вписывается в кадр с полями, север — вверх. Голова следа
/// идёт с постоянной скоростью по длине пути, а не по времени точек: паузы на светофоре не превращают ролик в стоп-кадр.
public struct TrailReplay: Sendable {
    /// Точка кадра в пикселях: `x` — вправо, `y` — вниз.
    public struct Point: Hashable, Sendable {
        public var x: Double
        public var y: Double

        public init(x: Double, y: Double) {
            self.x = x
            self.y = y
        }
    }

    /// Что нарисовать в кадре.
    public struct Frame: Equatable, Sendable {
        /// Пройденная часть следа от начала до головы; голова — последняя точка.
        public var drawn: [Point]
        public var head: Point
        /// Доля пути 0…1.
        public var progress: Double
        /// Пройдено к этому кадру, м.
        public var meters: Double
    }

    /// Весь след в пикселях кадра (точки подряд с одинаковым положением убраны).
    public let path: [Point]
    /// Длина пути до каждой точки `path`, м.
    public let distances: [Double]
    public let width: Double
    public let height: Double
    public let frameCount: Int

    /// Длина следа, м.
    public var totalMeters: Double { distances.last ?? 0 }

    /// - Parameters:
    ///   - coordinates: точки следа по порядку; недопустимые (`Coordinate.isValid == false`) пропускаются.
    ///   - width, height: размер кадра, пиксели.
    ///   - padding: поля со всех сторон, пиксели.
    ///   - frameCount: сколько кадров в ролике (не меньше одного).
    public init(coordinates: [Coordinate], width: Double, height: Double, padding: Double, frameCount: Int) {
        self.width = width
        self.height = height
        self.frameCount = max(1, frameCount)
        let valid = coordinates.filter(\.isValid)
        guard let origin = valid.first else {
            path = []
            distances = []
            return
        }
        let plane = LocalTangentPlane(origin: origin)
        var planar: [PlanarPoint] = []
        for point in valid.map(plane.project) where point != planar.last {
            planar.append(point)
        }
        let minEast = planar.map(\.east).min() ?? 0
        let maxEast = planar.map(\.east).max() ?? 0
        let minNorth = planar.map(\.north).min() ?? 0
        let maxNorth = planar.map(\.north).max() ?? 0
        let spanEast = maxEast - minEast
        let spanNorth = maxNorth - minNorth
        let usableWidth = max(width - 2 * padding, 1)
        let usableHeight = max(height - 2 * padding, 1)
        // Один масштаб по обеим осям — форма петли не искажается; по узкой стороне след по центру.
        let scale: Double
        if spanEast == 0 && spanNorth == 0 {
            scale = 1
        } else {
            scale = min(
                spanEast > 0 ? usableWidth / spanEast : .infinity, spanNorth > 0 ? usableHeight / spanNorth : .infinity)
        }
        let offsetX = (width - spanEast * scale) / 2
        let offsetY = (height - spanNorth * scale) / 2
        path = planar.map { point in
            Point(x: offsetX + (point.east - minEast) * scale, y: height - offsetY - (point.north - minNorth) * scale)
        }
        var cumulative: [Double] = [0]
        for index in planar.indices.dropFirst() {
            let a = planar[index - 1]
            let b = planar[index]
            cumulative.append(cumulative[index - 1] + hypot(b.east - a.east, b.north - a.north))
        }
        distances = cumulative
    }

    /// Кадр `index` (0…`frameCount − 1`; вне пределов — ближайший). Первый кадр — голова в начале, последний — весь след.
    /// Пустой след — пустой кадр с головой в центре.
    public func frame(_ index: Int) -> Frame {
        let clamped = min(max(index, 0), frameCount - 1)
        let progress = frameCount > 1 ? Double(clamped) / Double(frameCount - 1) : 1
        guard let first = path.first else {
            return Frame(drawn: [], head: Point(x: width / 2, y: height / 2), progress: progress, meters: 0)
        }
        let total = totalMeters
        guard total > 0 else {
            return Frame(drawn: [first], head: first, progress: progress, meters: 0)
        }
        let target = progress * total
        // Первая точка, до которой пройдено не меньше цели; голова — между ней и предыдущей.
        let after = distances.firstIndex { $0 >= target } ?? (path.count - 1)
        guard after > 0 else {
            return Frame(drawn: [first], head: first, progress: progress, meters: 0)
        }
        let a = path[after - 1]
        let b = path[after]
        let segment = distances[after] - distances[after - 1]
        let t = segment > 0 ? (target - distances[after - 1]) / segment : 1
        let head = Point(x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t)
        var drawn = Array(path[..<after])
        if head != drawn.last {
            drawn.append(head)
        }
        return Frame(drawn: drawn, head: head, progress: progress, meters: target)
    }

    /// Образец следа, когда своих забегов ещё нет (режим фикстур, первый запуск): замкнутая волнистая петля около
    /// 1,5 км вокруг `center`. Это форма для ролика, а не настоящий маршрут по улицам.
    public static func sampleLoop(around center: Coordinate, pointCount: Int = 160) -> [Coordinate] {
        let plane = LocalTangentPlane(origin: center)
        let count = max(pointCount, 3)
        return (0...count).map { step in
            let angle = Double(step) / Double(count) * 2 * .pi
            let radius = 220 * (1 + 0.18 * sin(3 * angle) + 0.08 * cos(5 * angle))
            return plane.unproject(PlanarPoint(east: radius * cos(angle), north: radius * sin(angle) * 0.8))
        }
    }
}
