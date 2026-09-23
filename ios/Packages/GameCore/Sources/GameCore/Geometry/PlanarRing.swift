/// Замкнутый контур на плоскости: вершины по порядку, последняя соединяется с первой.
/// Повторять первую точку в конце не нужно.
public struct PlanarRing: Hashable, Sendable {
    public var vertices: [PlanarPoint]

    public init(_ vertices: [PlanarPoint]) {
        self.vertices = vertices
    }

    /// Площадь со знаком по формуле шнурования (Гаусса), м².
    /// Больше нуля — обход против часовой стрелки, меньше нуля — по часовой.
    public var signedArea: Double {
        guard vertices.count >= 3 else { return 0 }
        var twiceArea = 0.0
        for index in vertices.indices {
            let current = vertices[index]
            let next = vertices[(index + 1) % vertices.count]
            twiceArea += current.east * next.north - next.east * current.north
        }
        return twiceArea / 2
    }

    /// Площадь без знака, м². Для контура без самопересечений.
    public var area: Double { abs(signedArea) }

    /// Периметр вместе с замыкающим отрезком, м.
    public var perimeter: Double {
        guard vertices.count >= 2 else { return 0 }
        var total = 0.0
        for index in vertices.indices {
            let current = vertices[index]
            let next = vertices[(index + 1) % vertices.count]
            let dx = next.east - current.east
            let dy = next.north - current.north
            total += (dx * dx + dy * dy).squareRoot()
        }
        return total
    }
}
