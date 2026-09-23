import Foundation
import Testing

@testable import GameCore

/// Эталоны растеризатора тумана — `contracts/fog.v1.json` (PLAN.md, §7.2: «растеризатор тумана — общие с C# эталонные
/// векторы»). Их записывает этот тест (`GORODKI_UPDATE_CONTRACTS=1 swift test`), проверяют обе реализации: эта
/// и серверная (`FogContractTests`) — бит в бит.
@Suite("Контракт растеризатора тумана (contracts/fog.v1.json)")
struct FogContractTests {
    struct Contract: Codable, Equatable {
        var version: Int
        var scenarios: [Scenario]
    }

    struct Scenario: Codable, Equatable {
        var name: String
        var operations: [Operation]
        var expected: Expected
    }

    /// Открыть круг вокруг точки или полосу вдоль пути. Координаты — в точности хранения (1e-7°).
    struct Operation: Codable, Equatable {
        var around: [Double]?
        var path: [Double]?
        var radius: Double
        var maxGap: Double?
    }

    struct Expected: Codable, Equatable {
        var cellCount: Int
        var tiles: [Tile]
    }

    /// Тайл: ненулевые 64-битные слова битовой карты — номер слова и значение в шестнадцатеричном виде.
    struct Tile: Codable, Equatable {
        var x: Int
        var y: Int
        var count: Int
        var words: [[String]]
    }

    private static let relativePath = "contracts/fog.v1.json"

    @Test("Растеризатор на телефоне открывает ровно клетки из эталонов")
    func replayMatchesExpectations() throws {
        for scenario in try Self.load().scenarios {
            #expect(Self.run(scenario.operations) == scenario.expected, "\(scenario.name)")
        }
    }

    @Test("Эталоны совпадают с текущими сценариями")
    func contractIsUpToDate() throws {
        let generated = Self.generate()
        if ProcessInfo.processInfo.environment["GORODKI_UPDATE_CONTRACTS"] == "1" {
            try Self.write(generated)
        }
        #expect(try Self.load() == generated)
    }

    // MARK: - Прогон

    private static func run(_ operations: [Operation]) -> Expected {
        var layer = FogLayer()
        for operation in operations {
            if let point = operation.around {
                layer.reveal(around: Coordinate(latitude: point[0], longitude: point[1]), radius: operation.radius)
            } else if let path = operation.path {
                layer.reveal(
                    from: Coordinate(latitude: path[0], longitude: path[1]),
                    to: Coordinate(latitude: path[2], longitude: path[3]),
                    radius: operation.radius,
                    maxGap: operation.maxGap ?? ExplorationSettings().maxGapMeters.run)
            }
        }
        let tiles = layer.tiles.keys.sorted().map { key -> Tile in
            let bits = layer.tiles[key] ?? FogTileBits()
            let words = bits.words.enumerated()
                .filter { $0.element != 0 }
                .map { [String($0.offset), String($0.element, radix: 16)] }
            return Tile(x: key.x, y: key.y, count: bits.count, words: words)
        }
        return Expected(cellCount: layer.cellCount, tiles: tiles)
    }

    // MARK: - Сценарии

    /// Точка в точности хранения сервера.
    private static func stored(_ coordinate: Coordinate) -> [Double] {
        [(coordinate.latitude * 1e7).rounded() / 1e7, (coordinate.longitude * 1e7).rounded() / 1e7]
    }

