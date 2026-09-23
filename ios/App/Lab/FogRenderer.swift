import GameCore
import MapKit
import os

/// Слой тумана поверх всей карты (PLAN.md, §6.4 и §7.2 «Туман»).
final class FogOverlay: NSObject, MKOverlay {
    let coordinate = CLLocationCoordinate2D(latitude: 52.0976, longitude: 23.6880)
    let boundingMapRect = MKMapRect.world
}

/// Рисует туман: сплошная полупрозрачная «дымка», из которой вырезаны открытые клетки.
///
/// Как это устроено:
/// - клетка тумана — пиксель веб-меркатора z22, а точка карты MapKit — пиксель z20, так что тайл z14 —
///   это ровно 16 384 × 16 384 точки карты, а клетка — 64 × 64;
/// - для каждого тайла из битов собирается маска 256 × 256 и рисуется в режиме `destinationOut` —
///   там, где маска непрозрачна, дымка стирается; сглаживание при растяжении даёт мягкие края;
/// - MapKit рисует из нескольких потоков одновременно, поэтому данные читаются из неизменяемого снимка под блокировкой.
final class FogRenderer: MKOverlayRenderer, @unchecked Sendable {
    private let state = OSAllocatedUnfairLock(initialState: State())

    private struct State: Sendable {
        var layer = FogLayer()
        var flipMask = true
    }

    /// Точек карты в тайле z14.
    private static let tileSize = 16_384.0

    /// Новый снимок тумана. Вызывается из главного потока, например раз в секунду во время прогулки.
    func update(_ layer: FogLayer) {
        state.withLock { $0.layer = layer }
        setNeedsDisplay()
    }

    /// Переворот маски по вертикали — проверяем на телефоне, какой вариант правильный (PLAN.md, §7.2).
    func setFlipMask(_ flip: Bool) {
        state.withLock { $0.flipMask = flip }
        setNeedsDisplay()
    }

    override func draw(_ mapRect: MKMapRect, zoomScale: MKZoomScale, in context: CGContext) {
        let snapshot = state.withLock { $0 }
        let drawRect = rect(for: mapRect)

        context.setFillColor(CGColor(red: 0.90, green: 0.87, blue: 0.80, alpha: 0.72))
        context.fill(drawRect)

        let minX = Int((mapRect.minX / Self.tileSize).rounded(.down))
        let maxX = Int((mapRect.maxX / Self.tileSize).rounded(.down))
        let minY = Int((mapRect.minY / Self.tileSize).rounded(.down))
        let maxY = Int((mapRect.maxY / Self.tileSize).rounded(.down))

        context.interpolationQuality = .high
        for x in minX...maxX {
            for y in minY...maxY {
                guard let bits = snapshot.layer.tiles[FogTileKey(x: x, y: y)], let mask = Self.mask(from: bits) else {
                    continue
                }
                let tileRect = rect(
                    for: MKMapRect(
                        x: Double(x) * Self.tileSize, y: Double(y) * Self.tileSize,
                        width: Self.tileSize, height: Self.tileSize))
                context.saveGState()
                context.setBlendMode(.destinationOut)
                if snapshot.flipMask {
                    // Контекст MapKit направлен вниз, а CGImage рисуется снизу вверх — переворачиваем.
                    context.translateBy(x: 0, y: tileRect.minY + tileRect.maxY)
                    context.scaleBy(x: 1, y: -1)
                }
                context.draw(mask, in: tileRect)
                context.restoreGState()
            }
        }
    }

    /// Маска тайла 256 × 256 в RGBA: открытая клетка — непрозрачный белый пиксель, закрытая — прозрачный.
    /// Строка 0 — северный край тайла (y веб-меркатора растёт на юг).
    private static func mask(from bits: FogTileBits) -> CGImage? {
        var bytes = [UInt8](repeating: 0, count: 256 * 256 * 4)
        for index in 0..<(256 * 256) where bits.isSet(index) {
            bytes[index * 4] = 255
            bytes[index * 4 + 1] = 255
            bytes[index * 4 + 2] = 255
            bytes[index * 4 + 3] = 255
        }
        guard let provider = CGDataProvider(data: Data(bytes) as CFData) else { return nil }
        return CGImage(
            width: 256, height: 256, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: 256 * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
            provider: provider, decode: nil, shouldInterpolate: true, intent: .defaultIntent)
    }
}
