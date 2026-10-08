@preconcurrency import AVFoundation
import CoreGraphics
import CoreText
import DesignSystem
import Foundation
import GameCore
import UIKit

/// Где лежат собранные видео-повторы: `Library/Caches/gorodki-replays` — это кэш, «Хранилище» его считает и чистит.
enum ReplayStore {
    static var folder: URL {
        let caches =
            FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        return caches.appendingPathComponent("gorodki-replays", isDirectory: true)
    }

    static func removeAll() {
        try? FileManager.default.removeItem(at: folder)
    }
}

/// Как выглядит кадр повтора: ночная карта, след цвета игрока со свечением, голова, подписи. Ночная тема — всегда:
/// ролик смотрят в окне «картинка в картинке» поверх любых приложений, и светящийся след читается на тёмном лучше.
struct ReplayStyle: Sendable {
    var trail: RGBA
    var background: RGBA = Palette.mapLand.night
    var grid: RGBA = Palette.uiSeparator.night
    var ink: RGBA = Palette.uiInk.night
    var ink2: RGBA = Palette.uiInk2.night
    var title: String

    init(player: PlayerColor, title: String) {
        trail = player.edge.night
        self.title = title
    }
}

/// Рисует кадр `TrailReplay.Frame` Core Graphics — одинаково в MP4 (`ReplayVideoWriter`) и в картинку-обложку. Контекст
/// повёрнут так, что `y` растёт вниз, как у `TrailReplay`. Без UIKit: кадры рисуются не на главном потоке.
enum ReplayRenderer {
    static func draw(
        _ frame: TrailReplay.Frame, style: ReplayStyle, in context: CGContext, width: Double, height: Double
    ) {
        let rect = CGRect(x: 0, y: 0, width: width, height: height)
        context.setFillColor(style.background.cgColor)
        context.fill(rect)
        drawGrid(style: style, in: context, rect: rect)
        drawTrail(frame, style: style, in: context, scale: width / 720)
        drawCaption(frame, style: style, in: context, width: width, height: height)
    }

    /// Картинка кадра (обложка ролика, «Кадр в Фото»).
    static func image(_ frame: TrailReplay.Frame, style: ReplayStyle, width: Int, height: Int) -> CGImage? {
        guard let context = bitmap(width: width, height: height, data: nil, bytesPerRow: 0) else { return nil }
        draw(frame, style: style, in: context, width: Double(width), height: Double(height))
        return context.makeImage()
    }

    /// Контекст sRGB BGRA (как пиксельный буфер видео), `y` вниз.
    static func bitmap(width: Int, height: Int, data: UnsafeMutableRawPointer?, bytesPerRow: Int) -> CGContext? {
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
            let context = CGContext(
                data: data, width: width, height: height, bitsPerComponent: 8, bytesPerRow: bytesPerRow, space: space,
                bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)
        else { return nil }
        context.translateBy(x: 0, y: CGFloat(height))
        context.scaleBy(x: 1, y: -1)
        return context
    }

    /// Тонкая сетка «кварталов» — чтобы след не висел в пустоте.
    private static func drawGrid(style: ReplayStyle, in context: CGContext, rect: CGRect) {
        let step = rect.width / 9
        context.setStrokeColor(style.grid.cgColor)
        context.setLineWidth(max(rect.width / 720, 1))
        var x = step / 2
        while x < rect.width {
            context.move(to: CGPoint(x: x, y: 0))
            context.addLine(to: CGPoint(x: x, y: rect.height))
            x += step
        }
        var y = step / 2
        while y < rect.height {
            context.move(to: CGPoint(x: 0, y: y))
            context.addLine(to: CGPoint(x: rect.width, y: y))
            y += step
        }
        context.strokePath()
    }

    /// След: свечение цвета игрока (тень без смещения), сам след и светлая сердцевина; голова — светящаяся точка.
    private static func drawTrail(_ frame: TrailReplay.Frame, style: ReplayStyle, in context: CGContext, scale: Double)
    {
        guard let first = frame.drawn.first else { return }
        let path = CGMutablePath()
        path.move(to: first.cgPoint)
        for point in frame.drawn.dropFirst() {
            path.addLine(to: point.cgPoint)
        }
        context.saveGState()
        context.setLineCap(.round)
        context.setLineJoin(.round)
        context.setShadow(offset: .zero, blur: 22 * scale, color: style.trail.withAlpha(0.95).cgColor)
        context.addPath(path)
        context.setStrokeColor(style.trail.cgColor)
        context.setLineWidth(9 * scale)
        context.strokePath()
        context.restoreGState()

        context.saveGState()
        context.setLineCap(.round)
        context.setLineJoin(.round)
        context.addPath(path)
        context.setStrokeColor(RGBA(0xFFFFFF, alpha: 0.55).cgColor)
        context.setLineWidth(2.5 * scale)
        context.strokePath()
        context.restoreGState()

        let head = frame.head.cgPoint
        context.saveGState()
        context.setShadow(offset: .zero, blur: 28 * scale, color: style.trail.cgColor)
        context.setFillColor(style.trail.cgColor)
        let outer = 13 * scale
        context.fillEllipse(in: CGRect(x: head.x - outer, y: head.y - outer, width: outer * 2, height: outer * 2))
        context.restoreGState()
        context.setFillColor(RGBA(0xFFFFFF).cgColor)
        let inner = 5 * scale
        context.fillEllipse(in: CGRect(x: head.x - inner, y: head.y - inner, width: inner * 2, height: inner * 2))
    }

