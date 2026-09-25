import Foundation
import GameCore
import Networking
import Sync

/// Синтетический забег в Бресте для сквозного прогона: квадрат 100 × 100 м пешком, хвост, затем «машина».
///
/// Начало — та же точка, что у `TrackSimulator` и `RunSessionTests.Walk` (52.0976, 23.6880), без реальных маршрутов
/// и домов. В UTM 34N это E 684 114,6 / N 5 775 302,6: тайл земли 1 км — `684:5775`, квадрат лежит в 110–215 м от
/// края тайла по востоку и в 302–406 м по северу, «машина» (до x ≈ 600 м) из тайла не выходит. Тайл тумана z14 —
/// `9270:5404`. Константы нужны только для проверки «карта чистая» и сверки с ответом сервера.
struct SyntheticRun {
    static let origin = Coordinate(latitude: 52.0976, longitude: 23.6880)
    static let territoryTile = TileKey(x: 684, y: 5775)
    static let plane = LocalTangentPlane(origin: origin)

    /// Часть 1 — квадрат по 72 точки на сторону на 1,4 м/с, возврат в начало и 20 с на юг до (0, −28).
    /// Петля замыкается на западной стороне сближением.
    static let walk: [PlanarPoint] = {
        var points: [PlanarPoint] = []
        let corners: [(Double, Double)] = [(0, 0), (100, 0), (100, 100), (0, 100), (0, 0)]
        for (a, b) in zip(corners, corners.dropFirst()) {
            for i in 0..<72 {
                let t = Double(i) / 72
                points.append(PlanarPoint(east: a.0 + (b.0 - a.0) * t, north: a.1 + (b.1 - a.1) * t))
            }
        }
        points.append(PlanarPoint(east: 0, north: 0))
        for i in 1...20 { points.append(PlanarPoint(east: 0, north: -1.4 * Double(i))) }
        return points
    }()

    /// Часть 2 — «машина»: 40 с на восток на 15 м/с без шагов. Судья рвёт след; туман у разрыва не открывается
    /// (правило «30 с до разрыва») — это сверяется с сервером бит в бит.
    static let drive: [PlanarPoint] = (1...40).map { PlanarPoint(east: 15 * Double($0), north: -28) }

    /// Весь забег, секунды: точка раз в секунду и «Финиш» через секунду после последней.
    static var duration: Double { Double(walk.count + drive.count + 1) }

    /// Начало забега. Точки лежат в прошлом, иначе сервер счёл бы их «из будущего» (`time_future`, допуск 2 мин).
    /// Запас — одна минута, не больше: обработчик визитов берёт забег, когда его конец старше публичного горизонта
    /// (20 мин), и поднимает версию тайла. Поэтому T0 + длительность + 20 мин должно быть позже конца теста (срок
    /// теста — 10 мин), иначе проверка «повтор ничего не изменил» спутала бы визит с дублем.
    let t0: Double

    init(now: Double = Date().timeIntervalSince1970) {
        t0 = now.rounded(.down) - (Self.duration + 60)
    }

    var startedAtMs: Int64 { Int64(t0 * 1_000) }
    var finishAt: Double { t0 + Self.duration }

    /// Входы трекера для части пути, начиная с точки номер `offset` (время `t0 + 1 + offset`); точность 5 м,
    /// `source` пустой — как у настоящего телефона.
    /// Датчики — без запаздывания, сознательно: сервер (`TrackJudging.JudgeAll`) кормит судью записями, закончившимися
    /// не позже точки, а телефон видит их, только если они пришли раньше точки. С настоящим запаздыванием CoreMotion
    /// вердикты могут расходиться; здесь это убрано, чтобы сверить туман бит в бит.
    func inputs(_ points: [PlanarPoint], offset: Int, speed: Double, stepsPerFiveSeconds: Int) -> [TrackerInput] {
        var inputs: [TrackerInput] = []
        if offset == 0 {
            inputs.append(.motion(MotionSample(timestamp: t0 + 0.5, activity: .walking)))
        }
        for (i, point) in points.enumerated() {
            let t = t0 + 1 + Double(offset + i)
            let everyFive = Int(t - t0) % 5 == 0
            if everyFive {
                inputs.append(.steps(PedometerSample(start: t - 5, end: t, steps: stepsPerFiveSeconds)))
            }
            let fix = LocationFix(
                coordinate: Self.plane.unproject(point), timestamp: t, horizontalAccuracy: 5, speed: speed, source: [])
            inputs.append(.fix(fix, receivedAt: t + 1))
            if everyFive {
                inputs.append(.tick(now: t))
            }
        }
        return inputs
    }
}
