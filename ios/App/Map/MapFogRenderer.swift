import DesignSystem
import GameCore
import MapKit
import os

/// Туман «Исследования» на карте игры (PLAN.md, §6.4; docs/design/tokens.md, §2 «Туман»): дымка `FogStyle.haze`
/// над дорогами, из которой вырезано открытое, и кромка открытого `FogStyle.edge` шириной `FogStyle.edgeBandWidth`
/// на экране.
///
/// Растр — как у стенда S4 (`FogRenderer`, «Лаборатория → Карта»): клетка тумана — пиксель z22, тайл z14 — ровно
/// 16 384 × 16 384 точки карты, маска тайла 256 × 256 растягивается со сглаживанием — края мягкие. Кромка — та же маска,
/// сдвинутая на ширину полосы в восемь сторон (расширение), минус само открытое. MapKit рисует из нескольких потоков,
/// поэтому данные — неизменяемый снимок под блокировкой, маски — неизменяемые `CGImage`.
final class MapFogRenderer: MKOverlayRenderer, @unchecked Sendable {
    private struct State: Sendable {
        var tiles: [FogTileKey: FogTileBits] = [:]
        var haze = FogStyle.haze.day
        var edge = FogStyle.edge.day
        var masks: [FogTileKey: Mask] = [:]
    }

    /// Маска тайла: `CGImage` неизменяем, его можно отдавать потокам отрисовки MapKit.
    private struct Mask: @unchecked Sendable {
        let image: CGImage
    }

    private let state = OSAllocatedUnfairLock(initialState: State())

    /// Точек карты в тайле z14.
    private static let tileSize = 16_384.0

    /// Новый туман и тема. Вызывается из главного потока, когда пришли тайлы или сменилась тема.
    func update(_ tiles: [FogTileKey: FogTileBits], theme: Theme) {
        state.withLock { state in
            // Маски пересобираются только у изменившихся тайлов.
            for (key, bits) in tiles where state.tiles[key] != bits {
                state.masks[key] = nil
            }
            state.tiles = tiles
            state.haze = FogStyle.haze[theme]
            state.edge = FogStyle.edge[theme]
        }
        setNeedsDisplay()
    }

    override func draw(_ mapRect: MKMapRect, zoomScale: MKZoomScale, in context: CGContext) {
        let snapshot = state.withLock { $0 }
        let drawRect = rect(for: mapRect)
        context.setFillColor(Self.cgColor(snapshot.haze))
        context.fill(drawRect)

        // Кромка шириной 1,8 pt на экране — в точках карты это 1,8 / zoomScale. Тайл, чья кромка заходит в эту часть
        // карты, рисуется, даже если сам он за её краем.
        let band = FogStyle.edgeBandWidth / Double(zoomScale)
        let minX = Int(((mapRect.minX - band) / Self.tileSize).rounded(.down))
        let maxX = Int(((mapRect.maxX + band) / Self.tileSize).rounded(.down))
        let minY = Int(((mapRect.minY - band) / Self.tileSize).rounded(.down))
        let maxY = Int(((mapRect.maxY + band) / Self.tileSize).rounded(.down))
        var visible: [(rect: CGRect, mask: CGImage)] = []
        for x in minX...maxX {
            for y in minY...maxY {
                let key = FogTileKey(x: x, y: y)
                guard let mask = mask(for: key, in: snapshot) else { continue }
                let tileRect = rect(
                    for: MKMapRect(
                        x: Double(x) * Self.tileSize, y: Double(y) * Self.tileSize,
                        width: Self.tileSize, height: Self.tileSize))
                visible.append((tileRect, mask))
            }
        }
        guard !visible.isEmpty else { return }
        context.interpolationQuality = .high

        // Кромка: открытое, сдвинутое в восемь сторон, одной прозрачной «плёнкой» — перекрытия не темнеют.
        let offset = CGFloat(band)
        context.saveGState()
        context.setAlpha(snapshot.edge.alpha)
        context.beginTransparencyLayer(auxiliaryInfo: nil)
        context.setFillColor(Self.cgColor(snapshot.edge.withAlpha(1)))
        for (tileRect, mask) in visible {
            for dx in [-offset, 0, offset] {
                for dy in [-offset, 0, offset] where dx != 0 || dy != 0 {
                    fill(tileRect.offsetBy(dx: dx, dy: dy), through: mask, in: context)
                }
            }
        }
        context.endTransparencyLayer()
        context.restoreGState()

        // Открытое — прозрачно: стираются и дымка, и кромка внутри.
        context.saveGState()
        context.setBlendMode(.clear)
        for (tileRect, mask) in visible {
            fill(tileRect, through: mask, in: context)
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

    private func mask(for key: FogTileKey, in snapshot: State) -> CGImage? {
        if let cached = snapshot.masks[key] {
            return cached.image
        }
        guard let bits = snapshot.tiles[key], bits.count > 0, let image = Self.mask(from: bits) else { return nil }
        let mask = Mask(image: image)
        state.withLock { state in
            if state.tiles[key] == bits {
                state.masks[key] = mask
            }
        }
        return image
    }

    /// Маска тайла 256 × 256 в оттенках серого без альфы — такую принимает `clip(to:mask:)`: белое — открыто.
    /// Строка 0 — северный край тайла (y веб-меркатора растёт на юг).
    static func mask(from bits: FogTileBits) -> CGImage? {
        var bytes = [UInt8](repeating: 0, count: 256 * 256)
        for index in 0..<(256 * 256) where bits.isSet(index) {
            bytes[index] = 255
        }
        guard let provider = CGDataProvider(data: Data(bytes) as CFData) else { return nil }
        return CGImage(
            width: 256, height: 256, bitsPerComponent: 8, bitsPerPixel: 8, bytesPerRow: 256,
            space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.none.rawValue),
            provider: provider, decode: nil, shouldInterpolate: true, intent: .defaultIntent)
    }

    private static func cgColor(_ color: RGBA) -> CGColor {
        CGColor(srgbRed: color.red, green: color.green, blue: color.blue, alpha: color.alpha)
    }
}
