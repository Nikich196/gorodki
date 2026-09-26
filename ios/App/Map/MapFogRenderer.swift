import DesignSystem
import GameCore
import MapKit
import os

/// Туман «Исследования» на карте игры (PLAN.md, §6.4; docs/design/tokens.md, §2 «Туман»): дымка `FogStyle.haze`
/// над дорогами, из которой вырезано открытое, кромка открытого `FogStyle.edge` шириной `FogStyle.edgeBandWidth`
/// на экране и мягкий край дымки за ней.
///
/// Растр — как у стенда S4 (`FogRenderer`, «Лаборатория → Карта»): клетка тумана — пиксель z22, тайл z14 — ровно
/// 16 384 × 16 384 точки карты, маска тайла 256 × 256. У тайла две маски:
/// - **чёткая** — открытые клетки: по ней кромка (маска, сдвинутая на ширину полосы в восемь сторон) и прозрачное
///   открытое;
/// - **мягкая** — та же, размытая σ ≈ 2 клетки (`FogStyle.softEdgeSigmaCells`) с краями соседних тайлов, чтобы на линии
///   тайла не было шва: по ней дымка нарастает за кромкой, а не обрывается.
///
/// MapKit рисует из нескольких потоков, поэтому данные — неизменяемый снимок под блокировкой, маски — неизменяемые
/// `CGImage`.
final class MapFogRenderer: MKOverlayRenderer, @unchecked Sendable {
    private struct State: Sendable {
        var tiles: [FogTileKey: FogTileBits] = [:]
        var haze = FogStyle.haze.day
        var edge = FogStyle.edge.day
        var masks: [FogTileKey: Masks] = [:]
        /// Туман виден только на «Исследовании»; оверлей остаётся на карте всегда.
        var visible = false
    }

    /// Маски тайла: `CGImage` неизменяем, его можно отдавать потокам отрисовки MapKit.
    private struct Masks: @unchecked Sendable {
        let sharp: CGImage
        let soft: CGImage
    }

    private let state = OSAllocatedUnfairLock(initialState: State())

    /// Точек карты в тайле z14.
    private static let tileSize = 16_384.0
    private static let side = 256
    /// Окно размытия: два прохода окна 2r + 1 по строкам и столбцам — σ² = 2 · ((2r + 1)² − 1) / 12. При r = 2 — σ = 2
    /// клетки, как в `FogStyle.softEdgeSigmaCells`.
    private static let blurRadius = Int(
        ((FogStyle.softEdgeSigmaCells * FogStyle.softEdgeSigmaCells * 6 + 1).squareRoot() - 1) / 2)
    /// Сколько клеток соседних тайлов нужно размытию: два прохода по r.
    private static let margin = 2 * blurRadius

    /// Новый туман и тема. Вызывается из главного потока, когда пришли тайлы или сменилась тема.
    func update(_ tiles: [FogTileKey: FogTileBits], theme: Theme) {
        state.withLock { state in
            // Маски пересобираются только у изменившихся тайлов и их соседей: мягкая маска берёт их края.
            for key in Set(tiles.keys).union(state.tiles.keys) where state.tiles[key] != tiles[key] {
                for dx in -1...1 {
                    for dy in -1...1 {
                        state.masks[FogTileKey(x: key.x + dx, y: key.y + dy)] = nil
                    }
                }
            }
            state.tiles = tiles
            state.haze = FogStyle.haze[theme]
            state.edge = FogStyle.edge[theme]
        }
        setNeedsDisplay()
    }

    /// Показать или скрыть туман — и перерисовать.
    func setVisible(_ visible: Bool) {
        let changed = state.withLock { state in
            defer { state.visible = visible }
            return state.visible != visible
        }
        if changed {
            setNeedsDisplay()
        }
    }

