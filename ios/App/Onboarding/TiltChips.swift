import CoreMotion
import DesignSystem
import SwiftUI
import UIKit

/// Значки интро, которые пересыпаются от наклона телефона (Никита 15.10: «чтобы значки рассыпались, наклонил телефон —
/// они пересыпались»). Свои значки, не чужие логотипы: звёзды-крепости в цветах игроков, кроссовок, флажок, кубок,
/// огонь. Гравитация — от датчика положения (`CMMotionManager.deviceMotion`, разрешения «Движение и фитнес» не нужно);
/// без датчика (симулятор) — по ориентации устройства (`UIDevice.orientation`): повернул на бок — пересыпались вбок,
/// вверх ногами — к верху экрана, с кувырком при повороте (Никита 22.10: «кручу телефон, а шарики болтаются сами по
/// себе»). Каждый заметный удар — лёгкая вибрация. «Уменьшить движение» — значки лежат на полу неподвижно.
struct TiltChips: View {
    /// Где пол и потолок: значки живут между надписью и панелью.
    var top: CGFloat
    var bottom: CGFloat
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var world = ChipWorld()

    var body: some View {
        let theme = Theme(colorScheme)
        TimelineView(.animation(paused: reduceMotion)) { timeline in
            Canvas { context, size in
                let floor = max(top + 80, size.height - bottom)
                world.step(to: timeline.date, area: CGRect(x: 0, y: top, width: size.width, height: floor - top))
                for chip in world.chips {
                    var piece = context
                    piece.translateBy(x: chip.position.x, y: chip.position.y)
                    piece.rotate(by: .radians(chip.angle))
                    let rect = CGRect(x: -chip.radius, y: -chip.radius, width: chip.radius * 2, height: chip.radius * 2)
                    piece.addFilter(.shadow(color: .black.opacity(0.22), radius: 6, y: 3))
                    piece.fill(Path(ellipseIn: rect), with: .color(chip.style.fill(theme)))
                    piece.stroke(
                        Path(ellipseIn: rect.insetBy(dx: 1, dy: 1)), with: .color(.white.opacity(0.75)), lineWidth: 1.5)
                    if let symbol = piece.resolveSymbol(id: chip.style.id) {
                        piece.draw(symbol, at: .zero)
                    }
                }
            } symbols: {
                ForEach(ChipStyle.all) { style in
                    style.icon(theme)
                        .tag(style.id)
                }
            }
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
        .onAppear { world.startMotion(reduceMotion: reduceMotion) }
        .onDisappear { world.stopMotion() }
    }
}

/// Вид значка: заливка и значок.
struct ChipStyle: Identifiable {
    let id: Int
    let symbol: String?
    let player: PlayerColor?

    static let all: [ChipStyle] = [
        ChipStyle(id: 0, symbol: nil, player: .sky),
        ChipStyle(id: 1, symbol: "shoe.fill", player: nil),
        ChipStyle(id: 2, symbol: nil, player: .coral),
        ChipStyle(id: 3, symbol: "flag.checkered", player: nil),
        ChipStyle(id: 4, symbol: nil, player: .forest),
        ChipStyle(id: 5, symbol: "trophy.fill", player: nil),
        ChipStyle(id: 6, symbol: nil, player: .violet),
        ChipStyle(id: 7, symbol: "flame.fill", player: nil),
        ChipStyle(id: 8, symbol: nil, player: .sun),
        ChipStyle(id: 9, symbol: "bolt.fill", player: nil),
        ChipStyle(id: 10, symbol: "figure.run", player: nil),
        ChipStyle(id: 11, symbol: nil, player: .magenta),
    ]

    /// Звёзды — цвета игроков, значки — огонь бренда.
    func fill(_ theme: Theme) -> Color {
        if let player { return player.color }
        return id % 4 == 1 ? Brand.ember[theme].color : Brand.amber[theme].color
    }

    @ViewBuilder
    func icon(_ theme: Theme) -> some View {
        if let symbol {
            Image(systemName: symbol)
                .font(.system(size: 18, weight: .bold))
                .foregroundStyle(.white)
        } else {
            FortressStar()
                .fill(.white)
                .frame(width: 26, height: 26)
        }
    }
}

/// Мир значков: шаги физики (гравитация, стены, столкновения) и датчик положения.
@MainActor
final class ChipWorld {
    struct Chip {
        var position: CGPoint
        var velocity: CGVector
        var angle: Double
        var spin: Double
        let radius: CGFloat
        let style: ChipStyle
    }

