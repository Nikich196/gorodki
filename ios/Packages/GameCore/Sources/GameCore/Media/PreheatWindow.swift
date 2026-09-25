/// Какие миниатюры «Галереи» готовить заранее (пункт 3 листика, кэширование): видимые строки сетки и запас
/// `margin` элементов с обеих сторон. Прокрутили — начать готовить новые и отпустить ушедшие далеко; кэш миниатюр
/// (`PHCachingImageManager`) держит только окно, а не всю медиатеку.
public struct PreheatWindow: Equatable, Sendable {
    /// Элементы, которые сейчас готовятся.
    public private(set) var range: Range<Int> = 0..<0

    public init() {}

    /// Новое окно по видимым элементам.
    /// - Parameters:
    ///   - visible: номера видимых элементов (пусто — ничего не видно, окно не меняется).
    ///   - total: сколько всего элементов.
    ///   - margin: запас с каждой стороны.
    /// - Returns: какие номера начать готовить и какие отпустить — по возрастанию.
    public mutating func update(visible: ClosedRange<Int>?, total: Int, margin: Int) -> (start: [Int], stop: [Int]) {
        let all = 0..<max(total, 0)
        let wanted: Range<Int>
        if let visible {
            let lower = max(visible.lowerBound - max(margin, 0), 0)
            let upper = min(visible.upperBound + max(margin, 0) + 1, all.upperBound)
            wanted = lower < upper ? lower..<upper : 0..<0
        } else {
            wanted = range.clamped(to: all)
        }
        let old = range.clamped(to: all)
        let start = wanted.filter { !old.contains($0) }
        let stop = range.filter { !wanted.contains($0) }
        range = wanted
        return (start, stop)
    }
}
