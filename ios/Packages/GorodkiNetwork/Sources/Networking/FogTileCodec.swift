import CZlib
import Foundation

/// Тайл тумана с сервера: 8 192 байта бит (256 × 256 клеток), сжатые raw DEFLATE, как `FogTileCodec` на сервере
/// (docs/architecture/fog.md). Слова по 64 бита — little-endian; клетка `(x, y)` тайла — бит `y · 256 + x`.
public enum FogTileCodec {
    public static let wordCount = 256 * 256 / 64
    public static let byteCount = wordCount * 8

    public enum Failure: Error, Equatable {
        /// Данные повреждены: не распаковались или после распаковки не ровно 8 КБ.
        case corrupted
    }

    /// Распаковать тайл в слова по 64 бита — в том же порядке, что `FogTileBits.words` в GameCore.
    public static func words(fromCompressed data: Data) throws -> [UInt64] {
        let bytes = try inflateRaw(data, expected: byteCount)
        // Обычный цикл, а не reduce: выражение с reduce и сдвигами Xcode 26 не успевает проверить по типам.
        var words = [UInt64](repeating: 0, count: wordCount)
        for index in 0..<wordCount {
            var word: UInt64 = 0
            for shift in 0..<8 {
                word |= UInt64(bytes[index * 8 + shift]) << UInt64(8 * shift)
            }
            words[index] = word
        }
        return words
    }

    /// Число открытых клеток — чтобы сверить с `cellCount` из ответа.
    public static func cellCount(_ words: [UInt64]) -> Int {
        words.reduce(0) { $0 + $1.nonzeroBitCount }
    }

    /// raw DEFLATE (RFC 1951, без заголовка zlib): `windowBits = −15`. Больше `expected` байт — это уже ошибка.
    static func inflateRaw(_ data: Data, expected: Int) throws -> [UInt8] {
        var input = [UInt8](data)
        var output = [UInt8](repeating: 0, count: expected + 1)
        var stream = z_stream()
        guard inflateInit2_(&stream, -15, ZLIB_VERSION, Int32(MemoryLayout<z_stream>.size)) == Z_OK else {
            throw Failure.corrupted
        }
        defer { inflateEnd(&stream) }
        let status = input.withUnsafeMutableBufferPointer { source in
            output.withUnsafeMutableBufferPointer { target in
                stream.next_in = source.baseAddress
                stream.avail_in = uInt(source.count)
                stream.next_out = target.baseAddress
                stream.avail_out = uInt(target.count)
                return inflate(&stream, Z_FINISH)
            }
        }
        guard status == Z_STREAM_END, Int(stream.total_out) == expected else { throw Failure.corrupted }
        return Array(output.prefix(expected))
    }
}