    override func draw(_ mapRect: MKMapRect, zoomScale: MKZoomScale, in context: CGContext) {
        let snapshot = state.withLock { $0 }
        guard snapshot.visible else { return }
        context.setFillColor(Self.cgColor(snapshot.haze))
        context.fill(rect(for: mapRect))

        // Кромка шириной 1,8 pt на экране — в точках карты это 1,8 / zoomScale; мягкий край — до `margin` клеток
        // (по 64 точки карты). Тайл, чей край заходит в эту часть карты, рисуется, даже если сам он за её краем.
        let band = FogStyle.edgeBandWidth / Double(zoomScale)
        let reach = max(band, Double(Self.margin) * Self.tileSize / Double(Self.side))
        let minX = Int(((mapRect.minX - reach) / Self.tileSize).rounded(.down))
        let maxX = Int(((mapRect.maxX + reach) / Self.tileSize).rounded(.down))
        let minY = Int(((mapRect.minY - reach) / Self.tileSize).rounded(.down))
        let maxY = Int(((mapRect.maxY + reach) / Self.tileSize).rounded(.down))
        var visible: [(rect: CGRect, masks: Masks)] = []
        for x in minX...maxX {
            for y in minY...maxY {
                let key = FogTileKey(x: x, y: y)
                guard let masks = masks(for: key, in: snapshot) else { continue }
                let tileRect = rect(
                    for: MKMapRect(
                        x: Double(x) * Self.tileSize, y: Double(y) * Self.tileSize,
                        width: Self.tileSize, height: Self.tileSize))
                visible.append((tileRect, masks))
            }
        }
        guard !visible.isEmpty else { return }
        context.interpolationQuality = .high

        // 1. Мягкий край: дымка нарастает от открытого наружу.
        context.saveGState()
        context.setBlendMode(.clear)
        for (tileRect, masks) in visible {
            fill(tileRect, through: masks.soft, in: context)
        }
        context.restoreGState()

        // 2. Кромка: открытое, сдвинутое в восемь сторон, одной прозрачной «плёнкой» — перекрытия не темнеют.
        let offset = CGFloat(band)
        context.saveGState()
        context.setAlpha(snapshot.edge.alpha)
        context.beginTransparencyLayer(auxiliaryInfo: nil)
        context.setFillColor(Self.cgColor(snapshot.edge.withAlpha(1)))
        for (tileRect, masks) in visible {
            for dx in [-offset, 0, offset] {
                for dy in [-offset, 0, offset] where dx != 0 || dy != 0 {
                    fill(tileRect.offsetBy(dx: dx, dy: dy), through: masks.sharp, in: context)
                }
            }
        }
        context.endTransparencyLayer()
        context.restoreGState()

        // 3. Открытое — прозрачно: стираются и дымка, и кромка внутри.
        context.saveGState()
        context.setBlendMode(.clear)
        for (tileRect, masks) in visible {
            fill(tileRect, through: masks.sharp, in: context)
        }
        context.restoreGState()
    }

    /// Закрасить `rect` там, где маска открыта. Контекст MapKit направлен вниз, а маска — снизу вверх: переворачиваем
    /// (как у стенда S4; ориентацию на телефоне проверяют по «Т» стенда).
    private func fill(_ rect: CGRect, through mask: CGImage, in context: CGContext) {
        context.saveGState()
        context.translateBy(x: 0, y: rect.minY + rect.maxY)
        context.scaleBy(x: 1, y: -1)
        context.clip(to: rect, mask: mask)
        context.fill(rect)
        context.restoreGState()
    }

    /// Маски тайла: есть открытое в нём — обе; нет, но рядом есть — только мягкая заходит в него, чёткая пустая.
    private func masks(for key: FogTileKey, in snapshot: State) -> Masks? {
        if let cached = snapshot.masks[key] {
            return cached
        }
        var neighbors: [FogTileBits?] = []
        for dy in -1...1 {
            for dx in -1...1 {
                neighbors.append(snapshot.tiles[FogTileKey(x: key.x + dx, y: key.y + dy)])
            }
        }
        guard neighbors.contains(where: { ($0?.count ?? 0) > 0 }),
            let sharp = Self.image(Self.cells(neighbors[4])),
            let soft = Self.image(Self.softCells(neighbors))
        else { return nil }
        let masks = Masks(sharp: sharp, soft: soft)
        let tiles = snapshot.tiles
        state.withLock { state in
            if state.tiles == tiles {
                state.masks[key] = masks
            }
        }
        return masks
    }

