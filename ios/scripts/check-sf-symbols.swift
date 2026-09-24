// Каждое имя SF Symbols из кода приложения, расширения и GorodkiKit есть в системе (PLAN.md, §13: «iOS: … SF Symbols»).
// Опечатка в имени — не ошибка сборки: `Image(systemName:)` молча рисует пустое место, и заметно это только глазами.
// Информационный шаг ios-snapshots.yml на раннере macos-26: символы берутся у NSImage той же 26-й системы, что
// и минимальная iOS приложения (26.0), — символа из более новой системы там нет, и проверка его не пропустит.
// Поднимется минимальная iOS — поднять и раннер.
//
// Какие имена находятся:
//  - строки сразу после `systemName:` и `systemImage:` — `Image(systemName: "map")`, `Label(…, systemImage: "flask")`,
//    обе ветви `условие ? "clock" : "xmark.octagon.fill"`;
//  - строки в теле свойства или функции со словом «symbol» в имени (`var symbolName: String { switch … }`).
// Имя из переменной (`Image(systemName: item.symbolName)`) проверить нельзя — такие места перечисляются отдельно.
//
// Запуск: swift ios/scripts/check-sf-symbols.swift [папка ios]
// На macOS — проверка через NSImage; на Linux — только найденные имена (проверить сам разбор). Код выхода 1 — есть
// имена, которых нет в системе (или не нашлось ни одного имени — значит, сломался разбор).

import Foundation

#if canImport(AppKit)
    import AppKit
#endif

let sourceDirectories = ["App", "Widgets", "Packages/GorodkiKit"]

struct Usage {
    let name: String
    let file: String
    let line: Int
}

struct Match {
    let range: Range<String.Index>
    /// Первая группа выражения; `nil` — группы нет или она не совпала.
    let group: Range<String.Index>?
}

/// Совпадения выражения в тексте (или в его части). NSRegularExpression, а не Regex: скрипт запускается
/// `swift файл.swift`, и доступность Regex (macOS 13+) зависела бы от того, какую систему компилятор возьмёт целью.
func matches(of pattern: String, in text: String, within bounds: Range<String.Index>? = nil) -> [Match] {
    guard let regex = try? NSRegularExpression(pattern: pattern) else {
        fatalError("Неверное выражение: \(pattern)")
    }
    let range = NSRange(bounds ?? text.startIndex..<text.endIndex, in: text)
    return regex.matches(in: text, range: range).compactMap { result in
        guard let whole = Range(result.range, in: text) else { return nil }
        let group = result.numberOfRanges > 1 ? Range(result.range(at: 1), in: text) : nil
        return Match(range: whole, group: group)
    }
}

/// Номер строки позиции в тексте (с единицы).
func lineNumber(of index: String.Index, in text: String) -> Int {
    text[..<index].reduce(1) { $1 == "\n" ? $0 + 1 : $0 }
}

/// Строковые литералы без интерполяции (интерполированное имя — всё равно что имя из переменной): текст и позиция.
func literals(in text: String, within bounds: Range<String.Index>) -> [(name: String, at: String.Index)] {
    matches(of: #""([^"\\\n]*)""#, in: text, within: bounds).compactMap { match in
        match.group.map { (String(text[$0]), match.range.lowerBound) }
    }
}

/// Имена после `systemName:` / `systemImage:` — до запятой, скобки или конца строки; литерал может стоять
/// и на следующей строке. Второй список — места, где имя берётся из переменной.
func keywordUsages(in text: String, file: String) -> (found: [Usage], dynamic: [Usage]) {
    var found: [Usage] = []
    var dynamic: [Usage] = []
    for match in matches(of: #"(?:systemName|systemImage):[ \t]*(?:\n[ \t]*)?([^,)\n]*)"#, in: text) {
        guard let expression = match.group else { continue }
        let line = lineNumber(of: match.range.lowerBound, in: text)
        let names = literals(in: text, within: expression)
        if names.isEmpty {
            dynamic.append(Usage(name: text[expression].trimmingCharacters(in: .whitespaces), file: file, line: line))
        }
        found += names.map { Usage(name: $0.name, file: file, line: line) }
    }
    return (found, dynamic)
}

/// Литералы в теле `var …symbol…` / `func …symbol…` — от первой `{` до парной `}`.
func symbolPropertyUsages(in text: String, file: String) -> [Usage] {
    var usages: [Usage] = []
    for match in matches(of: #"(?:var|func)\s+\w*[Ss]ymbol\w*[^{\n]*\{"#, in: text) {
        var depth = 0
        var end = match.range.upperBound
        var index = text.index(before: match.range.upperBound)
        while index < text.endIndex {
            if text[index] == "{" {
                depth += 1
            } else if text[index] == "}" {
                depth -= 1
                if depth == 0 {
                    end = index
                    break
                }
            }
            index = text.index(after: index)
        }
        for literal in literals(in: text, within: match.range.upperBound..<end) {
            usages.append(Usage(name: literal.name, file: file, line: lineNumber(of: literal.at, in: text)))
        }
    }
    return usages
}

let iosRoot = URL(fileURLWithPath: CommandLine.arguments.dropFirst().first ?? "ios", isDirectory: true)
    .standardizedFileURL.resolvingSymlinksInPath()
var usages: [Usage] = []
var dynamicUsages: [Usage] = []
for directory in sourceDirectories {
    let root = iosRoot.appendingPathComponent(directory, isDirectory: true)
    guard let files = FileManager.default.enumerator(at: root, includingPropertiesForKeys: nil) else {
        print("::error::Нет папки \(root.path)")
        exit(1)
    }
    for case let url as URL in files where url.pathExtension == "swift" && !url.path.contains("/.build/") {
        guard let text = try? String(contentsOf: url, encoding: .utf8) else { continue }
        let path = url.standardizedFileURL.resolvingSymlinksInPath().path
        let file = "ios/" + path.replacingOccurrences(of: iosRoot.path + "/", with: "")
        let (found, dynamic) = keywordUsages(in: text, file: file)
        usages += found + symbolPropertyUsages(in: text, file: file)
        dynamicUsages += dynamic
    }
}

let names = Set(usages.map(\.name)).sorted()
guard !names.isEmpty else {
    print("::error::В коде не нашлось ни одного имени SF Symbols — разбор в ios/scripts/check-sf-symbols.swift сломан?")
    exit(1)
}
print("Имён SF Symbols в коде: \(names.count) — \(names.joined(separator: ", "))")
for usage in dynamicUsages.sorted(by: { ($0.file, $0.line) < ($1.file, $1.line) }) {
    print("  не проверено, имя из кода: \(usage.file):\(usage.line): \(usage.name)")
}

#if canImport(AppKit)
    let missing = usages.filter { NSImage(systemSymbolName: $0.name, accessibilityDescription: nil) == nil }
    for usage in missing.sorted(by: { ($0.file, $0.line) < ($1.file, $1.line) }) {
        // Формат ::error file=…,line=… — GitHub покажет ошибку прямо на строке в PR.
        print("::error file=\(usage.file),line=\(usage.line)::Нет символа SF Symbols «\(usage.name)»")
    }
    let version = ProcessInfo.processInfo.operatingSystemVersionString
    if missing.isEmpty {
        print("Все \(names.count) имён есть в SF Symbols этой системы (macOS \(version)).")
    } else {
        print("Нет в SF Symbols этой системы (macOS \(version)): \(Set(missing.map(\.name)).sorted())")
        exit(1)
    }
#else
    print("Проверки через NSImage нет: это не macOS. Здесь проверяется только разбор кода.")
#endif