    private(set) var chips: [Chip] = []
    private var last: Date?
    private var area: CGRect = .zero
    private let motion = CMMotionManager()
    private var gravity = CGVector(dx: 0, dy: 1)
    private var hasMotion = false
    /// Наблюдатель поворота устройства — когда датчика положения нет.
    private var orientationObserver: (any NSObjectProtocol)?
    private var lastBump = Date.distantPast
    private let bump = UIImpactFeedbackGenerator(style: .soft)

    func startMotion(reduceMotion: Bool) {
        guard !reduceMotion else { return }
        guard motion.isDeviceMotionAvailable else {
            followOrientation()
            return
        }
        guard !motion.isDeviceMotionActive else { return }
        hasMotion = true
        motion.deviceMotionUpdateInterval = 1.0 / 60
        motion.startDeviceMotionUpdates(to: .main) { [weak self] data, _ in
            guard let data else { return }
            MainActor.assumeIsolated {
                // Экран в портретной ориентации: x — вправо, y датчика — вверх, на экране — вниз.
                self?.gravity = CGVector(dx: data.gravity.x, dy: -data.gravity.y)
            }
        }
        bump.prepare()
    }

    func stopMotion() {
        motion.stopDeviceMotionUpdates()
        if let orientationObserver {
            NotificationCenter.default.removeObserver(orientationObserver)
            UIDevice.current.endGeneratingDeviceOrientationNotifications()
            self.orientationObserver = nil
        }
    }

    /// Без датчика: «низ» — по ориентации устройства. Экран только портретный, поэтому повёрнутое устройство меняет
    /// сторону, куда падают значки, а не сам экран.
    private func followOrientation() {
        guard orientationObserver == nil else { return }
        let device = UIDevice.current
        device.beginGeneratingDeviceOrientationNotifications()
        gravity = Self.gravity(for: device.orientation) ?? gravity
        orientationObserver = NotificationCenter.default.addObserver(
            forName: UIDevice.orientationDidChangeNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, let down = Self.gravity(for: UIDevice.current.orientation), down != self.gravity else {
                    return
                }
                self.gravity = down
                self.tumble()
            }
        }
    }

    /// Куда «низ» в координатах портретного экрана; лицом вверх или вниз — без изменений (`nil`).
    static func gravity(for orientation: UIDeviceOrientation) -> CGVector? {
        switch orientation {
        case .portrait: CGVector(dx: 0, dy: 1)
        case .portraitUpsideDown: CGVector(dx: 0, dy: -1)
        // landscapeLeft — кнопка «Домой» справа: верх устройства слева, вниз смотрит левый край экрана; landscapeRight —
        // наоборот.
        case .landscapeLeft: CGVector(dx: -1, dy: 0)
        case .landscapeRight: CGVector(dx: 1, dy: 0)
        default: nil
        }
    }

    /// Поворот — значки подбрасывает и закручивает, дальше их несёт новая «сила тяжести».
    private func tumble() {
        var generator = SeededRandom(seed: UInt64(chips.count) &+ UInt64(Date.now.timeIntervalSince1970))
        for index in chips.indices {
            chips[index].velocity.dx += (generator.next() - 0.5) * 520
            chips[index].velocity.dy += (generator.next() - 0.5) * 520
            chips[index].spin += (generator.next() - 0.5) * 8
        }
        bump.impactOccurred(intensity: 0.6)
    }

    func step(to date: Date, area newArea: CGRect) {
        if chips.isEmpty || area.size != newArea.size {
            area = newArea
            seed()
        }
        area = newArea
        let dt = min(1.0 / 30, max(0, date.timeIntervalSince(last ?? date)))
        last = date
        guard dt > 0 else { return }
        // С датчиком — его вектор (по длине — сколько наклонён), без него — ориентация устройства.
        let g = CGVector(dx: gravity.dx * 2200, dy: gravity.dy * 2200)
        var strongest: CGFloat = 0
        for index in chips.indices {
            var chip = chips[index]
            chip.velocity.dx += g.dx * dt
            chip.velocity.dy += g.dy * dt
            chip.velocity.dx *= 0.995
            chip.velocity.dy *= 0.995
            chip.position.x += chip.velocity.dx * dt
            chip.position.y += chip.velocity.dy * dt
            chip.angle += chip.spin * dt
            chip.spin *= 0.98
            strongest = max(strongest, bounceOffWalls(&chip))
            chips[index] = chip
        }
        for i in chips.indices {
            for j in chips.indices where j > i {
                strongest = max(strongest, collide(i, j))
            }
        }
        if hasMotion, strongest > 240, date.timeIntervalSince(lastBump) > 0.07 {
            lastBump = date
            bump.impactOccurred(intensity: min(1, strongest / 900))
        }
    }