    /// Клетки тайла: 255 — открыто. Строка 0 — северный край (y веб-меркатора растёт на юг).
    static func cells(_ bits: FogTileBits?) -> [UInt8] {
        var bytes = [UInt8](repeating: 0, count: side * side)
        guard let bits else { return bytes }
        for index in 0..<(side * side) where bits.isSet(index) {
            bytes[index] = 255
        }
        return bytes
    }

    /// Мягкая маска тайла: его клетки с полосой `margin` клеток соседей (порядок `neighbors` — строки с севера,
    /// в строке с запада, центр — пятый), размытые двумя проходами окна по строкам и столбцам, без полосы.
    static func softCells(_ neighbors: [FogTileBits?]) -> [UInt8] {
        let padded = side + 2 * margin
        var bytes = [UInt8](repeating: 0, count: padded * padded)
        for (index, bits) in neighbors.enumerated() {
            guard let bits, bits.count > 0 else { continue }
            let offsetX = (index % 3 - 1) * side + margin
            let offsetY = (index / 3 - 1) * side + margin
            for y in 0..<side {
                let row = offsetY + y
                guard (0..<padded).contains(row) else { continue }
                for x in 0..<side where bits.isSet(y * side + x) {
                    let column = offsetX + x
                    if (0..<padded).contains(column) {
                        bytes[row * padded + column] = 255
                    }
                }
            }
        }
        for _ in 0..<2 {
            bytes = boxBlur(bytes, side: padded, radius: blurRadius, horizontal: true)
            bytes = boxBlur(bytes, side: padded, radius: blurRadius, horizontal: false)
        }
        var result = [UInt8](repeating: 0, count: side * side)
        for y in 0..<side {
            for x in 0..<side {
                result[y * side + x] = bytes[(y + margin) * padded + x + margin]
            }
        }
        return result
    }

    /// Среднее по окну 2 · `radius` + 1 вдоль строк или столбцов квадрата `side` × `side`; за краем — нули.
    static func boxBlur(_ source: [UInt8], side: Int, radius: Int, horizontal: Bool) -> [UInt8] {
        guard radius > 0 else { return source }
        let window = 2 * radius + 1
        var result = [UInt8](repeating: 0, count: source.count)
        for line in 0..<side {
            func at(_ position: Int) -> Int {
                guard (0..<side).contains(position) else { return 0 }
                return Int(horizontal ? source[line * side + position] : source[position * side + line])
            }
            var sum = 0
            for position in -radius...radius {
                sum += at(position)
            }
            for position in 0..<side {
                let value = UInt8((sum + window / 2) / window)
                if horizontal {
                    result[line * side + position] = value
                } else {
                    result[position * side + line] = value
                }
                sum += at(position + radius + 1) - at(position - radius)
            }
        }
        return result
    }

    /// Маска 256 × 256 в оттенках серого без альфы — такую принимает `clip(to:mask:)`: белое — открыто.
    static func image(_ bytes: [UInt8]) -> CGImage? {
        guard let provider = CGDataProvider(data: Data(bytes) as CFData) else { return nil }
        return CGImage(
            width: side, height: side, bitsPerComponent: 8, bitsPerPixel: 8, bytesPerRow: side,
            space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.none.rawValue),
            provider: provider, decode: nil, shouldInterpolate: true, intent: .defaultIntent)
    }

    private static func cgColor(_ color: RGBA) -> CGColor {
        CGColor(srgbRed: color.red, green: color.green, blue: color.blue, alpha: color.alpha)
    }
}
