// Разбор записанного забега, половина телефона. Как запускать — docs/guides/calibration-replay.md.

import Foundation
import LoopReplayKit

func printError(_ text: String) {
    FileHandle.standardError.write(Data((text + "\n").utf8))
}

do {
    let options = try ReplayOptions.parse(Array(CommandLine.arguments.dropFirst()))
    let json: Data
    if options.synthetic {
        json = try LoopReplay.encode(SyntheticRuns.myData())
    } else if let input = options.input {
        let url = URL(fileURLWithPath: input)
        let data = try JSONDecoder().decode(MyData.self, from: Data(contentsOf: url))
        let output = LoopReplay.run(
            data, input: url.lastPathComponent, detectors: options.grid.settings, newcomer: options.newcomer,
            runId: options.runId)
        if output.runs.isEmpty {
            printError(options.runId.map { "Забега \($0) в файле нет." } ?? "В файле нет забегов.")
            exit(1)
        }
        for run in output.runs {
            let found = run.variants.map { String($0.loops.count) }.joined(separator: "/")
            printError(
                "Забег \(run.id): точек \(run.points.count), принято \(run.judge.accepted), "
                    + "разрывов \(run.judge.breaks), петель по вариантам: \(found)")
        }
        json = try LoopReplay.encode(output)
    } else {
        throw UsageError(description: "Не указан файл «Моих данных».")
    }
    if let path = options.output {
        try json.write(to: URL(fileURLWithPath: path), options: .atomic)
    } else {
        FileHandle.standardOutput.write(json)
    }
} catch let error as UsageError {
    printError(error.description.isEmpty ? ReplayOptions.usage : "\(error)\n\n\(ReplayOptions.usage)")
    exit(error.description.isEmpty ? 0 : 2)
} catch {
    printError("Ошибка: \(error)")
    exit(1)
}