    /// Подписи: «ГОРОДКИ · …» сверху, пройденные километры снизу — крупно, SF Pro Rounded.
    private static func drawCaption(
        _ frame: TrailReplay.Frame, style: ReplayStyle, in context: CGContext, width: Double, height: Double
    ) {
        let scale = width / 720
        let margin = 36 * scale
        text(
            "ГОРОДКИ · " + style.title.uppercased(), size: 22 * scale, weight: .semibold, color: style.ink2,
            at: CGPoint(x: margin, y: margin + 22 * scale), in: context)
        text(
            NumberText.kilometers(fromMeters: frame.meters, fractionDigits: 2), size: 64 * scale, weight: .heavy,
            color: style.ink, at: CGPoint(x: margin, y: height - margin), in: context)
    }

    /// Строка CoreText по базовой линии `point` (координаты `y` вниз).
    private static func text(
        _ string: String, size: Double, weight: UIFont.Weight, color: RGBA, at point: CGPoint, in context: CGContext
    ) {
        let base = UIFont.systemFont(ofSize: size, weight: weight)
        let font = base.fontDescriptor.withDesign(.rounded).map { UIFont(descriptor: $0, size: size) } ?? base
        let attributed = NSAttributedString(
            string: string,
            attributes: [
                NSAttributedString.Key(kCTFontAttributeName as String): font as CTFont,
                NSAttributedString.Key(kCTForegroundColorAttributeName as String): color.cgColor,
            ])
        let line = CTLineCreateWithAttributedString(attributed)
        context.saveGState()
        // CoreText рисует с `y` вверх — развернуть обратно у базовой линии.
        context.translateBy(x: point.x, y: point.y)
        context.scaleBy(x: 1, y: -1)
        context.textPosition = .zero
        CTLineDraw(line, context)
        context.restoreGState()
    }
}

extension TrailReplay.Point {
    var cgPoint: CGPoint { CGPoint(x: x, y: y) }
}

extension RGBA {
    var cgColor: CGColor { CGColor(srgbRed: red, green: green, blue: blue, alpha: alpha) }
}

/// MP4 из кадров (`AVAssetWriter`, H.264): след рисуется за `animated` кадров, последний кадр держится ещё `hold`.
enum ReplayVideoWriter {
    static let side = 720
    static let fps: Int32 = 30
    /// 6 секунд движения и секунда на итог.
    static let animatedFrames = 180
    static let holdFrames = 30

    enum Failure: Error {
        case writer(String)
    }

    /// Записать ролик в `url` (старый файл заменяется). `progress` — доля готовых кадров 0…1, зовётся не с главного
    /// потока. Отмена задачи обрывает запись.
    @concurrent
    static func write(
        coordinates: [Coordinate], style: ReplayStyle, to url: URL, progress: @escaping @Sendable (Double) -> Void
    ) async throws {
        let replay = TrailReplay(
            coordinates: coordinates, width: Double(side), height: Double(side), padding: 110,
            frameCount: animatedFrames)
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? FileManager.default.removeItem(at: url)
        let writer = try AVAssetWriter(outputURL: url, fileType: .mp4)
        let input = AVAssetWriterInput(
            mediaType: .video,
            outputSettings: [
                AVVideoCodecKey: AVVideoCodecType.h264, AVVideoWidthKey: side, AVVideoHeightKey: side,
            ])
        input.expectsMediaDataInRealTime = false
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(
            assetWriterInput: input,
            sourcePixelBufferAttributes: [
                kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
                kCVPixelBufferWidthKey as String: side, kCVPixelBufferHeightKey as String: side,
            ])
        guard writer.canAdd(input) else { throw Failure.writer("видео нельзя записать") }
        writer.add(input)
        guard writer.startWriting() else {
            throw Failure.writer(writer.error?.localizedDescription ?? "запись не началась")
        }
        writer.startSession(atSourceTime: .zero)

        let total = animatedFrames + holdFrames
        for index in 0..<total {
            if Task.isCancelled {
                writer.cancelWriting()
                throw CancellationError()
            }
            while !input.isReadyForMoreMediaData {
                try await Task.sleep(for: .milliseconds(5))
            }
            guard
                let buffer = pixelBuffer(
                    for: replay.frame(min(index, animatedFrames - 1)), style: style, adaptor: adaptor)
            else { throw Failure.writer("не хватило памяти на кадр") }
            guard adaptor.append(buffer, withPresentationTime: CMTime(value: CMTimeValue(index), timescale: fps)) else {
                throw Failure.writer(writer.error?.localizedDescription ?? "кадр не записался")
            }
            progress(Double(index + 1) / Double(total))
        }
        input.markAsFinished()
        await writer.finishWriting()
        if writer.status != .completed {
            throw Failure.writer(writer.error?.localizedDescription ?? "ролик не дописался")
        }
    }

    private static func pixelBuffer(
        for frame: TrailReplay.Frame, style: ReplayStyle, adaptor: AVAssetWriterInputPixelBufferAdaptor
    ) -> CVPixelBuffer? {
        var buffer: CVPixelBuffer?
        if let pool = adaptor.pixelBufferPool {
            CVPixelBufferPoolCreatePixelBuffer(nil, pool, &buffer)
        } else {
            CVPixelBufferCreate(nil, side, side, kCVPixelFormatType_32BGRA, nil, &buffer)
        }
        guard let buffer else { return nil }
        CVPixelBufferLockBaseAddress(buffer, [])
        defer { CVPixelBufferUnlockBaseAddress(buffer, []) }
        guard
            let context = ReplayRenderer.bitmap(
                width: side, height: side, data: CVPixelBufferGetBaseAddress(buffer),
                bytesPerRow: CVPixelBufferGetBytesPerRow(buffer))
        else { return nil }
        ReplayRenderer.draw(frame, style: style, in: context, width: Double(side), height: Double(side))
        return buffer
    }
}