    private func seed() {
        var generator = SeededRandom(seed: 7)
        chips = ChipStyle.all.enumerated().map { index, style in
            let radius = CGFloat(20 + (index * 7) % 9)
            return Chip(
                position: CGPoint(
                    x: area.minX + radius + CGFloat(generator.next()) * max(1, area.width - radius * 2),
                    y: area.minY + radius + CGFloat(generator.next()) * max(1, area.height * 0.6)),
                velocity: CGVector(dx: (generator.next() - 0.5) * 120, dy: 0), angle: generator.next() * .pi,
                spin: (generator.next() - 0.5) * 2, radius: radius, style: style)
        }
    }

    /// Удар о стену — отскок с потерей энергии; возвращает силу удара.
    private func bounceOffWalls(_ chip: inout Chip) -> CGFloat {
        var hit: CGFloat = 0
        let restitution = 0.45
        if chip.position.x < area.minX + chip.radius {
            chip.position.x = area.minX + chip.radius
            hit = max(hit, abs(chip.velocity.dx))
            chip.velocity.dx = -chip.velocity.dx * restitution
            chip.spin += chip.velocity.dy * 0.01
        } else if chip.position.x > area.maxX - chip.radius {
            chip.position.x = area.maxX - chip.radius
            hit = max(hit, abs(chip.velocity.dx))
            chip.velocity.dx = -chip.velocity.dx * restitution
            chip.spin -= chip.velocity.dy * 0.01
        }
        if chip.position.y < area.minY + chip.radius {
            chip.position.y = area.minY + chip.radius
            hit = max(hit, abs(chip.velocity.dy))
            chip.velocity.dy = -chip.velocity.dy * restitution
        } else if chip.position.y > area.maxY - chip.radius {
            chip.position.y = area.maxY - chip.radius
            hit = max(hit, abs(chip.velocity.dy))
            chip.velocity.dy = -chip.velocity.dy * restitution
            chip.spin += chip.velocity.dx * 0.02
            chip.velocity.dx *= 0.96  // трение о пол
        }
        return hit
    }

    /// Столкновение двух значков — упругий удар с потерей энергии; возвращает силу удара.
    private func collide(_ i: Int, _ j: Int) -> CGFloat {
        let dx = chips[j].position.x - chips[i].position.x
        let dy = chips[j].position.y - chips[i].position.y
        let distance = max(0.001, hypot(dx, dy))
        let overlap = chips[i].radius + chips[j].radius - distance
        guard overlap > 0 else { return 0 }
        let nx = dx / distance
        let ny = dy / distance
        chips[i].position.x -= nx * overlap / 2
        chips[i].position.y -= ny * overlap / 2
        chips[j].position.x += nx * overlap / 2
        chips[j].position.y += ny * overlap / 2
        let relative =
            (chips[j].velocity.dx - chips[i].velocity.dx) * nx + (chips[j].velocity.dy - chips[i].velocity.dy) * ny
        guard relative < 0 else { return 0 }
        let impulse = -(1 + 0.4) * relative / 2
        chips[i].velocity.dx -= impulse * nx
        chips[i].velocity.dy -= impulse * ny
        chips[j].velocity.dx += impulse * nx
        chips[j].velocity.dy += impulse * ny
        chips[i].spin -= impulse * 0.004
        chips[j].spin += impulse * 0.004
        return abs(relative)
    }
}

/// Повторяемый случайный ряд — значки каждый раз разложены одинаково (снимки экранов).
private struct SeededRandom {
    private var state: UInt64

    init(seed: UInt64) {
        state = seed &* 6_364_136_223_846_793_005 &+ 1
    }

    mutating func next() -> Double {
        state = state &* 6_364_136_223_846_793_005 &+ 1_442_695_040_888_963_407
        return Double(state >> 11) / Double(1 << 53)
    }
}