    private static func generate() -> Contract {
        let plane = LocalTangentPlane(origin: Coordinate(latitude: 52.0976, longitude: 23.6880))
        func at(_ east: Double, _ north: Double) -> [Double] {
            stored(plane.unproject(PlanarPoint(east: east, north: north)))
        }
        func around(_ point: [Double], radius: Double = 25) -> Operation {
            Operation(around: point, path: nil, radius: radius, maxGap: nil)
        }
        func path(_ from: [Double], _ to: [Double], radius: Double = 25, maxGap: Double = 100) -> Operation {
            Operation(around: nil, path: from + to, radius: radius, maxGap: maxGap)
        }
        func walk(_ vertices: [(Double, Double)], step: Double, maxGap: Double = 100) -> [Operation] {
            var points: [[Double]] = []
            for (index, (x1, y1)) in vertices.enumerated().dropLast() {
                let (x2, y2) = vertices[index + 1]
                let length = ((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)).squareRoot()
                let count = max(1, Int((length / step).rounded(.up)))
                for i in 0..<count {
                    let t = Double(i) / Double(count)
                    points.append(at(x1 + (x2 - x1) * t, y1 + (y2 - y1) * t))
                }
            }
            points.append(at(vertices[vertices.count - 1].0, vertices[vertices.count - 1].1))
            return zip(points, points.dropFirst()).map { path($0, $1, maxGap: maxGap) }
        }

        // Угол четырёх тайлов уровня 14 — найти рядом с началом координат: граница тайлов проходит по клеткам,
        // кратным 256.
        let corner = FogGrid.center(of: FogCell(x: 9_271 << 8, y: 5_404 << 8))

        let there = walk([(0, 0), (100, 0)], step: 5)
        let andAgain = walk([(0, 0), (100, 0)], step: 6)
        let scenarios: [(String, [Operation])] = [
            ("одна точка — круг 25 м", [around(at(0, 0))]),
            ("точка на углу четырёх тайлов", [around(stored(corner))]),
            ("«Радар» — круг 50 м", [around(at(0, 0), radius: 50)]),
            ("прямая 300 м на восток, точки каждые 3 м", walk([(0, 0), (300, 0)], step: 3)),
            ("диагональ 250 м, точки каждые 7 м", walk([(0, 0), (180, 170)], step: 7)),
            ("разрыв 150 м пешком — только круги на концах", [path(at(0, 0), at(150, 0))]),
            ("разрыв 150 м на велосипеде — полоса (порог 200 м)", [path(at(0, 0), at(150, 0), maxGap: 200)]),
            ("квартал 200 × 200 м по кругу", walk([(0, 0), (200, 0), (200, 200), (0, 200), (0, 0)], step: 4)),
            ("повторный проход не меняет карту", there + andAgain),
        ]
        return Contract(
            version: 1,
            scenarios: scenarios.map { Scenario(name: $0.0, operations: $0.1, expected: run($0.1)) })
    }

    // MARK: - Файл

    private static func contractURL() throws -> URL {
        var directory = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
        for _ in 0..<10 {
            if FileManager.default.fileExists(atPath: directory.appendingPathComponent("contracts").path) {
                return directory.appendingPathComponent(relativePath)
            }
            directory.deleteLastPathComponent()
        }
        throw CocoaError(.fileNoSuchFile, userInfo: [NSFilePathErrorKey: relativePath])
    }

    private static func load() throws -> Contract {
        try JSONDecoder().decode(Contract.self, from: Data(contentsOf: contractURL()))
    }

    /// Одна операция и один тайл — одна строка: правки эталонов видны в обзоре PR.
    private static func write(_ contract: Contract) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        func json(_ value: some Encodable) throws -> String {
            String(decoding: try encoder.encode(value), as: UTF8.self)
        }

        var lines = ["{\"version\":\(contract.version),\"scenarios\":["]
        for (index, scenario) in contract.scenarios.enumerated() {
            lines.append("{\"name\":\(try json(scenario.name)),\"operations\":[")
            lines += try scenario.operations.enumerated().map { i, operation in
                try json(operation) + (i + 1 < scenario.operations.count ? "," : "")
            }
            lines.append("],\"expected\":{\"cellCount\":\(scenario.expected.cellCount),\"tiles\":[")
            lines += try scenario.expected.tiles.enumerated().map { i, tile in
                try json(tile) + (i + 1 < scenario.expected.tiles.count ? "," : "")
            }
            lines.append("]}}" + (index + 1 < contract.scenarios.count ? "," : ""))
        }
        lines.append("]}")
        try (lines.joined(separator: "\n") + "\n").write(to: try contractURL(), atomically: true, encoding: .utf8)
    }
}
