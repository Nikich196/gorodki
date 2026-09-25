import Foundation
import GameCore

/// Сетка чисел детектора: каждое число — списком через запятую, перебираются все сочетания. По умолчанию — числа
/// из кода (`LoopDetectorSettings()`, они же версия 1 конфига).
public struct DetectorGrid: Equatable, Sendable {
    public var minPathMeters = [LoopDetectorSettings().minPathMeters]
    public var radiusFactor = [LoopDetectorSettings().radiusFactor]
    public var minRadiusMeters = [LoopDetectorSettings().minRadiusMeters]
    public var maxRadiusMeters = [LoopDetectorSettings().maxRadiusMeters]
    public var minEstimatedAreaSquareMeters = [LoopDetectorSettings().minEstimatedAreaSquareMeters]

    public init() {}

    /// Все сочетания; R_мин больше R_макс — пропускается.
    public var settings: [LoopDetectorSettings] {
        var result: [LoopDetectorSettings] = []
        for path in minPathMeters {
            for factor in radiusFactor {
                for low in minRadiusMeters {
                    for high in maxRadiusMeters where low <= high {
                        for area in minEstimatedAreaSquareMeters {
                            var settings = LoopDetectorSettings()
                            settings.minPathMeters = path
                            settings.radiusFactor = factor
                            settings.minRadiusMeters = low
                            settings.maxRadiusMeters = high
                            settings.minEstimatedAreaSquareMeters = area
                            result.append(settings)
                        }
                    }
                }
            }
        }
        return result
    }
}

/// Неверные параметры запуска.
public struct UsageError: Error, CustomStringConvertible {
    public var description: String

    public init(description: String) {
        self.description = description
    }
}

/// Параметры командной строки.
public struct ReplayOptions: Equatable, Sendable {
    public var input: String?
    public var output: String?
    public var runId: String?
    public var newcomer = false
    public var synthetic = false
    public var grid = DetectorGrid()

    public static let usage = """
        Разбор записанного забега: детектор петли телефона (GameCore) с другими числами.

          LoopReplay <мои-данные.json> [параметры]    найти петли, JSON — в --out или на экран
          LoopReplay --synthetic [--out файл]          синтетические «Мои данные»: квадрат, полоса, дуга

        Числа — списком через запятую, дробь через точку; перебираются все сочетания:
          --r-min 20          R не меньше, м (minRadiusMeters)
          --r-max 50          R не больше, м (maxRadiusMeters; больше 50 детектор проверяет не полностью)
          --r-factor 1.62     множитель в R = k·√(accᵢ² + accₙ²); 0 — R всегда равен --r-min
          --min-path 150      путь петли не короче, м
          --min-est-area 1000 не заявлять петлю с оценкой площади меньше, м²
        Прочее:
          --run <id>          только этот забег
          --newcomer          судья с порогом точности новичка (35 м вместо 25)
          --out <файл>        куда записать JSON
        """

    public init() {}

    public static func parse(_ arguments: [String]) throws -> ReplayOptions {
        var options = ReplayOptions()
        var rest = arguments[...]
        while let argument = rest.popFirst() {
            func value() throws -> String {
                guard let next = rest.popFirst() else {
                    throw UsageError(description: "После \(argument) нужно значение.")
                }
                return next
            }
            switch argument {
            case "--r-min": options.grid.minRadiusMeters = try numbers(try value(), for: argument)
            case "--r-max": options.grid.maxRadiusMeters = try numbers(try value(), for: argument)
            case "--r-factor": options.grid.radiusFactor = try numbers(try value(), for: argument)
            case "--min-path": options.grid.minPathMeters = try numbers(try value(), for: argument)
            case "--min-est-area": options.grid.minEstimatedAreaSquareMeters = try numbers(try value(), for: argument)
            case "--run": options.runId = try value()
            case "--out": options.output = try value()
            case "--newcomer": options.newcomer = true
            case "--synthetic": options.synthetic = true
            case "-h", "--help": throw UsageError(description: "")
            default:
                guard !argument.hasPrefix("-"), options.input == nil else {
                    throw UsageError(description: "Неизвестный параметр: \(argument)")
                }
                options.input = argument
            }
        }
        guard options.synthetic || options.input != nil else {
            throw UsageError(description: "Не указан файл «Моих данных».")
        }
        guard !options.grid.settings.isEmpty else {
            throw UsageError(description: "Нет ни одного сочетания: R_мин везде больше R_макс.")
        }
        return options
    }

    static func numbers(_ text: String, for name: String) throws -> [Double] {
        let values = text.split(separator: ",").map { Double($0.trimmingCharacters(in: .whitespaces)) }
        guard !values.isEmpty, values.allSatisfy({ $0.map { $0.isFinite && $0 >= 0 } ?? false }) else {
            throw UsageError(description: "\(name): нужны неотрицательные числа через запятую (дробь — через точку).")
        }
        return values.compactMap { $0 }
    }
}
